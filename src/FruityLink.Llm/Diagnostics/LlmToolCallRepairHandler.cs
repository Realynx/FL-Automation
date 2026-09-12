using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FruityLink.Core.Abstractions;

namespace FruityLink.Llm.Diagnostics;

/// <summary>
/// Sanitizes malformed tool calls in (non-streaming) chat-completion responses before
/// Semantic Kernel parses them. Weak OpenAI-compatible backends (Ollama, MiniMax, LM Studio)
/// routinely emit invalid tool calls that SK then rejects for the whole turn:
/// <list type="bullet">
///   <item>argument JSON that is concatenated (<c>{}{…}</c>), double/triple string-encoded,
///     python-literal (<c>{'a': True}</c>), trailing-comma'd, fenced, or prose-wrapped —
///     repaired by <see cref="RepairArguments(string)"/>;</item>
///   <item>missing/empty/duplicate <c>tool_call.id</c>s — synthesized/re-minted (strict backends
///     400 the NEXT request if <c>tool_call_id</c> is blank; duplicates make tool results
///     overwrite each other);</item>
///   <item>misspelled function names — resolved against the tool list advertised in the request
///     (case, separator, plugin-prefix, and one-typo forgiveness) and rewritten to the advertised
///     spelling.</item>
/// </list>
/// <para>
/// It also folds a message's reasoning channel into the content channel: GLM (and other thinking
/// models over the OpenAI-compatible API) return reasoning in a non-standard
/// <c>message.reasoning_content</c> / <c>message.reasoning</c> field that SK's typed deserialization
/// silently drops. Prepending it to <c>content</c> as <c>&lt;think&gt;…&lt;/think&gt;</c> lets it
/// survive into <see cref="Microsoft.SemanticKernel.ChatMessageContent.Content"/>, where the agent
/// splits it back out into a "thought" delta.
/// </para>
/// <para>
/// Every repair is observable: one <see cref="ILlmDiagnostics"/> entry per repaired response names
/// the kinds that fired (with before/after snippets for argument coercions), unrecoverable cases
/// ({} fallback, unparseable body) get distinct error-level entries, and per-kind session counters
/// accumulate on the sink's optional <see cref="ILlmRepairTelemetry"/> capability.
/// </para>
/// </summary>
public sealed class LlmToolCallRepairHandler : DelegatingHandler
{
    /// <summary>Longest before/after snippet captured for an args-coerce diagnostics entry.</summary>
    private const int MaxSnippet = 500;

    /// <summary>
    /// How many string-unwrap levels <see cref="RepairArguments(string)"/> attempts. Triple-encoded
    /// arguments have been observed in the wild, so allow one more for headroom; the cap keeps a
    /// pathological deeply-nested string from looping.
    /// </summary>
    private const int MaxStringUnwrapDepth = 4;

    /// <summary>
    /// Lenient options for *reading* sloppy model output (trailing commas, comments). Emitted JSON
    /// is always re-serialized through <see cref="JsonNode"/>, so the leniency never leaks into
    /// what Semantic Kernel receives.
    /// </summary>
    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly ILlmDiagnostics _diagnostics;
    private readonly ILlmRepairTelemetry? _telemetry;

    public LlmToolCallRepairHandler(HttpMessageHandler innerHandler, ILlmDiagnostics diagnostics)
        : base(innerHandler)
    {
        _diagnostics = diagnostics;
        // Counters are an optional capability of the sink (see ILlmRepairTelemetry for why it is
        // not part of Core's ILlmDiagnostics); a sink without it still gets the Record() entries.
        _telemetry = diagnostics as ILlmRepairTelemetry;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode) return response;
        if (!LlmHttp.IsChatCompletions(request))
            return response;

        // Only hard-skip a real streaming (SSE) body — buffering that would break the stream. For
        // everything else (application/json, text/json, text/plain, or a server that omits the type)
        // we attempt repair; RepairBody fails safe and returns the body unchanged if it isn't JSON.
        if (LlmHttp.IsEventStream(response))
            return response;

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // Fire the JSON pass when there is tool-call JSON to repair OR a reasoning field to fold
        // into content. A body with neither is handed through verbatim (no parse, no re-serialize).
        bool hasToolCalls = body.Contains("\"tool_calls\"", StringComparison.Ordinal);
        bool hasReasoning = body.Contains("\"reasoning_content\"", StringComparison.Ordinal)
            || body.Contains("\"reasoning\"", StringComparison.Ordinal);

        string outBody = body;
        if (hasToolCalls || hasReasoning)
        {
            // The advertised tool list (the authoritative spelling for name resolution) comes from
            // the request we just sent. Read it only when the response actually contains tool
            // calls; the body is re-readable because the retry handler buffers every attempt into
            // a ByteArrayContent clone.
            string? requestBody = null;
            if (hasToolCalls && request.Content is not null)
            {
                try { requestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); }
                catch { /* unreadable request body -> skip name resolution, never fail the response */ }
            }

            var report = new RepairReport();
            outBody = RepairBody(body, requestBody, report);
            RecordRepairs(request, response, report, body);
        }

        // Always re-buffer: ReadAsStringAsync consumed the stream, so hand the SDK a fresh,
        // readable copy regardless of whether the underlying response was buffered. Media type is
        // forced to application/json because that is what the (possibly repaired) body now is.
        HttpContentRebuffer.Replace(response, outBody, "application/json");
        return response;
    }

    /// <summary>
    /// Emits the diagnostics for one repair pass: per-kind session counters, one entry naming the
    /// repair kinds that fired, and DISTINCT error-level entries for the two cases that must never
    /// be silent — a swallowed body-parse failure (the body went through unrepaired, so a later SK
    /// failure would be impossible to attribute) and the {} argument fallback (real argument
    /// content was discarded).
    /// </summary>
    private void RecordRepairs(
        HttpRequestMessage request, HttpResponseMessage response, RepairReport report, string originalBody)
    {
        if (!report.HasAnything) return;

        // Per-turn transcript (best-effort): which wire-repairs this response needed — a per-backend
        // tool-call-quality signal for the review (e.g. a model that constantly needs ArgsCoerce).
        var repairKinds = new List<KeyValuePair<string, int>>(6);
        if (report.IdSynths > 0) repairKinds.Add(new(nameof(LlmRepairKind.IdSynth), report.IdSynths));
        if (report.NameResolves > 0) repairKinds.Add(new(nameof(LlmRepairKind.NameResolve), report.NameResolves));
        if (report.ArgsCoerces > 0) repairKinds.Add(new(nameof(LlmRepairKind.ArgsCoerce), report.ArgsCoerces));
        if (report.ReasoningFolds > 0) repairKinds.Add(new(nameof(LlmRepairKind.ReasoningFold), report.ReasoningFolds));
        if (report.EmptyArgsFallbacks > 0) repairKinds.Add(new(nameof(LlmRepairKind.EmptyArgsFallback), report.EmptyArgsFallbacks));
        if (report.ParseFailure is not null) repairKinds.Add(new(nameof(LlmRepairKind.ParseFailure), 1));
        if (repairKinds.Count > 0) SessionTranscript.Current?.AddRepairs(repairKinds);

        if (_telemetry is not null)
        {
            if (report.IdSynths > 0) _telemetry.CountRepair(LlmRepairKind.IdSynth, report.IdSynths);
            if (report.NameResolves > 0) _telemetry.CountRepair(LlmRepairKind.NameResolve, report.NameResolves);
            if (report.ArgsCoerces > 0) _telemetry.CountRepair(LlmRepairKind.ArgsCoerce, report.ArgsCoerces);
            if (report.ReasoningFolds > 0) _telemetry.CountRepair(LlmRepairKind.ReasoningFold, report.ReasoningFolds);
            if (report.EmptyArgsFallbacks > 0) _telemetry.CountRepair(LlmRepairKind.EmptyArgsFallback, report.EmptyArgsFallbacks);
            if (report.ParseFailure is not null) _telemetry.CountRepair(LlmRepairKind.ParseFailure);
        }

        string uri = request.RequestUri?.ToString() ?? string.Empty;
        int attempt = request.Options.TryGetValue(LlmLoggingHandler.AttemptKey, out int a) ? a : 1;
        int status = (int)response.StatusCode;

        if (report.ParseFailure is { } failure)
        {
            _diagnostics.Record(new LlmCallRecord
            {
                Timestamp = DateTimeOffset.Now,
                Method = "REPAIR",
                Uri = uri,
                StatusCode = status,
                Ok = false,
                Attempt = attempt,
                Error = $"tool-call repair skipped — body unparseable: {failure.GetType().Name}: {failure.Message}",
                ResponsePreview = Clip(originalBody),
            });
        }

        if (report.HasRepairs)
        {
            _diagnostics.Record(new LlmCallRecord
            {
                Timestamp = DateTimeOffset.Now,
                Method = "REPAIR",
                Uri = uri,
                StatusCode = status,
                Ok = true,
                Attempt = attempt,
                ResponsePreview = report.Describe(),
            });
        }

        if (report.EmptyArgsFallbacks > 0)
        {
            _diagnostics.Record(new LlmCallRecord
            {
                Timestamp = DateTimeOffset.Now,
                Method = "REPAIR",
                Uri = uri,
                StatusCode = status,
                Ok = false,
                Attempt = attempt,
                Error = $"tool-call arguments unrecoverable — collapsed to {{}} (x{report.EmptyArgsFallbacks})",
                ResponsePreview = report.Describe(),
            });
        }
    }

    private static string RepairBody(string body, string? requestBody, RepairReport report)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(body);
            JsonArray? choices = root?["choices"]?.AsArray();
            if (choices is null) return body;

            // The advertised tool names are parsed from the request lazily, once, on the first
            // tool call encountered — most responses need no name resolution at all.
            IReadOnlyList<string>? advertised = null;
            bool advertisedLoaded = false;

            bool changed = false;
            foreach (JsonNode? choice in choices)
            {
                JsonNode? message = choice?["message"];
                if (message is null) continue;

                // 1) Repair malformed tool calls — the malformations weak OpenAI-compatible backends
                //    (Ollama, MiniMax, LM Studio) routinely emit and that SK then rejects.
                JsonArray? toolCalls = message["tool_calls"]?.AsArray();
                if (toolCalls is not null)
                {
                    // Ids must be unique within one message: SK keys the follow-up tool-result
                    // messages by tool_call_id, and duplicates make results overwrite each other.
                    var seenIds = new HashSet<string>(StringComparer.Ordinal);

                    foreach (JsonNode? call in toolCalls)
                    {
                        if (call is null) continue;

                        // Synthesize a missing/empty id (the follow-up tool-result message carries
                        // it as tool_call_id, and strict backends 400 the NEXT request if it's
                        // blank) and re-mint duplicates of an earlier call's id.
                        string? id = ReadString(call["id"]);
                        if (string.IsNullOrEmpty(id) || !seenIds.Add(id))
                        {
                            string fresh = NewCallId();
                            seenIds.Add(fresh);
                            call["id"] = fresh;
                            changed = true;
                            report.IdSynths++;
                        }
                        if (ReadString(call["type"]) is null)
                        {
                            call["type"] = "function";
                            changed = true;
                        }

                        JsonObject? fn = call["function"]?.AsObject();
                        if (fn is null) continue;

                        // Strip a namespacing prefix some models prepend (functions.foo / tools.foo)
                        // so the name matches a registered SK function.
                        string? name = ReadString(fn["name"]);
                        if (name is not null &&
                            (name.StartsWith("functions.", StringComparison.Ordinal) ||
                             name.StartsWith("tools.", StringComparison.Ordinal)))
                        {
                            name = name[(name.LastIndexOf('.') + 1)..];
                            fn["name"] = name;
                            changed = true;
                            report.NameResolves++;
                        }

                        // Resolve the (possibly de-prefixed) name against the advertised tool list.
                        // Only a UNIQUE match is rewritten; ambiguous or unmatched names pass
                        // through so SK's "wasn't defined. Correct yourself." feedback still fires.
                        if (name is not null)
                        {
                            if (!advertisedLoaded)
                            {
                                advertised = ExtractAdvertisedToolNames(requestBody);
                                advertisedLoaded = true;
                            }
                            if (advertised is not null && ResolveToolName(name, advertised) is { } resolved)
                            {
                                fn["name"] = resolved;
                                changed = true;
                                report.NameResolves++;
                            }
                        }

                        // SK requires `arguments` to be a JSON *string* holding an object. Coerce every
                        // other shape: JSON null / missing -> "{}", an object/array value -> stringified,
                        // a non-string scalar -> "{}", and a string -> repaired (unwraps multi-encoding,
                        // collapses {}{}, converts python literals, trims prose). fn["arguments"] returns
                        // null for both absent and JSON-null, which is exactly the case to backfill.
                        JsonNode? argsNode = fn["arguments"];
                        if (argsNode is JsonObject or JsonArray)
                        {
                            fn["arguments"] = argsNode.ToJsonString();
                            changed = true;
                            report.ArgsCoerces++;
                        }
                        else if (argsNode is null)
                        {
                            fn["arguments"] = "{}";
                            changed = true;
                            report.ArgsCoerces++;
                        }
                        else
                        {
                            string? args = TryGetString(argsNode);
                            if (args is null)
                            {
                                // A non-string scalar (number/bool) can never bind to arguments;
                                // collapsing it discards data, so flag it like the {} fallback.
                                report.AddArgsSnippet(argsNode.ToJsonString(), "{}");
                                fn["arguments"] = "{}";
                                changed = true;
                                report.ArgsCoerces++;
                                report.EmptyArgsFallbacks++;
                            }
                            else
                            {
                                string fixedArgs = RepairArguments(args, out bool collapsedToEmpty);
                                if (!string.Equals(fixedArgs, args, StringComparison.Ordinal))
                                {
                                    report.AddArgsSnippet(args, fixedArgs);
                                    fn["arguments"] = fixedArgs;
                                    changed = true;
                                    report.ArgsCoerces++;
                                    if (collapsedToEmpty) report.EmptyArgsFallbacks++;
                                }
                            }
                        }
                    }
                }

                // 2) Fold reasoning into the content channel so SK's typed deserialization keeps it.
                if (FoldReasoningIntoContent(message))
                {
                    changed = true;
                    report.ReasoningFolds++;
                }
            }

            return changed ? root!.ToJsonString() : body;
        }
        catch (Exception ex)
        {
            // Never let repair break a response — but never swallow silently either: the caller
            // records this as a distinct diagnostics entry (see RecordRepairs).
            report.ParseFailure = ex;
            return body;
        }
    }

    private static string NewCallId() => "call_" + Guid.NewGuid().ToString("N")[..24];

    /// <summary>
    /// Folds a message's non-standard <c>reasoning_content</c> / <c>reasoning</c> field into its
    /// <c>content</c> as a leading <c>&lt;think&gt;…&lt;/think&gt;</c> block, then removes the source
    /// field. No-ops (returns false) when neither field holds a non-empty string, so a response
    /// without reasoning is left exactly as-is. Tool-call-only messages (content null/absent) become
    /// content = just the <c>&lt;think&gt;…&lt;/think&gt;</c> block; tool_calls are never touched.
    /// </summary>
    private static bool FoldReasoningIntoContent(JsonNode message)
    {
        string? reasoning = ReadString(message["reasoning_content"]) ?? ReadString(message["reasoning"]);
        if (string.IsNullOrEmpty(reasoning)) return false;

        // content may be a plain string OR an OpenAI content-parts array
        // ([{ "type": "text", "text": "…" }, …]). Flatten the parts first — replacing an array
        // node with "<think>…</think>" + empty would delete the model's actual answer text.
        JsonNode? contentNode = message["content"];
        string content = contentNode is JsonArray parts
            ? ConcatTextParts(parts)
            : ReadString(contentNode) ?? string.Empty;
        message["content"] = $"<think>{reasoning}</think>{content}";

        JsonObject obj = message.AsObject();
        obj.Remove("reasoning_content");
        obj.Remove("reasoning");
        return true;
    }

    /// <summary>Concatenates the text of a content-parts array. Accepts both bare string elements
    /// and the standard <c>{ "type": "text", "text": "…" }</c> shape; non-text parts are skipped.</summary>
    private static string ConcatTextParts(JsonArray parts)
    {
        var sb = new StringBuilder();
        foreach (JsonNode? part in parts)
        {
            if (part is null) continue;
            string? text = part is JsonObject ? ReadString(part["text"]) : ReadString(part);
            if (!string.IsNullOrEmpty(text)) sb.Append(text);
        }
        return sb.ToString();
    }

    private static string? ReadString(JsonNode? node) => node is null ? null : TryGetString(node);

    private static string? TryGetString(JsonNode node)
    {
        try { return node.GetValue<string>(); }
        catch { return null; }
    }

    // ---- tool-name resolution -------------------------------------------------------------

    /// <summary>Parses <c>tools[].function.name</c> from a request body — the authoritative
    /// advertised list. Returns null when the request has no parseable tools (resolution is skipped).</summary>
    private static IReadOnlyList<string>? ExtractAdvertisedToolNames(string? requestBody)
    {
        if (string.IsNullOrEmpty(requestBody)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(requestBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("tools", out JsonElement tools) ||
                tools.ValueKind != JsonValueKind.Array)
                return null;

            var names = new List<string>();
            foreach (JsonElement tool in tools.EnumerateArray())
            {
                if (tool.ValueKind == JsonValueKind.Object &&
                    tool.TryGetProperty("function", out JsonElement fn) &&
                    fn.ValueKind == JsonValueKind.Object &&
                    fn.TryGetProperty("name", out JsonElement n) &&
                    n.ValueKind == JsonValueKind.String)
                {
                    names.Add(n.GetString()!);
                }
            }
            return names.Count > 0 ? names : null;
        }
        catch
        {
            return null; // no advertised list -> no resolution; never fail the response
        }
    }

    /// <summary>
    /// Resolves a response function name to the ADVERTISED spelling, trying progressively fuzzier
    /// tiers: exact (no rewrite needed) → case-insensitive → separator/whitespace normalization
    /// ([._- ] squashed) → bare name matching exactly one <c>*-name</c>/<c>*_name</c>/<c>*.name</c>
    /// plugin-prefixed tool → edit-distance-1 (one typo). Each tier only rewrites on a UNIQUE
    /// match; ambiguity falls through and ultimately returns null (leave the name unchanged).
    /// </summary>
    private static string? ResolveToolName(string name, IReadOnlyList<string> advertised)
    {
        foreach (string a in advertised)
            if (string.Equals(a, name, StringComparison.Ordinal))
                return null; // already the advertised spelling

        string? match = SingleOrNull(advertised, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        string normalized = NormalizeToolName(name);
        if (normalized.Length > 0)
        {
            match = SingleOrNull(advertised, a => string.Equals(NormalizeToolName(a), normalized, StringComparison.Ordinal));
            if (match is not null) return match;
        }

        match = SingleOrNull(advertised, a => HasBareNameSuffix(a, name));
        if (match is not null) return match;

        return SingleOrNull(advertised, a => IsEditDistanceOne(a, name));
    }

    /// <summary>Returns the single element satisfying <paramref name="predicate"/>, or null when
    /// none or several do — a rewrite is only safe on an unambiguous match.</summary>
    private static string? SingleOrNull(IReadOnlyList<string> advertised, Func<string, bool> predicate)
    {
        string? match = null;
        foreach (string candidate in advertised)
        {
            if (!predicate(candidate)) continue;
            if (match is not null) return null; // ambiguous
            match = candidate;
        }
        return match;
    }

    /// <summary>Lower-cases and squashes separator characters ('.', '_', '-', whitespace) so
    /// "Set Tempo" / "set-tempo" / "set_tempo" all normalize identically.</summary>
    private static string NormalizeToolName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c is '.' or '_' or '-' || char.IsWhiteSpace(c)) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>True when <paramref name="advertised"/> is <paramref name="bareName"/> with a
    /// plugin prefix in front (e.g. "flstudio-set_tempo" for bare "set_tempo").</summary>
    private static bool HasBareNameSuffix(string advertised, string bareName)
    {
        if (advertised.Length <= bareName.Length + 1) return false;
        if (!advertised.EndsWith(bareName, StringComparison.OrdinalIgnoreCase)) return false;
        char separator = advertised[advertised.Length - bareName.Length - 1];
        return separator is '-' or '_' or '.';
    }

    /// <summary>Case-insensitive Levenshtein distance == 1 (one substitution, insertion, or
    /// deletion). Distance 0 returns false — exact/case matches are handled by earlier tiers.</summary>
    private static bool IsEditDistanceOne(string a, string b)
    {
        if (a.Length > b.Length) (a, b) = (b, a);
        if (b.Length - a.Length > 1) return false;

        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[j])) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length == b.Length) { i++; j++; } // substitution
            else { j++; }                            // char inserted into the longer string
        }
        edits += (a.Length - i) + (b.Length - j); // unmatched tail = trailing insertions
        return edits == 1;
    }

    // ---- argument repair ------------------------------------------------------------------

    internal static string RepairArguments(string args) => RepairArguments(args, out _);

    /// <summary>
    /// Repairs a tool-call arguments string into strict JSON-object text. Tries, per candidate:
    /// direct parse (lenient read, strict re-emit), extraction of the first NON-EMPTY balanced
    /// object from concatenated/fenced/prose-wrapped text, then a python-literal conversion
    /// (single quotes, True/False/None) — and unwraps up to <see cref="MaxStringUnwrapDepth"/>
    /// levels of string-encoding between rounds. <paramref name="collapsedToEmpty"/> is true only
    /// when NON-EMPTY input defeated every strategy and real content was discarded — the caller
    /// must surface that loudly, never silently.
    /// </summary>
    internal static string RepairArguments(string args, out bool collapsedToEmpty)
    {
        collapsedToEmpty = false;
        string s = args.Trim();
        if (s.Length == 0) return "{}"; // absent args backfill — nothing was lost

        string candidate = s;
        for (int level = 0; level <= MaxStringUnwrapDepth; level++)
        {
            if (TryRepairCandidate(candidate, out string strict)) return strict;
            if (level == MaxStringUnwrapDepth || !TryUnwrapJsonString(candidate, out string inner)) break;
            candidate = inner.Trim();
            if (candidate.Length == 0) return "{}"; // string-wrapped emptiness — still a backfill
        }

        collapsedToEmpty = true;
        return "{}";
    }

    /// <summary>One repair round for one candidate string: direct object → balanced-object scan →
    /// python-literal conversion (then both again on the converted text).</summary>
    private static bool TryRepairCandidate(string candidate, out string strict)
    {
        if (TryNormalizeObject(candidate, out strict)) return true;
        if (TryExtractBestObject(candidate, out strict)) return true;

        // Python-literal fallback: some models emit dict repr ({'track': 3, 'mute': True}) instead
        // of JSON. Convert and re-test; ReferenceEquals short-circuits when nothing python-ish was
        // found (ConvertPythonLiterals returns the same instance).
        string pythonized = ConvertPythonLiterals(candidate);
        if (!ReferenceEquals(pythonized, candidate))
        {
            if (TryNormalizeObject(pythonized, out strict)) return true;
            if (TryExtractBestObject(pythonized, out strict)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="s"/> reads as a JSON *object* (not a bare string/number/null,
    /// which SK cannot bind to a function's arguments), yielding strict object text. A strictly
    /// valid input is returned verbatim so an already-clean body is never reformatted (a re-emit
    /// would change whitespace and mark every response "repaired"); only the lenient path
    /// (trailing commas, comments) re-serializes to guarantee strict output.
    /// </summary>
    private static bool TryNormalizeObject(string s, out string strict)
    {
        if (IsStrictJsonObject(s)) { strict = s; return true; }

        try
        {
            if (JsonNode.Parse(s, documentOptions: LenientJson) is JsonObject obj)
            {
                strict = obj.ToJsonString();
                return true;
            }
        }
        catch { /* not an object */ }

        strict = string.Empty;
        return false;
    }

    private static bool IsStrictJsonObject(string s)
    {
        try { using JsonDocument d = JsonDocument.Parse(s); return d.RootElement.ValueKind == JsonValueKind.Object; }
        catch { return false; }
    }

    /// <summary>If <paramref name="s"/> is a JSON string literal, yields its decoded content.</summary>
    private static bool TryUnwrapJsonString(string s, out string inner)
    {
        inner = string.Empty;
        try
        {
            using JsonDocument d = JsonDocument.Parse(s, LenientJson);
            if (d.RootElement.ValueKind == JsonValueKind.String)
            {
                inner = d.RootElement.GetString() ?? string.Empty;
                return true;
            }
        }
        catch { /* not a JSON string */ }
        return false;
    }

    /// <summary>
    /// Scans ALL balanced top-level objects in concatenated/fenced/prose-wrapped text and returns
    /// the first NON-EMPTY parseable one, falling back to the first parseable empty one —
    /// <c>{}{"track":3}</c> must yield <c>{"track":3}</c>, not the leading <c>{}</c>.
    ///
    /// <para>Two deliberate refusals (return false → the caller falls through to the LOUD
    /// <c>{}</c> + collapsedToEmpty path): (1) when an UNBALANCED '{' is the start of a REAL
    /// object — either its prefix is nothing but whitespace/earlier balanced objects, or (even
    /// under a prose prefix) its first non-whitespace character is a quoted key — the remainder of
    /// the candidate is ONE truncated object (classic finish_reason=length cut); rescanning inside
    /// it would promote a nested fragment (e.g. <c>{"mute":true}</c> out of
    /// <c>{"track":3,"opts":{"mute":true}</c>, prose-wrapped or not) to be the entire arguments.
    /// The inside-rescan is only for a genuinely prose-embedded stray '{' NOT followed by a quoted
    /// key (e.g. <c>x{y {"a":1}</c> still recovers <c>{"a":1}</c>). (2) a
    /// harvested EMPTY object only wins when the whole candidate is empty objects + whitespace — a
    /// <c>{}</c> plucked out of a string value or prose must not silently discard real content.</para>
    /// </summary>
    private static bool TryExtractBestObject(string s, out string strict)
    {
        strict = string.Empty;
        string? firstEmpty = null;

        int searchFrom = 0;
        int clean = 0;   // s[..clean] consists ONLY of whitespace and balanced objects
        while (searchFrom < s.Length)
        {
            int start = s.IndexOf('{', searchFrom);
            if (start < 0) break;
            bool prefixClean = clean <= start && IsWhiteSpaceRange(s, clean, start);

            string? balanced = ExtractBalancedObject(s, start, out int end);
            if (balanced is null)
            {
                // Refusal (1): the whole remaining candidate is one truncated object.
                if (prefixClean) return false;
                // Refusal (1), prose-prefixed variant: the brace opens a REAL object (its first
                // non-whitespace character is a quoted key — post-ConvertPythonLiterals this also
                // covers truncated python dicts) that never closes: a length cut mid-object.
                // Rescanning inside it would silently promote a nested fragment to be the entire
                // arguments; collapse loudly instead.
                if (StartsQuotedKey(s, start)) return false;
                // Unbalanced from this '{' (a stray brace in prose, e.g. x{y {"a":1}) — a later
                // one may be fine.
                searchFrom = start + 1;
                continue;
            }
            if (prefixClean) clean = end + 1;
            searchFrom = end + 1;

            if (!TryNormalizeObject(balanced, out string candidate)) continue;
            if (!IsEmptyObjectJson(candidate))
            {
                strict = candidate;
                return true;
            }
            firstEmpty ??= candidate;
        }

        // Refusal (2): only surface a harvested "{}" when nothing else was in the candidate.
        if (firstEmpty is not null && IsWhiteSpaceRange(s, clean, s.Length))
        {
            strict = firstEmpty;
            return true;
        }
        return false;
    }

    /// <summary>True when the first non-whitespace character after the '{' at
    /// <paramref name="braceIndex"/> is '"' — i.e. the brace opens a real JSON object (a quoted
    /// key follows) rather than sitting in prose.</summary>
    private static bool StartsQuotedKey(string s, int braceIndex)
    {
        for (int i = braceIndex + 1; i < s.Length; i++)
        {
            if (char.IsWhiteSpace(s[i])) continue;
            return s[i] == '"';
        }
        return false;
    }

    /// <summary>True when <c>s[from..to)</c> is empty or all whitespace.</summary>
    private static bool IsWhiteSpaceRange(string s, int from, int to)
    {
        for (int i = from; i < to; i++)
            if (!char.IsWhiteSpace(s[i])) return false;
        return true;
    }

    /// <summary>Extracts the balanced <c>{…}</c> starting at <paramref name="start"/> (string- and
    /// escape-aware), or null when it never closes. <paramref name="end"/> is the index of the
    /// closing brace so the caller can continue scanning after it.</summary>
    private static string? ExtractBalancedObject(string s, int start, out int end)
    {
        end = -1;
        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    end = i;
                    return s.Substring(start, i - start + 1);
                }
            }
        }
        return null;
    }

    private static bool IsEmptyObjectJson(string s)
    {
        try
        {
            using JsonDocument d = JsonDocument.Parse(s, LenientJson);
            if (d.RootElement.ValueKind != JsonValueKind.Object) return false;
            JsonElement.ObjectEnumerator properties = d.RootElement.EnumerateObject();
            return !properties.MoveNext();
        }
        catch { return false; }
    }

    /// <summary>
    /// State-machine conversion of python literals to JSON: single-quoted strings become
    /// double-quoted (escaping embedded double quotes, honoring backslash escapes) and bare
    /// <c>True</c>/<c>False</c>/<c>None</c> identifiers become <c>true</c>/<c>false</c>/<c>null</c>.
    /// Content inside double-quoted strings is never touched. Returns the SAME instance when no
    /// conversion applied, so callers can short-circuit with ReferenceEquals.
    /// </summary>
    private static string ConvertPythonLiterals(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        bool changed = false;
        bool inDouble = false;
        bool inSingle = false;
        bool escape = false;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (inDouble)
            {
                sb.Append(c);
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inDouble = false;
            }
            else if (inSingle)
            {
                if (escape)
                {
                    escape = false;
                    // Python's \' has no JSON equivalent — emit the bare quote. Everything else
                    // keeps its backslash (\\, \n, \" survive as valid JSON escapes).
                    if (c == '\'') sb.Append('\'');
                    else sb.Append('\\').Append(c);
                }
                else if (c == '\\') escape = true;
                else if (c == '\'') { sb.Append('"'); inSingle = false; }
                else if (c == '"') sb.Append("\\\""); // now inside a double-quoted JSON string
                else sb.Append(c);
            }
            else if (c == '"')
            {
                sb.Append(c);
                inDouble = true;
            }
            else if (c == '\'')
            {
                sb.Append('"');
                inSingle = true;
                changed = true;
            }
            else if (char.IsLetter(c) && (i == 0 || !IsIdentifierChar(s[i - 1])))
            {
                if (MatchesWord(s, i, "True")) { sb.Append("true"); i += 3; changed = true; }
                else if (MatchesWord(s, i, "False")) { sb.Append("false"); i += 4; changed = true; }
                else if (MatchesWord(s, i, "None")) { sb.Append("null"); i += 3; changed = true; }
                else sb.Append(c);
            }
            else
            {
                sb.Append(c);
            }
        }

        return changed ? sb.ToString() : s;
    }

    private static bool MatchesWord(string s, int index, string word) =>
        index + word.Length <= s.Length &&
        string.CompareOrdinal(s, index, word, 0, word.Length) == 0 &&
        (index + word.Length == s.Length || !IsIdentifierChar(s[index + word.Length]));

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string Clip(string s) =>
        s.Length <= MaxSnippet ? s : s[..MaxSnippet] + "…";

    /// <summary>Mutable tally of what one repair pass did, turned into diagnostics by
    /// <see cref="RecordRepairs"/>.</summary>
    private sealed class RepairReport
    {
        public int IdSynths { get; set; }
        public int NameResolves { get; set; }
        public int ArgsCoerces { get; set; }
        public int ReasoningFolds { get; set; }
        public int EmptyArgsFallbacks { get; set; }
        public Exception? ParseFailure { get; set; }
        public List<string> ArgsSnippets { get; } = [];

        public bool HasRepairs => IdSynths + NameResolves + ArgsCoerces + ReasoningFolds > 0;
        public bool HasAnything => HasRepairs || ParseFailure is not null;

        public void AddArgsSnippet(string before, string after) =>
            ArgsSnippets.Add($"{Clip(before)} -> {Clip(after)}");

        public string Describe()
        {
            var sb = new StringBuilder("kinds:");
            AppendKind(sb, "id-synth", IdSynths);
            AppendKind(sb, "name-resolve", NameResolves);
            AppendKind(sb, "args-coerce", ArgsCoerces);
            AppendKind(sb, "reasoning-fold", ReasoningFolds);
            AppendKind(sb, "args-empty-fallback", EmptyArgsFallbacks);
            foreach (string snippet in ArgsSnippets)
                sb.AppendLine().Append("args-coerce: ").Append(snippet);
            return sb.ToString();
        }

        private static void AppendKind(StringBuilder sb, string label, int count)
        {
            if (count <= 0) return;
            sb.Append(' ').Append(label);
            if (count > 1) sb.Append('x').Append(count);
        }
    }
}
