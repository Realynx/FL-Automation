using System.Net;
using System.Net.Http;
using FruityLink.Llm.Auth;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

public sealed class GatewayAuthHandlerTests
{
    private static HttpClient Make(FakeHttpMessageHandler inner, FakeAccountAuth auth)
        => new(new GatewayAuthHandler(auth, inner));

    private static HttpRequestMessage ChatRequest() => new(HttpMethod.Post, "https://ai.fl-automate.com/v1/chat/completions")
    {
        Content = new StringContent("""{"model":"default","messages":[]}""", System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Stamps_the_bearer_token_overwriting_the_placeholder()
    {
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}");
        var auth = new FakeAccountAuth { AccessToken = "live-token" };
        using HttpClient http = Make(inner, auth);
        using var request = ChatRequest();
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fl-automate");

        using HttpResponseMessage response = await http.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        inner.LastRequest!.Headers.Authorization!.Parameter.ShouldBe("live-token");
    }

    [Fact]
    public async Task On_401_refreshes_once_and_retries_with_the_new_token()
    {
        int calls = 0;
        var inner = FakeHttpMessageHandler.From(req =>
        {
            calls++;
            return req.Headers.Authorization!.Parameter == "fresh-token"
                ? FakeHttpMessageHandler.JsonResponse(HttpStatusCode.OK, """{"ok":true}""")
                : FakeHttpMessageHandler.JsonResponse(HttpStatusCode.Unauthorized, """{"error":"expired"}""");
        });
        var auth = new FakeAccountAuth { AccessToken = "stale-token", RefreshedToken = "fresh-token" };
        using HttpClient http = Make(inner, auth);

        using HttpResponseMessage response = await http.SendAsync(ChatRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        calls.ShouldBe(2);                    // original + exactly one retry
        auth.RefreshCalls.ShouldBe(1);
        // The retry carried an identical body (buffered up front, resent as a clone).
        inner.RequestBodies[1].ShouldBe(inner.RequestBodies[0]);
    }

    [Fact]
    public async Task A_second_401_after_the_refresh_maps_to_a_sign_in_again_error_not_a_loop()
    {
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, """{"error":"expired"}""");
        var auth = new FakeAccountAuth { AccessToken = "stale", RefreshedToken = "still-rejected" };
        using HttpClient http = Make(inner, auth);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => http.SendAsync(ChatRequest()));

        ex.Message.ShouldContain("sign in again");
        inner.RequestCount.ShouldBe(2);       // one refresh, one retry — never a third attempt
        auth.RefreshCalls.ShouldBe(1);
    }

    [Fact]
    public async Task When_the_refresh_fails_the_401_maps_without_a_retry()
    {
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, """{"error":"expired"}""");
        var auth = new FakeAccountAuth { AccessToken = "stale", RefreshedToken = null };   // refresh → null
        using HttpClient http = Make(inner, auth);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => http.SendAsync(ChatRequest()));

        ex.Message.ShouldContain("sign in again");
        inner.RequestCount.ShouldBe(1);       // no retry without a new token
    }

    [Fact]
    public async Task Maps_402_to_the_quota_message()
    {
        var inner = FakeHttpMessageHandler.Json(
            HttpStatusCode.PaymentRequired, """{"error":"quota_exceeded","used":1000000}""");
        using HttpClient http = Make(inner, new FakeAccountAuth());

        var ex = await Should.ThrowAsync<HttpRequestException>(() => http.SendAsync(ChatRequest()));

        ex.Message.ShouldContain("Monthly AI token quota reached for your plan");
    }

    [Fact]
    public async Task Maps_403_to_the_subscription_inactive_message()
    {
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.Forbidden, """{"error":"subscription_inactive"}""");
        using HttpClient http = Make(inner, new FakeAccountAuth());

        var ex = await Should.ThrowAsync<HttpRequestException>(() => http.SendAsync(ChatRequest()));

        ex.Message.ShouldContain("Subscription inactive");
    }

    [Fact]
    public async Task Logged_out_requests_fail_fast_with_a_sign_in_message_without_hitting_the_wire()
    {
        var inner = FakeHttpMessageHandler.Throws(new InvalidOperationException("must not be reached"));
        using HttpClient http = Make(inner, new FakeAccountAuth { AccessToken = null, RefreshedToken = null });

        var ex = await Should.ThrowAsync<HttpRequestException>(() => http.SendAsync(ChatRequest()));

        ex.Message.ShouldContain("sign in to your FL Automate account");
        inner.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task Maps_503_no_backend_configured_preferring_the_server_message()
    {
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.ServiceUnavailable,
            """{"error":"no_backend_configured","message":"Your plan's AI backend is being provisioned."}""");
        using HttpClient http = Make(inner, new FakeAccountAuth());

        var ex = await Should.ThrowAsync<HttpRequestException>(() => http.SendAsync(ChatRequest()));

        ex.Message.ShouldBe("Your plan's AI backend is being provisioned.");
    }

    [Fact]
    public async Task Maps_503_no_backend_configured_without_a_message_to_the_default_text()
    {
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.ServiceUnavailable,
            """{"error":"no_backend_configured"}""");
        using HttpClient http = Make(inner, new FakeAccountAuth());

        var ex = await Should.ThrowAsync<HttpRequestException>(() => http.SendAsync(ChatRequest()));

        ex.Message.ShouldContain("isn't configured for your plan yet");
    }

    [Fact]
    public async Task An_unrelated_503_passes_through_for_the_retry_layer()
    {
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.ServiceUnavailable,
            """{"error":"upstream_restarting"}""");
        using HttpClient http = Make(inner, new FakeAccountAuth());

        using HttpResponseMessage response = await http.SendAsync(ChatRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);   // not our error to translate
    }

    [Fact]
    public async Task Success_and_ordinary_errors_pass_through_untouched()
    {
        var inner = FakeHttpMessageHandler.Json(HttpStatusCode.InternalServerError, """{"error":"boom"}""");
        using HttpClient http = Make(inner, new FakeAccountAuth());

        using HttpResponseMessage response = await http.SendAsync(ChatRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);   // retry layer's job, not ours
    }
}
