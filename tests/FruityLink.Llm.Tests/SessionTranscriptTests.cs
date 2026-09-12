using System.Text.Json;
using FruityLink.Llm.Diagnostics;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

/// <summary>
/// The per-turn structured transcript is the data the tool-surface review runs on, so its shape is
/// pinned here: a well-formed one-line JSON record with the model requested vs. used, the ordered
/// tool-call sequence, token totals, repairs, reasoning, and status — plus the ambient-scope
/// mechanics (nesting for sub-agents) it shares with <see cref="LlmTurnBudget"/>. The file WRITE is
/// suppressed under the test host, so these assert the serialized string via the internal seam.
/// </summary>
public sealed class SessionTranscriptTests
{
    private static JsonElement SerializeAndParse(TurnScope scope) =>
        JsonDocument.Parse(SessionTranscriptWriter.Serialize(scope)).RootElement;

    [Fact]
    public void FullTurn_SerializesTheReviewableShape()
    {
        var scope = new TurnScope("sess1", modelRequested: "deepseek", prompt: "make the bass punchier",
            advertisedTools: 12, streamed: false);
        scope.RecordRequest("glm-4.6", promptTokens: 100, completionTokens: 20, requestBytes: 5000, toolsBytes: 800);
        scope.RecordTool("native_list_channels", "", "OK: 3 channels", ms: 5);
        scope.RecordTool("native_set_channel_name", "index=2, name=Bass", "OK: channel 2 = 'Bass'", ms: 12);
        scope.SetReasonings(new[] { "list channels first to find the bass" });
        scope.AddRepairs(new[] { new KeyValuePair<string, int>("ReasoningFold", 1) });
        scope.Finish("ok", wasCapped: false, "final reasoning", "Done — renamed it.", error: null);

        JsonElement root = SerializeAndParse(scope);

        root.GetProperty("turn").GetString().ShouldNotBeNullOrEmpty();
        root.GetProperty("session").GetString().ShouldBe("sess1");
        root.GetProperty("model_requested").GetString().ShouldBe("deepseek");
        root.GetProperty("model_used").GetString().ShouldBe("glm-4.6");
        root.GetProperty("prompt").GetString().ShouldBe("make the bass punchier");
        root.GetProperty("advertised_tools").GetInt32().ShouldBe(12);
        root.GetProperty("status").GetString().ShouldBe("ok");
        root.GetProperty("capped").GetBoolean().ShouldBeFalse();
        root.GetProperty("tool_calls").GetInt32().ShouldBe(2);
        root.GetProperty("final_text").GetString().ShouldBe("Done — renamed it.");

        JsonElement tokens = root.GetProperty("tokens");
        tokens.GetProperty("prompt").GetInt64().ShouldBe(100);
        tokens.GetProperty("completion").GetInt64().ShouldBe(20);
        tokens.GetProperty("total").GetInt64().ShouldBe(120);

        JsonElement calls = root.GetProperty("calls");
        calls.GetArrayLength().ShouldBe(2);
        calls[0].GetProperty("name").GetString().ShouldBe("native_list_channels");
        calls[0].GetProperty("seq").GetInt32().ShouldBe(1);
        calls[1].GetProperty("name").GetString().ShouldBe("native_set_channel_name");
        calls[1].GetProperty("result").GetString().ShouldBe("OK: channel 2 = 'Bass'");

        root.GetProperty("repairs").GetProperty("ReasoningFold").GetInt32().ShouldBe(1);
        root.GetProperty("reasoning")[0].GetString().ShouldBe("list channels first to find the bass");
    }

    [Fact]
    public void StreamedTurn_WithNoRecordedUsage_ReportsNullTokens_AndDerivesRounds()
    {
        // The main UI streams; the HTTP layer records no usage on SSE. Tokens must serialize as null
        // (an honest "unavailable"), and the round count falls back to reasonings + the answer round.
        var scope = new TurnScope("s", "glm", "hi", advertisedTools: -1, streamed: true);
        scope.SetModelUsed("glm-4.6");
        scope.SetReasonings(new[] { "round-1 thinking", "round-2 thinking" });
        scope.Finish("ok", false, "", "hello", null);

        JsonElement root = SerializeAndParse(scope);

        root.GetProperty("tokens").ValueKind.ShouldBe(JsonValueKind.Null);
        root.GetProperty("model_used").GetString().ShouldBe("glm-4.6");
        root.GetProperty("advertised_tools").GetInt32().ShouldBe(-1);
        root.GetProperty("rounds").GetInt32().ShouldBe(3); // 2 reasoning rounds + 1 answer round
    }

    [Fact]
    public void LongToolArguments_AreClipped()
    {
        var scope = new TurnScope("s", "m", "p", 0, false);
        scope.RecordTool("native_add_notes", new string('x', 5000), "OK", 1);
        scope.Finish("ok", false, "", "", null);

        string args = SerializeAndParse(scope).GetProperty("calls")[0].GetProperty("args").GetString()!;

        args.Length.ShouldBeLessThan(2100);
        args.ShouldContain("…(+");
    }

    [Fact]
    public void Scope_IsAmbient_AndNestsForSubAgents()
    {
        SessionTranscript.Current.ShouldBeNull();

        var parent = new TurnScope("s", "m", "parent turn", 0, false);
        using (SessionTranscript.Begin(parent))
        {
            SessionTranscript.Current.ShouldBe(parent);
            parent.ParentTurnId.ShouldBeNull();

            var child = new TurnScope("s", "m", "sub-agent turn", 0, false);
            using (SessionTranscript.Begin(child))
            {
                SessionTranscript.Current.ShouldBe(child);
                child.ParentTurnId.ShouldBe(parent.TurnId); // sub-agent turn points back at its parent
            }

            SessionTranscript.Current.ShouldBe(parent); // inner dispose restored the parent
        }

        SessionTranscript.Current.ShouldBeNull(); // fully closed
    }

    [Fact]
    public void ResetForRetry_DiscardsTheAbortedAttemptsTelemetry()
    {
        // The streamed→buffered fallback reuses the scope; the aborted streamed attempt's tool calls
        // must not double-count with the buffered retry's.
        var scope = new TurnScope("s", "m", "p", 0, true);
        scope.RecordTool("streamed_call", "", "OK", 1);
        scope.RecordRequest("m", 10, 5, 100, 20);
        scope.AddRepairs(new[] { new KeyValuePair<string, int>("ArgsCoerce", 1) });

        scope.ResetForRetry();

        scope.RecordTool("buffered_call", "", "OK", 2); // the retry re-accumulates
        scope.Finish("ok", false, "", "done", null);

        JsonElement root = SerializeAndParse(scope);
        root.GetProperty("tool_calls").GetInt32().ShouldBe(1);
        JsonElement calls = root.GetProperty("calls");
        calls.GetArrayLength().ShouldBe(1);
        calls[0].GetProperty("name").GetString().ShouldBe("buffered_call");
        calls[0].GetProperty("seq").GetInt32().ShouldBe(1);                 // seq reset
        root.GetProperty("tokens").ValueKind.ShouldBe(JsonValueKind.Null);  // request cleared
        root.GetProperty("repairs").ValueKind.ShouldBe(JsonValueKind.Null); // repairs cleared
    }

    [Fact]
    public void CancelledTurn_SerializesCancelledStatus()
    {
        var scope = new TurnScope("s", "m", "p", 0, true);
        scope.Finish("cancelled", false, "", "", "cancelled");

        SerializeAndParse(scope).GetProperty("status").GetString().ShouldBe("cancelled");
    }
}
