using System.Net;
using System.Net.Http;
using FruityLink.Llm.Auth;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

public sealed class GatewayConnectivityTests
{
    private static GatewayConnectivity Make(FakeHttpMessageHandler handler, FakeAccountAuth? auth = null)
        => new(new HttpClient(handler), auth ?? new FakeAccountAuth());

    [Fact]
    public async Task ListModels_sends_the_bearer_token_and_parses_ids_with_display_names()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
        {"object":"list","data":[
            {"id":"swift-1","object":"model","owned_by":"fl-automate","display_name":"FL Swift"},
            {"id":"deep-9","object":"model","owned_by":"fl-automate","display_name":"FL Deep"}]}
        """);
        var auth = new FakeAccountAuth { AccessToken = "live-token" };

        var models = await Make(handler, auth).ListModelsAsync("https://ai.fl-automate.com/");

        models.ShouldBe(new[] { new GatewayModel("swift-1", "FL Swift"), new GatewayModel("deep-9", "FL Deep") });
        handler.LastRequest!.RequestUri!.ToString().ShouldBe("https://ai.fl-automate.com/v1/models");
        handler.LastRequest.Headers.Authorization!.Parameter.ShouldBe("live-token");
    }

    [Fact]
    public async Task ListModels_falls_back_to_the_id_when_display_name_is_missing_or_blank()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, """
        {"object":"list","data":[
            {"id":"bare-model","object":"model"},
            {"id":"blank-name","object":"model","display_name":"  "},
            {"object":"model","display_name":"no id at all"}]}
        """);

        var models = await Make(handler).ListModelsAsync("https://ai.fl-automate.com");

        // Entries without an id are skipped rather than failing the whole list.
        models.ShouldBe(new[] { new GatewayModel("bare-model", "bare-model"), new GatewayModel("blank-name", "blank-name") });
    }

    [Fact]
    public async Task ListModels_returns_an_empty_list_for_an_unconfigured_plan()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, """{"object":"list","data":[]}""");

        var models = await Make(handler).ListModelsAsync("https://ai.fl-automate.com");

        models.ShouldBeEmpty();   // valid answer — the UI shows just the "Auto" sentinel
    }

    [Fact]
    public async Task ListModels_maps_a_garbage_body_to_a_friendly_message()
    {
        var handler = FakeHttpMessageHandler.Json(HttpStatusCode.OK, "<html>definitely not json</html>");

        var ex = await Should.ThrowAsync<AccountAuthException>(
            () => Make(handler).ListModelsAsync("https://ai.fl-automate.com"));

        ex.Message.ShouldContain("not with a model list");
    }

    [Fact]
    public async Task ListModels_when_logged_out_asks_the_user_to_sign_in()
    {
        var handler = FakeHttpMessageHandler.Throws(new InvalidOperationException("no wire expected"));
        var auth = new FakeAccountAuth { AccessToken = null };

        var ex = await Should.ThrowAsync<AccountAuthException>(
            () => Make(handler, auth).ListModelsAsync("https://ai.fl-automate.com"));

        ex.Message.ShouldContain("Not signed in");
        handler.RequestCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "sign in again")]
    [InlineData(HttpStatusCode.PaymentRequired, "quota")]
    [InlineData(HttpStatusCode.Forbidden, "subscription is inactive")]
    public async Task ListModels_maps_auth_statuses_to_friendly_messages(HttpStatusCode status, string fragment)
    {
        var handler = FakeHttpMessageHandler.Json(status, """{"error":"x"}""");

        var ex = await Should.ThrowAsync<AccountAuthException>(
            () => Make(handler).ListModelsAsync("https://ai.fl-automate.com"));

        ex.Message.ShouldContain(fragment, Case.Insensitive);
    }

    [Fact]
    public async Task ListModels_maps_network_failure_to_a_friendly_message()
    {
        var handler = FakeHttpMessageHandler.Throws(new HttpRequestException("refused"));

        var ex = await Should.ThrowAsync<AccountAuthException>(
            () => Make(handler).ListModelsAsync("http://localhost:9"));

        ex.Message.ShouldContain("Couldn't reach the AI gateway");
    }
}
