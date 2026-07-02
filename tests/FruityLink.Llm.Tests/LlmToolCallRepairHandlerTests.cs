using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FruityLink.Core.Abstractions;
using FruityLink.Llm.Diagnostics;
using Shouldly;
using Xunit;

namespace FruityLink.Llm.Tests;

/// <summary>
/// Golden-file suite for <see cref="LlmToolCallRepairHandler"/>. The RepairArguments tables pin
/// every argument-coercion behavior to a NAMED case, so any future change to the repair strategy
/// flips a named assertion instead of silently shifting wire behavior. The SendAsync tests drive
/// full chat-completion JSON through the handler end-to-end (ids, names, argument shapes,
/// reasoning folding, SSE passthrough, header hygiene, diagnostics).
/// </summary>
public sealed class LlmToolCallRepairHandlerTests
{
    private const string ChatUri = "http://localhost:11434/v1/chat/completions";
    private const string MinimalRequest = """{"model":"m","messages":[]}""";
    private const string Track3 = """{"track":3}""";

    // ---- RepairArguments: golden table (repairable inputs) ----------------------------------

    public static TheoryData<string, string, string> RepairableArgumentCases()
    {
        // "double-encoded" = the arguments string itself is a JSON string literal holding the
        // object (one extra encoding layer); each Serialize adds one more layer.
        string doubleEncoded = JsonSerializer.Serialize(Track3);
        string tripleEncoded = JsonSerializer.Serialize(doubleEncoded);
        string maxDepthEncoded = JsonSerializer.Serialize(JsonSerializer.Serialize(tripleEncoded)); // 4 layers

        return new TheoryData<string, string, string>
        {
            // (case name, wire input, expected strict-object output)
            { "already-strict object passes through verbatim", Track3, Track3 },
            { "whitespace padding trimmed", "  " + Track3 + "  ", Track3 },
            { "trailing comma", """{"track":3,}""", Track3 },
            { "line comment", "{\"track\":3 // drums\n}", Track3 },
            { "block comment", """{"track":3 /* drums */}""", Track3 },
            { "python dict single quotes + True/False/None",
              "{'track': 3, 'mute': True, 'name': None, 'solo': False}",
              """{"track": 3, "mute": true, "name": null, "solo": false}""" },
            { "python single-quoted string with embedded double quotes",
              "{'msg': 'say \"hi\"'}",
              """{"msg": "say \"hi\""}""" },
            { "double-encoded string", doubleEncoded, Track3 },
            { "triple-encoded string", tripleEncoded, Track3 },
            { "string-unwrap depth boundary (4 layers still repairs)", maxDepthEncoded, Track3 },
            { "concatenation prefers the non-empty object ({} first)", "{}" + Track3, Track3 },
            { "concatenation prefers the non-empty object ({} last)", Track3 + "{}", Track3 },
            { "only empty objects concatenated yield {}", "{}{}", "{}" },
            { "code fence", "```json\n" + Track3 + "\n```", Track3 },
            { "prose prefix", "Sure — calling it with " + Track3 + " now.", Track3 },
            { "stray unbalanced brace before the object", "use { sparingly " + Track3, Track3 },
            // The truncated-root refusal must key on a QUOTED KEY after the '{' — a prose brace
            // (followed by ordinary text) still rescans and recovers the real object.
            { "prose brace not followed by a quoted key still recovers", """x{y {"a":1}""", """{"a":1}""" },
            { "unicode content preserved untouched", """{"name":"Пиано 🎹"}""", """{"name":"Пиано 🎹"}""" },
        };
    }

    [Theory]
    [MemberData(nameof(RepairableArgumentCases))]
    public void RepairArguments_Golden_YieldsStrictObject(string caseName, string input, string expected)
    {
        string repaired = LlmToolCallRepairHandler.RepairArguments(input, out bool collapsedToEmpty);

        repaired.ShouldBe(expected, caseName);
        collapsedToEmpty.ShouldBeFalse(caseName);
    }

    // ---- RepairArguments: residual-{} fallback (real content discarded, must be flagged) ----

    public static TheoryData<string, string> UnrecoverableArgumentCases()
    {
        string overDepthEncoded = Track3;
        for (int i = 0; i < 5; i++) overDepthEncoded = JsonSerializer.Serialize(overDepthEncoded); // one past the cap

        return new TheoryData<string, string>
        {
            { "bare array", "[1,2,3]" },
            { "string-encoded array", JsonSerializer.Serialize("[1,2,3]") },
            { "bare number", "42" },
            { "bare boolean", "true" },
            { "quoted prose", "\"hello\"" },
            { "prose without any object", "cannot comply" },
            { "string-unwrap depth exceeded (5 layers)", overDepthEncoded },
            // A TRUNCATED root object (classic finish_reason=length cut) must NOT have a nested
            // fragment promoted to be the whole arguments ({"mute":true} here would execute the
            // tool with a plausible-but-wrong argument set) — it must collapse LOUDLY instead.
            { "truncated root object keeps its nested fragment out", """{"track": 3, "opts": {"mute": true}""" },
            // The refusal must also hold when PROSE precedes the truncated root object (the
            // "prefixClean" fast-path doesn't apply): rescanning inside would promote the nested
            // {"mute":true} to be the ENTIRE arguments — silently, since collapsedToEmpty stays
            // false — and execute the tool against the wrong target.
            { "prose-prefixed truncated root object keeps its nested fragment out",
              """call with {"track":3,"opts":{"mute":true}""" },
            // Same gap via the python-literal pass: ConvertPythonLiterals rewrites the dict to
            // JSON quoting, then the extraction re-runs — it must refuse there too.
            { "prose-prefixed truncated python dict keeps its nested fragment out",
              "use {'track': 3, 'opts': {'mute': True}" },
            // A '{}' living inside a STRING VALUE of a truncated object is not an extraction —
            // returning it silently would discard the real argument content without the flag.
            { "empty object inside a string value of a truncated object", """{"query": "{} is empty", "track": """ },
            // Accepted trade of the truncated-root refusal: a doubled-brace typo no longer
            // recovers the inner object; it collapses loudly and the model self-corrects.
            { "doubled opening brace", """{{"a":1}""" },
        };
    }

    [Theory]
    [MemberData(nameof(UnrecoverableArgumentCases))]
    public void RepairArguments_UnrecoverableInput_FallsBackToEmptyObject_AndFlagsLoss(string caseName, string input)
    {
        string repaired = LlmToolCallRepairHandler.RepairArguments(input, out bool collapsedToEmpty);

        repaired.ShouldBe("{}", caseName);
        collapsedToEmpty.ShouldBeTrue(caseName); // real content was discarded — never silent
    }

    // ---- RepairArguments: empty backfill (nothing was lost, must NOT be flagged) ------------

    public static TheoryData<string, string> EmptyBackfillCases() => new()
    {
        { "empty string", "" },
        { "whitespace only", "   " },
        { "string-wrapped empty", "\"\"" },
        { "string-wrapped whitespace", "\" \"" },
    };

    [Theory]
    [MemberData(nameof(EmptyBackfillCases))]
    public void RepairArguments_EmptyInput_BackfillsWithoutFlaggingLoss(string caseName, string input)
    {
        string repaired = LlmToolCallRepairHandler.RepairArguments(input, out bool collapsedToEmpty);

        repaired.ShouldBe("{}", caseName);
        collapsedToEmpty.ShouldBeFalse(caseName);
    }

    // ---- SendAsync: end-to-end over full chat-completion JSON -------------------------------

    [Fact]
    public async Task SendAsync_SynthesizesMissingIds_RemintsDuplicates_AndBackfillsType()
    {
        const string body = """
        {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
          {"id":"","type":"function","function":{"name":"set_tempo","arguments":"{\"bpm\":128}"}},
          {"id":"call_1","type":"function","function":{"name":"set_tempo","arguments":"{}"}},
          {"id":"call_1","function":{"name":"set_tempo","arguments":"{}"}}
        ]}}]}
        """;

        var (response, diagnostics) = await SendThroughHandlerAsync(FakeHttpMessageHandler.Json(HttpStatusCode.OK, body));

        JsonArray calls = (await ReadJsonAsync(response))["choices"]![0]!["message"]!["tool_calls"]!.AsArray();
        string id0 = calls[0]!["id"]!.GetValue<string>();
        string id1 = calls[1]!["id"]!.GetValue<string>();
        string id2 = calls[2]!["id"]!.GetValue<string>();

        id0.ShouldStartWith("call_");
        id1.ShouldBe("call_1");    // the first holder of an id keeps it
        id2.ShouldNotBe("call_1"); // the duplicate is re-minted
        id2.ShouldStartWith("call_");
        new[] { id0, id1, id2 }.ShouldBeUnique();
        calls[2]!["type"]!.GetValue<string>().ShouldBe("function");
        calls[0]!["function"]!["arguments"]!.GetValue<string>().ShouldBe("""{"bpm":128}""");

        // A repaired response is observable: one REPAIR entry naming the kinds, plus counters.
        LlmCallRecord repair = diagnostics.Records.ShouldHaveSingleItem();
        repair.Method.ShouldBe("REPAIR");
        repair.Ok.ShouldBeTrue();
        repair.ResponsePreview!.ShouldContain("id-synth");
        diagnostics.RepairCounters[LlmRepairKind.IdSynth].ShouldBe(2);
    }

    [Theory]
    [InlineData("set_tempo", "set_tempo")]               // exact — untouched
    [InlineData("SET_TEMPO", "set_tempo")]               // case-insensitive
    [InlineData("set.tempo", "set_tempo")]               // separator normalization
    [InlineData("set tempo", "set_tempo")]               // whitespace separator
    [InlineData("set_volume", "flstudio-set_volume")]    // bare name -> plugin-prefixed tool
    [InlineData("list_chanels", "list_channels")]        // edit distance 1 (one typo)
    [InlineData("functions.set_tempo", "set_tempo")]     // namespacing prefix stripped
    [InlineData("tools.list_channels", "list_channels")] // namespacing prefix stripped
    [InlineData("do_magic_stuff", "do_magic_stuff")]     // no match — passthrough so SK feedback fires
    public async Task SendAsync_ResolvesToolNames_AgainstRequestAdvertisedTools(string modelName, string expected)
    {
        const string requestWithTools = """
        {"model":"m","messages":[],"tools":[
          {"type":"function","function":{"name":"set_tempo"}},
          {"type":"function","function":{"name":"flstudio-set_volume"}},
          {"type":"function","function":{"name":"list_channels"}}
        ]}
        """;
        string body = $$$"""
        {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
          {"id":"call_1","type":"function","function":{"name":"{{{modelName}}}","arguments":"{}"}}
        ]}}]}
        """;

        var (response, _) = await SendThroughHandlerAsync(
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, body), requestWithTools);

        (await ReadJsonAsync(response))["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["name"]!
            .GetValue<string>().ShouldBe(expected);
    }

    [Fact]
    public async Task SendAsync_CoercesNonStringArgumentShapes_ToObjectStrings()
    {
        const string body = """
        {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
          {"id":"call_a","type":"function","function":{"name":"t1","arguments":null}},
          {"id":"call_b","type":"function","function":{"name":"t2"}},
          {"id":"call_c","type":"function","function":{"name":"t3","arguments":{"track":3}}},
          {"id":"call_d","type":"function","function":{"name":"t4","arguments":7}}
        ]}}]}
        """;

        var (response, diagnostics) = await SendThroughHandlerAsync(FakeHttpMessageHandler.Json(HttpStatusCode.OK, body));

        JsonArray calls = (await ReadJsonAsync(response))["choices"]![0]!["message"]!["tool_calls"]!.AsArray();
        calls[0]!["function"]!["arguments"]!.GetValue<string>().ShouldBe("{}");                  // JSON null backfilled
        calls[1]!["function"]!["arguments"]!.GetValue<string>().ShouldBe("{}");                  // absent backfilled
        calls[2]!["function"]!["arguments"]!.GetValue<string>().ShouldBe("""{"track":3}""");     // object stringified
        calls[3]!["function"]!["arguments"]!.GetValue<string>().ShouldBe("{}");                  // scalar unbindable
        diagnostics.RepairCounters[LlmRepairKind.ArgsCoerce].ShouldBe(4);
        diagnostics.RepairCounters[LlmRepairKind.EmptyArgsFallback].ShouldBe(1); // only the scalar lost data
    }

    [Fact]
    public async Task SendAsync_UnrecoverableArguments_CollapseToEmpty_AndRecordErrorDiagnostics()
    {
        const string body = """
        {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[
          {"id":"call_1","type":"function","function":{"name":"set_tempo","arguments":"[1,2,3]"}}
        ]}}]}
        """;

        var (response, diagnostics) = await SendThroughHandlerAsync(FakeHttpMessageHandler.Json(HttpStatusCode.OK, body));

        (await ReadJsonAsync(response))["choices"]![0]!["message"]!["tool_calls"]![0]!["function"]!["arguments"]!
            .GetValue<string>().ShouldBe("{}");
        diagnostics.RepairCounters[LlmRepairKind.EmptyArgsFallback].ShouldBe(1);
        // The {} fallback is a distinct ERROR-level entry (argument content was discarded) on top
        // of the normal ok REPAIR entry.
        diagnostics.Records.ShouldContain(r => r.Ok && r.Method == "REPAIR");
        LlmCallRecord loss = diagnostics.Records.Single(r => !r.Ok);
        loss.Error!.ShouldContain("unrecoverable");
    }

    [Fact]
    public async Task SendAsync_FoldsReasoningContent_IntoThinkPrefixedContent()
    {
        const string body = """{"choices":[{"message":{"role":"assistant","reasoning_content":"check the bpm","content":"Done."}}]}""";

        var (response, diagnostics) = await SendThroughHandlerAsync(FakeHttpMessageHandler.Json(HttpStatusCode.OK, body));

        JsonNode message = (await ReadJsonAsync(response))["choices"]![0]!["message"]!;
        message["content"]!.GetValue<string>().ShouldBe("<think>check the bpm</think>Done.");
        message.AsObject().ContainsKey("reasoning_content").ShouldBeFalse();
        diagnostics.RepairCounters[LlmRepairKind.ReasoningFold].ShouldBe(1);
    }

    [Fact]
    public async Task SendAsync_FoldsReasoningFieldVariant_IntoContent()
    {
        const string body = """{"choices":[{"message":{"role":"assistant","reasoning":"alt channel","content":"Hi"}}]}""";

        var (response, _) = await SendThroughHandlerAsync(FakeHttpMessageHandler.Json(HttpStatusCode.OK, body));

        JsonNode message = (await ReadJsonAsync(response))["choices"]![0]!["message"]!;
        message["content"]!.GetValue<string>().ShouldBe("<think>alt channel</think>Hi");
        message.AsObject().ContainsKey("reasoning").ShouldBeFalse();
    }

    [Fact]
    public async Task SendAsync_FoldsReasoning_OnToolCallOnlyMessage()
    {
        const string body = """
        {"choices":[{"message":{"role":"assistant","content":null,"reasoning_content":"pick the mixer tool","tool_calls":[
          {"id":"call_1","type":"function","function":{"name":"set_tempo","arguments":"{}"}}
        ]}}]}
        """;

        var (response, _) = await SendThroughHandlerAsync(FakeHttpMessageHandler.Json(HttpStatusCode.OK, body));

        JsonNode message = (await ReadJsonAsync(response))["choices"]![0]!["message"]!;
        message["content"]!.GetValue<string>().ShouldBe("<think>pick the mixer tool</think>");
        message["tool_calls"]![0]!["id"]!.GetValue<string>().ShouldBe("call_1"); // tool calls untouched
    }

    [Fact]
    public async Task SendAsync_FoldsReasoning_ContentPartsArray_AnswerTextSurvives()
    {
        // content may be an OpenAI content-parts ARRAY; folding must flatten the parts, not
        // replace the array with <think>…</think> + nothing (that would delete the answer).
        const string body = """
        {"choices":[{"message":{"role":"assistant","reasoning_content":"think",
          "content":[{"type":"text","text":"The tempo "},{"type":"text","text":"is 128."}]}}]}
        """;

        var (response, _) = await SendThroughHandlerAsync(FakeHttpMessageHandler.Json(HttpStatusCode.OK, body));

        (await ReadJsonAsync(response))["choices"]![0]!["message"]!["content"]!
            .GetValue<string>().ShouldBe("<think>think</think>The tempo is 128.");
    }

    [Fact]
    public async Task SendAsync_SseBody_PassesThroughUntouched()
    {
        // Streaming bodies must never be buffered/parsed — even when they contain "tool_calls".
        const string sse = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"\"}]}}]}\n\ndata: [DONE]\n\n";
        var inner = new StaticResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        var (response, diagnostics) = await SendThroughHandlerAsync(inner);

        (await response.Content.ReadAsStringAsync()).ShouldBe(sse);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        diagnostics.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task SendAsync_StripsStaleByteDescribingHeaders_KeepsOthers()
    {
        const string body = """{"choices":[]}""";
        var inner = new StaticResponseHandler(() =>
        {
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            content.Headers.ContentEncoding.Add("gzip");   // describes bytes that no longer exist
            content.Headers.ContentMD5 = new byte[16];     // checksum of the old bytes
            content.Headers.ContentLanguage.Add("en");     // NOT byte-describing — must survive
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var (response, _) = await SendThroughHandlerAsync(inner);

        response.Content.Headers.ContentEncoding.ShouldBeEmpty();
        response.Content.Headers.ContentMD5.ShouldBeNull();
        response.Content.Headers.ContentLanguage.ShouldContain("en");
        response.Content.Headers.ContentLength.ShouldBe((long?)Encoding.UTF8.GetByteCount(body));
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
    }

    [Fact]
    public async Task SendAsync_CleanResponse_IsNotRewritten_AndRecordsNothing()
    {
        const string body = """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"set_tempo","arguments":"{}"}}]}}]}""";

        var (response, diagnostics) = await SendThroughHandlerAsync(FakeHttpMessageHandler.Json(HttpStatusCode.OK, body));

        // Byte-for-byte identical: an already-clean body is never re-serialized (a re-emit would
        // change whitespace and mark every response "repaired").
        (await response.Content.ReadAsStringAsync()).ShouldBe(body);
        diagnostics.Records.ShouldBeEmpty();
        diagnostics.RepairCounters.ShouldBeEmpty();
    }

    [Fact]
    public async Task SendAsync_NonChatCompletionUri_IsIgnored()
    {
        const string body = """{"data":[],"note":"contains \"tool_calls\" lookalike text"}""";

        var (response, diagnostics) = await SendThroughHandlerAsync(
            FakeHttpMessageHandler.Json(HttpStatusCode.OK, body),
            uri: "http://localhost:11434/v1/embeddings");

        (await response.Content.ReadAsStringAsync()).ShouldBe(body);
        diagnostics.Records.ShouldBeEmpty();
    }

    [Fact]
    public async Task SendAsync_UnparseableBody_PassesThrough_AndRecordsParseFailure()
    {
        const string body = """{"choices":[{"message":{"tool_calls":[""";

        var (response, diagnostics) = await SendThroughHandlerAsync(FakeHttpMessageHandler.Json(HttpStatusCode.OK, body));

        (await response.Content.ReadAsStringAsync()).ShouldBe(body); // fail-safe: body untouched
        LlmCallRecord record = diagnostics.Records.ShouldHaveSingleItem();
        record.Ok.ShouldBeFalse();
        record.Error!.ShouldContain("unparseable");
        diagnostics.RepairCounters[LlmRepairKind.ParseFailure].ShouldBe(1);
    }

    // ---- plumbing ----------------------------------------------------------------------------

    private static async Task<(HttpResponseMessage Response, RecordingLlmDiagnostics Diagnostics)> SendThroughHandlerAsync(
        HttpMessageHandler inner, string requestBody = MinimalRequest, string uri = ChatUri)
    {
        var diagnostics = new RecordingLlmDiagnostics();
        using var client = new HttpClient(new LlmToolCallRepairHandler(inner, diagnostics));

        HttpResponseMessage response = await client.PostAsync(
            uri, new StringContent(requestBody, Encoding.UTF8, "application/json"));

        return (response, diagnostics);
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    /// <summary>Returns a caller-built response for every request — for response shapes
    /// <see cref="FakeHttpMessageHandler"/> cannot express (SSE media type, custom content headers).</summary>
    private sealed class StaticResponseHandler(Func<HttpResponseMessage> factory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(factory());
    }
}

/// <summary>
/// Recording <see cref="ILlmDiagnostics"/> sink shared by the wire-layer handler tests. Also
/// implements the optional telemetry capabilities the handlers discover via soft cast
/// (<see cref="ILlmRepairTelemetry"/>, <see cref="ILlmUsageTelemetry"/>) so tests can assert on
/// repair counters and usage samples without touching the file-writing default sink.
/// </summary>
internal sealed class RecordingLlmDiagnostics : ILlmDiagnostics, ILlmRepairTelemetry, ILlmUsageTelemetry
{
    private readonly Dictionary<LlmRepairKind, long> _counters = new();

    public List<LlmCallRecord> Records { get; } = [];
    public List<LlmUsageSample> UsageSamples { get; } = [];

    public string LogDirectory => string.Empty;
    public IReadOnlyList<LlmCallRecord> Recent => Records;
    public event EventHandler? Changed { add { } remove { } }

    public void Record(LlmCallRecord record) => Records.Add(record);

    public void Clear()
    {
        Records.Clear();
        UsageSamples.Clear();
        _counters.Clear();
    }

    public void CountRepair(LlmRepairKind kind, int count = 1) =>
        _counters[kind] = _counters.TryGetValue(kind, out long current) ? current + count : count;

    public IReadOnlyDictionary<LlmRepairKind, long> RepairCounters => _counters;

    public void RecordUsage(LlmUsageSample sample) => UsageSamples.Add(sample);

    public LlmUsageTotals UsageTotals => new();
}
