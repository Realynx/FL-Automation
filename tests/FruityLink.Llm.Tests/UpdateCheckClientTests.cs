using System.Net;
using System.Net.Http;
using FruityLink.Core.Configuration;
using FruityLink.Llm.Auth;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

/// <summary>
/// The public update check (<c>GET /v1/client-version</c>). The contract is "null unless there is
/// definitely a newer release": every failure mode — offline, non-2xx, nothing published,
/// unparseable versions — collapses to a quiet null so an update check can never bother the user.
/// </summary>
public sealed class UpdateCheckClientTests
{
    private static (UpdateCheckClient Client, FakeHttpMessageHandler Http) Build(FakeHttpMessageHandler http)
    {
        var settings = new InMemorySettingsStore
        {
            Current = new AppSettings
            {
                Account = new AccountSettings(GatewayBaseUrl: "https://gw.test"),
            },
        };
        return (new UpdateCheckClient(new HttpClient(http), settings), http);
    }

    private static (UpdateCheckClient Client, FakeHttpMessageHandler Http) BuildJson(
        string body, HttpStatusCode status = HttpStatusCode.OK)
        => Build(FakeHttpMessageHandler.Json(status, body));

    [Fact]
    public async Task RemoteNewer_ReturnsVerbatimVersionAndUrl()
    {
        var (client, _) = BuildJson("""{"version":"1.5.0","downloadUrl":"https://fl-automate.com/dl/1.5.0"}""");

        UpdateInfo? info = await client.CheckAsync("1.4.2");

        info.ShouldNotBeNull();
        info.Value.Version.ShouldBe("1.5.0");
        info.Value.DownloadUrl.ShouldBe("https://fl-automate.com/dl/1.5.0");
    }

    [Fact]
    public async Task RemoteEqual_ReturnsNull()
    {
        var (client, _) = BuildJson("""{"version":"1.4.2","downloadUrl":"https://fl-automate.com/dl"}""");
        (await client.CheckAsync("1.4.2")).ShouldBeNull();
    }

    [Fact]
    public async Task RemoteOlder_ReturnsNull()
    {
        var (client, _) = BuildJson("""{"version":"1.4.1","downloadUrl":"https://fl-automate.com/dl"}""");
        (await client.CheckAsync("1.4.2")).ShouldBeNull();
    }

    [Fact]
    public async Task VersionNullInBody_ReturnsNull_ButStillMadeTheRequest()
    {
        var (client, http) = BuildJson("""{"version":null,"downloadUrl":"https://fl-automate.com/dl"}""");

        (await client.CheckAsync("1.4.2")).ShouldBeNull();

        http.RequestCount.ShouldBe(1);   // "nothing published" is a server answer, not a skipped check
    }

    [Fact]
    public async Task VPrefixedRemote_IsDetected_AndKeptVerbatim()
    {
        var (client, _) = BuildJson("""{"version":"v2.0.0","downloadUrl":"https://fl-automate.com/dl"}""");

        UpdateInfo? info = await client.CheckAsync("1.9.9");

        info.ShouldNotBeNull();
        info.Value.Version.ShouldBe("v2.0.0");   // raw string as sent — display only
    }

    [Fact]
    public async Task ComponentCountMismatch_IsNormalized_NotMissedAsOlder()
    {
        // Raw System.Version says "1.4" < "1.3.9" is false but "1.4" < "1.4.0" is TRUE
        // (missing components compare as -1); the client pads to avoid that trap.
        var (client, _) = BuildJson("""{"version":"1.4","downloadUrl":"https://fl-automate.com/dl"}""");

        UpdateInfo? info = await client.CheckAsync("1.3.9");

        info.ShouldNotBeNull();
        info.Value.Version.ShouldBe("1.4");
    }

    [Fact]
    public async Task PaddedEquality_TwoSpellingsOfSameRelease_ReturnsNull()
    {
        var (client, _) = BuildJson("""{"version":"1.4.0","downloadUrl":"https://fl-automate.com/dl"}""");
        (await client.CheckAsync("1.4")).ShouldBeNull();
    }

    [Fact]
    public async Task UnparseableRemoteVersion_ReturnsNull()
    {
        var (client, _) = BuildJson("""{"version":"latest-and-greatest","downloadUrl":"https://fl-automate.com/dl"}""");
        (await client.CheckAsync("1.4.2")).ShouldBeNull();
    }

    [Fact]
    public async Task MissingDownloadUrl_FallsBackToAccountPage()
    {
        var (client, _) = BuildJson("""{"version":"9.9.9"}""");

        UpdateInfo? info = await client.CheckAsync("1.0.0");

        info.ShouldNotBeNull();
        info.Value.DownloadUrl.ShouldBe("https://fl-automate.com/account");
    }

    [Fact]
    public async Task MalformedJson_ReturnsNull()
    {
        var (client, _) = BuildJson("not json {");
        (await client.CheckAsync("1.4.2")).ShouldBeNull();
    }

    [Fact]
    public async Task NonSuccessStatus_ReturnsNull()
    {
        var (client, _) = BuildJson("""{"version":"9.9.9","downloadUrl":"https://fl-automate.com/dl"}""", HttpStatusCode.ServiceUnavailable);
        (await client.CheckAsync("1.4.2")).ShouldBeNull();
    }

    [Fact]
    public async Task TransportFailure_ReturnsNull()
    {
        var (client, _) = Build(FakeHttpMessageHandler.Throws(new HttpRequestException("offline")));
        (await client.CheckAsync("1.4.2")).ShouldBeNull();
    }

    [Fact]
    public async Task Request_IsAnUnauthenticatedGet_ToClientVersionRoute()
    {
        var (client, http) = BuildJson("""{"version":"1.5.0","downloadUrl":"https://fl-automate.com/dl"}""");

        await client.CheckAsync("1.4.2");

        http.LastRequest!.Method.ShouldBe(HttpMethod.Get);
        http.LastRequest.RequestUri!.ToString().ShouldBe("https://gw.test/v1/client-version");
        http.LastRequest.Headers.Authorization.ShouldBeNull();   // public endpoint — never send a token
    }
}
