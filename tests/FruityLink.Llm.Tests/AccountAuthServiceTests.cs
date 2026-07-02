using System.Net;
using System.Net.Http;
using System.Text.Json;
using FruityLink.Core.Configuration;
using FruityLink.Llm.Auth;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

public sealed class AccountAuthServiceTests
{
    private static AccountAuthService Make(
        FakeHttpMessageHandler handler,
        out InMemorySettingsStore settings,
        out InMemorySecretStore secrets)
    {
        settings = new InMemorySettingsStore();
        secrets = new InMemorySecretStore();
        return new AccountAuthService(new HttpClient(handler), settings, secrets);
    }

    // ── token decode ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_reads_claims_and_expiry_from_the_payload()
    {
        string jwt = AuthTestSupport.MakeJwt(
            email: "a@b.c", tier: "studio", plan: "beta", subActive: true, expiresIn: TimeSpan.FromMinutes(15));

        AccountSession? s = AccessTokenPayload.Decode(jwt);

        s.ShouldNotBeNull();
        s.Email.ShouldBe("a@b.c");
        s.Tier.ShouldBe("studio");
        s.Plan.ShouldBe("beta");
        s.SubActive.ShouldBeTrue();
        s.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(14));
        s.ExpiresAt.ShouldBeLessThan(DateTimeOffset.UtcNow.AddMinutes(16));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.one")]        // second segment is "one" → not valid JSON once decoded
    [InlineData("a.!!!!.c")]        // invalid base64url payload
    public void Decode_returns_null_for_malformed_tokens(string? jwt)
        => AccessTokenPayload.Decode(jwt).ShouldBeNull();

    // ── login ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_stores_refresh_token_and_display_identity_and_reports_session()
    {
        string access = AuthTestSupport.MakeJwt(email: "fox@example.com", plan: "pro");
        var handler = FakeHttpMessageHandler.Json(
            HttpStatusCode.OK, AuthTestSupport.TokenPairJson(access, "refresh-1"));
        AccountAuthService auth = Make(handler, out var settings, out var secrets);

        AccountSession session = await auth.LoginAsync("fox@example.com", "hunter2");

        session.Email.ShouldBe("fox@example.com");
        auth.IsLoggedIn.ShouldBeTrue();
        secrets.Secrets[AccountAuthService.RefreshTokenSecretName].ShouldBe("refresh-1");
        settings.Current.AccountOrDefault.Email.ShouldBe("fox@example.com");
        settings.Current.AccountOrDefault.Plan.ShouldBe("pro");
        (await auth.GetAccessTokenAsync()).ShouldBe(access);

        // Exact wire shape: {"email","password"} only (the API rejects unknown fields).
        using JsonDocument body = JsonDocument.Parse(handler.RequestBodies[0]!);
        body.RootElement.EnumerateObject().Count().ShouldBe(2);
        body.RootElement.GetProperty("email").GetString().ShouldBe("fox@example.com");
        body.RootElement.GetProperty("password").GetString().ShouldBe("hunter2");
        handler.Requests[0].RequestUri!.ToString().ShouldBe("https://fl-automate.com/api/auth/login");
    }

    [Fact]
    public async Task Login_maps_401_to_invalid_credentials()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, """{"error":"invalid_credentials"}""");
        AccountAuthService auth = Make(handler, out _, out _);

        var ex = await Should.ThrowAsync<AccountAuthException>(() => auth.LoginAsync("a@b.c", "wrong"));

        ex.Message.ShouldBe("Invalid email or password.");
        auth.IsLoggedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task Login_maps_403_to_the_servers_verify_email_message()
    {
        var handler = FakeHttpMessageHandler.Json(
            HttpStatusCode.Forbidden, """{"error":"Email not verified. Check your inbox."}""");
        AccountAuthService auth = Make(handler, out _, out _);

        var ex = await Should.ThrowAsync<AccountAuthException>(() => auth.LoginAsync("a@b.c", "pw"));

        ex.Message.ShouldBe("Email not verified. Check your inbox.");
    }

    [Fact]
    public async Task Login_maps_network_failure_to_a_friendly_message()
    {
        var handler = FakeHttpMessageHandler.Throws(new HttpRequestException("no route to host"));
        AccountAuthService auth = Make(handler, out _, out _);

        var ex = await Should.ThrowAsync<AccountAuthException>(() => auth.LoginAsync("a@b.c", "pw"));

        ex.Message.ShouldContain("internet connection");
    }

    // ── access-token caching + proactive refresh ─────────────────────────────────────────────

    [Fact]
    public async Task GetAccessToken_returns_cached_token_without_a_network_call_while_fresh()
    {
        string access = AuthTestSupport.MakeJwt(expiresIn: TimeSpan.FromMinutes(15));
        var handler = FakeHttpMessageHandler.Json(
            HttpStatusCode.OK, AuthTestSupport.TokenPairJson(access, "refresh-1"));
        AccountAuthService auth = Make(handler, out _, out _);
        await auth.LoginAsync("a@b.c", "pw");
        handler.RequestCount.ShouldBe(1);

        (await auth.GetAccessTokenAsync()).ShouldBe(access);
        (await auth.GetAccessTokenAsync()).ShouldBe(access);

        handler.RequestCount.ShouldBe(1);   // login only — no refresh traffic
    }

    [Fact]
    public async Task GetAccessToken_refreshes_proactively_when_under_two_minutes_to_expiry()
    {
        string dying = AuthTestSupport.MakeJwt(expiresIn: TimeSpan.FromSeconds(60));   // < 2-min skew
        string fresh = AuthTestSupport.MakeJwt(expiresIn: TimeSpan.FromMinutes(15));
        var handler = FakeHttpMessageHandler.From(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/auth/login")
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, AuthTestSupport.TokenPairJson(dying, "refresh-1"))
                : FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, AuthTestSupport.TokenPairJson(fresh, "refresh-2")));
        AccountAuthService auth = Make(handler, out _, out var secrets);
        await auth.LoginAsync("a@b.c", "pw");

        string? token = await auth.GetAccessTokenAsync();

        token.ShouldBe(fresh);
        handler.Requests[^1].RequestUri!.AbsolutePath.ShouldEndWith("/auth/refresh");
        // ROTATION: the new refresh token replaced the old one.
        secrets.Secrets[AccountAuthService.RefreshTokenSecretName].ShouldBe("refresh-2");
    }

    [Fact]
    public async Task Refresh_rotation_chains_each_refresh_sends_the_previous_response_token()
    {
        string dying = AuthTestSupport.MakeJwt(expiresIn: TimeSpan.FromSeconds(30));
        int refreshCount = 0;
        var handler = FakeHttpMessageHandler.From(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth/login"))
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, AuthTestSupport.TokenPairJson(dying, "refresh-1"));
            refreshCount++;
            // Keep handing out short-lived tokens so every Get triggers another rotation.
            return FakeHttpMessageHandler.JsonResponse(
                HttpStatusCode.OK, AuthTestSupport.TokenPairJson(dying, $"refresh-{refreshCount + 1}"));
        });
        AccountAuthService auth = Make(handler, out _, out var secrets);
        await auth.LoginAsync("a@b.c", "pw");

        await auth.GetAccessTokenAsync();   // rotation 1: sends refresh-1, stores refresh-2
        await auth.GetAccessTokenAsync();   // rotation 2: sends refresh-2, stores refresh-3

        string[] refreshBodiesSent = handler.RequestBodies
            .Where((_, i) => handler.Requests[i].RequestUri!.AbsolutePath.EndsWith("/auth/refresh"))
            .Select(b => JsonDocument.Parse(b!).RootElement.GetProperty("refreshToken").GetString()!)
            .ToArray();
        refreshBodiesSent.ShouldBe(new[] { "refresh-1", "refresh-2" });
        secrets.Secrets[AccountAuthService.RefreshTokenSecretName].ShouldBe("refresh-3");
    }

    [Fact]
    public async Task Concurrent_token_requests_single_flight_one_refresh()
    {
        string dying = AuthTestSupport.MakeJwt(expiresIn: TimeSpan.FromSeconds(30));
        string fresh = AuthTestSupport.MakeJwt(expiresIn: TimeSpan.FromMinutes(15));
        int refreshes = 0;
        var handler = FakeHttpMessageHandler.From(req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth/login"))
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, AuthTestSupport.TokenPairJson(dying, "refresh-1"));
            Interlocked.Increment(ref refreshes);
            return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, AuthTestSupport.TokenPairJson(fresh, "refresh-2"));
        });
        AccountAuthService auth = Make(handler, out _, out _);
        await auth.LoginAsync("a@b.c", "pw");

        string?[] tokens = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => auth.GetAccessTokenAsync()));

        tokens.ShouldAllBe(t => t == fresh);
        refreshes.ShouldBe(1);
    }

    // ── restore + rejection ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TryRestore_brings_a_stored_refresh_token_back_to_a_live_session()
    {
        string access = AuthTestSupport.MakeJwt(email: "fox@example.com", plan: "studio");
        var handler = FakeHttpMessageHandler.Json(
            HttpStatusCode.OK, AuthTestSupport.TokenPairJson(access, "refresh-2"));
        AccountAuthService auth = Make(handler, out var settings, out var secrets);
        secrets.Secrets[AccountAuthService.RefreshTokenSecretName] = "refresh-1";

        bool restored = await auth.TryRestoreAsync();

        restored.ShouldBeTrue();
        auth.IsLoggedIn.ShouldBeTrue();
        auth.Session!.Email.ShouldBe("fox@example.com");
        secrets.Secrets[AccountAuthService.RefreshTokenSecretName].ShouldBe("refresh-2");
        settings.Current.AccountOrDefault.Plan.ShouldBe("studio");
    }

    [Fact]
    public async Task TryRestore_with_no_stored_token_stays_logged_out_without_network()
    {
        var handler = FakeHttpMessageHandler.Throws(new InvalidOperationException("no requests expected"));
        AccountAuthService auth = Make(handler, out _, out _);

        (await auth.TryRestoreAsync()).ShouldBeFalse();
        auth.IsLoggedIn.ShouldBeFalse();
        handler.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task Rejected_refresh_clears_the_dead_token_and_display_identity()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, """{"error":"revoked"}""");
        AccountAuthService auth = Make(handler, out var settings, out var secrets);
        secrets.Secrets[AccountAuthService.RefreshTokenSecretName] = "revoked-token";
        settings.Current = new AppSettings(Account: new AccountSettings { Email = "old@x.y", Plan = "pro" });

        (await auth.TryRestoreAsync()).ShouldBeFalse();

        auth.IsLoggedIn.ShouldBeFalse();
        secrets.Secrets.ShouldNotContainKey(AccountAuthService.RefreshTokenSecretName);
        settings.Current.AccountOrDefault.Email.ShouldBeNull();   // card stops claiming "signed in"
    }

    // ── logout ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Logout_revokes_clears_secrets_and_identity_and_survives_network_failure()
    {
        string access = AuthTestSupport.MakeJwt();
        bool loginDone = false;
        var handler = FakeHttpMessageHandler.From(req =>
        {
            if (!loginDone && req.RequestUri!.AbsolutePath.EndsWith("/auth/login"))
            {
                loginDone = true;
                return FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, AuthTestSupport.TokenPairJson(access, "refresh-1"));
            }
            throw new HttpRequestException("network down during logout");   // revoke fails
        });
        AccountAuthService auth = Make(handler, out var settings, out var secrets);
        await auth.LoginAsync("a@b.c", "pw");

        await auth.LogoutAsync();   // must not throw

        auth.IsLoggedIn.ShouldBeFalse();
        (await auth.GetAccessTokenAsync()).ShouldBeNull();
        secrets.Secrets.ShouldNotContainKey(AccountAuthService.RefreshTokenSecretName);
        settings.Current.AccountOrDefault.Email.ShouldBeNull();
    }
}
