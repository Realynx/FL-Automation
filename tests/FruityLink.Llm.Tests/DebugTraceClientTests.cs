using System.Net;
using System.Net.Http;
using System.Text.Json;
using FruityLink.Core.Configuration;
using FruityLink.Llm.Auth;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

/// <summary>
/// The opt-in debug-trace uploader. The FIRST test is the load-bearing one: with the opt-in OFF
/// (the default) the client must produce ZERO network traffic — that's the product's privacy
/// promise, not an optimization.
/// </summary>
public sealed class DebugTraceClientTests
{
    private const string TurnJson = """{"session":"abc123","turn":"t1","status":"ok","tool_calls":2}""";

    private static (DebugTraceClient Client, FakeHttpMessageHandler Http, InMemorySettingsStore Settings, FakeAccountAuth Auth)
        Build(bool optedIn, HttpStatusCode status = HttpStatusCode.Created)
    {
        var http = FakeHttpMessageHandler.Json(status, """{"id":"dt_1"}""");
        var settings = new InMemorySettingsStore
        {
            Current = new AppSettings
            {
                Account = new AccountSettings(GatewayBaseUrl: "https://gw.test") { ShareDebugData = optedIn },
            },
        };
        var auth = new FakeAccountAuth();
        return (new DebugTraceClient(new HttpClient(http), auth, settings), http, settings, auth);
    }

    [Fact]
    public async Task OptedOut_SendsNothing_AndReturnsFalse()
    {
        var (client, http, _, _) = Build(optedIn: false);

        bool sent = await client.TrySendTurnAsync(TurnJson);

        sent.ShouldBeFalse();
        http.RequestCount.ShouldBe(0);   // the privacy gate: no request may even be attempted
    }

    [Fact]
    public async Task SignedOut_SendsNothing_EvenWhenOptedIn()
    {
        var (client, http, _, auth) = Build(optedIn: true);
        auth.AccessToken = null;

        bool sent = await client.TrySendTurnAsync(TurnJson);

        sent.ShouldBeFalse();
        http.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task OptedIn_PostsTurnEnvelope_WithBearer_ToDebugTracesRoute()
    {
        var (client, http, _, _) = Build(optedIn: true);

        bool sent = await client.TrySendTurnAsync(TurnJson);

        sent.ShouldBeTrue();
        http.RequestCount.ShouldBe(1);
        http.LastRequest!.RequestUri!.ToString().ShouldBe("https://gw.test/v1/debug-traces");
        http.LastRequest.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        http.LastRequest.Headers.Authorization.Parameter.ShouldBe("token-1");

        using JsonDocument body = JsonDocument.Parse(http.RequestBodies[0]!);
        body.RootElement.GetProperty("turn").GetProperty("session").GetString().ShouldBe("abc123");
        body.RootElement.GetProperty("turn").GetProperty("tool_calls").GetInt32().ShouldBe(2);
        body.RootElement.TryGetProperty("clientVersion", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task GatewayRejection_IsAQuietFalse()
    {
        var (client, _, _, _) = Build(optedIn: true, status: HttpStatusCode.InternalServerError);
        (await client.TrySendTurnAsync(TurnJson)).ShouldBeFalse();
    }

    [Fact]
    public async Task MalformedTurnJson_IsAQuietFalse_WithNoRequest()
    {
        var (client, http, _, _) = Build(optedIn: true);
        (await client.TrySendTurnAsync("not json {")).ShouldBeFalse();
        http.RequestCount.ShouldBe(0);
    }

    [Fact]
    public async Task TransportFailure_IsAQuietFalse()
    {
        var settings = new InMemorySettingsStore
        {
            Current = new AppSettings
            {
                Account = new AccountSettings(GatewayBaseUrl: "https://gw.test") { ShareDebugData = true },
            },
        };
        var client = new DebugTraceClient(
            new HttpClient(FakeHttpMessageHandler.Throws(new HttpRequestException("offline"))),
            new FakeAccountAuth(), settings);

        (await client.TrySendTurnAsync(TurnJson)).ShouldBeFalse();
    }
}
