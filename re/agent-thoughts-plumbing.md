# Agent "Thoughts" plumbing — why no reasoning ever shows, end to end

**Question:** the FL Agent (backend `glm-5.2:cloud` over an OpenAI-compatible endpoint)
displays **no thoughts/reasoning**. Is the model not *emitting* reasoning, is the pipeline
not *parsing* it, or is the UI not *showing* it?

**Answer (one line):** the **UIs are fine**; reasoning is **lost at the connector/parse
boundary**. Semantic Kernel's OpenAI connector throws away the model's `reasoning_content` /
`reasoning` field, and `FlAgent.ExtractReasoning` only inspects SK *metadata*, which never
contains reasoning (at best it mis-fires on the `FinishReason` key and surfaces the literal
string `"Stop"`). Secondarily, the request never explicitly enables thinking. Fix = surface
the reasoning field through the existing HTTP handler chain and parse it in `FlAgent`.

> Scope: this note is about **plumbing + display** of thoughts. The *content/quality* of the
> reasoning (caveman prompt, minimalism) is a sibling's lane and only noted in passing.

---

## TL;DR of the gap

| Layer | File:line | Verdict |
|---|---|---|
| **Request** (enable thinking) | `FlAgent.cs:84-102` `OpenAIPromptExecutionSettings` | No `thinking`/`reasoning`/`reasoning_effort`/`enable_thinking` param. For GLM over a generic OpenAI-compatible endpoint the thinking param is frequently required to *emit* reasoning. (Likely auto-enabled for `glm-5.2:cloud` via Ollama, but not guaranteed.) |
| **Transport** (raw body) | `FlAgent.cs:108-110` | Runs **non-streaming** via `_chat.GetChatMessageContentAsync` and never reads the raw HTTP body, so even an emitted `reasoning_content` is invisible to app code. |
| **Connector** (parse) | SK Connectors.OpenAI 1.77.0 | **Drops `reasoning_content`.** Metadata dict has only `Id, CreatedAt, SystemFingerprint, Usage, Refusal, FinishReason, ContentTokenLogProbabilities`. No reasoning key. ← **the precise gap** |
| **Agent** (extract) | `FlAgent.cs:114-116` + `:144-154` `ExtractReasoning` | Reads **`reply.Metadata` only**. Reasoning is never there. Worse: its `Contains("reason")` substring test matches the **`FinishReason`** key (value `"Stop"`/`"ToolCalls"`, a non-empty *string* in 1.77.0) → returns the finish reason as the "thought", not reasoning. |
| **App UI** | `ChatViewModel.cs:173-179`, `ChatMessageViewModel.cs:38-60`, `ChatPanel.xaml:42-52` | **Correct.** Binds `Thought` deltas → `Thoughts`/`HasThoughts`/`ThoughtsExpanded` (collapsed-by-default disclosure). |
| **Plugin UI (#66)** | `FlAgentChatWindow.cs:305-311`, `:464-532` | **Correct.** Teal "thinking…" block consumes `Thought` deltas; toggle visible by default. Once deltas flow + it ships, it shows. |

**Net:** the model's genuine chain-of-thought is discarded by the SK connector and never
becomes an `AgentDelta(Kind=Thought)`. Both UIs already render Thought deltas, so they will
"just work" the instant `FlAgent.StreamAsync` actually produces them.

---

## The full trace (read)

### 1. Where Thought deltas come from — `FlAgent.StreamAsync`
`src/FruityLink.Agent/FlAgent.cs`

- **Execution settings** (`:84-102`): `OpenAIPromptExecutionSettings { MaxTokens=4096,
  Temperature=0.25, FunctionChoiceBehavior=Auto(parallel) }`. **No reasoning/thinking field
  of any kind.**
- **The call** (`:108-110`):
  ```csharp
  ChatMessageContent reply = await _chat!
      .GetChatMessageContentAsync(_history, settings, _kernel, ct)   // NON-streaming
      .ConfigureAwait(false);
  ```
- **Thought production** (`:114-116`) — the *only* place a Thought delta is ever made:
  ```csharp
  string? thought = ExtractReasoning(reply.Metadata);
  if (!string.IsNullOrEmpty(thought))
      yield return new AgentDelta(AgentDeltaKind.Thought, thought);
  ```
- **`ExtractReasoning`** (`:144-154`):
  ```csharp
  foreach (KeyValuePair<string, object?> kv in metadata)
  {
      if (kv.Key.Contains("reason", StringComparison.OrdinalIgnoreCase)
          && kv.Value is string s && s.Length > 0)
          return s;
  }
  return null;
  ```
  This **only** looks at `ChatMessageContent.Metadata` and **never** at `reply.Content` (no
  `<think>` parsing) — and it relies on SK having put reasoning into metadata under a
  "reason"-ish key. It hasn't (see §3). The one metadata key that *does* contain "reason" is
  `FinishReason`, whose value in SK 1.77.0 is a non-empty string (`"Stop"`/`"ToolCalls"`),
  so this function returns the **finish reason**, not reasoning — a latent bug.

`AgentDelta` / `AgentDeltaKind` are defined in `src/FruityLink.Agent/AgentDelta.cs:4-14`
(`enum { Text, Thought }`, `readonly record struct AgentDelta(AgentDeltaKind Kind, string Text)`).

The in-FL native chat path `ChatBridgeService.cs:118` uses the same non-streaming
`GetChatMessageContentAsync` and does **not** even attempt to surface thoughts (parked path).
The two live UIs (app + plugin) both call `FlAgent.StreamAsync`, so fixing it there fixes both.

### 2. Connector config — `ChatKernelFactory.cs`
`src/FruityLink.Llm/ChatKernelFactory.cs:76-81`: Ollama/OpenAI/Anthropic all go through
`builder.AddOpenAIChatCompletion(modelId, endpoint, apiKey, httpClient)`. **No reasoning /
thinking option is set** (the connector has none to set). The shared `HttpClient`
(`:31-35`) is wrapped with `LlmRetryHandler → LlmLoggingHandler → LlmToolCallRepairHandler →
HttpClientHandler` — this handler chain is the natural seam for the fix (§Fix).

Note: `LlmToolCallRepairHandler` (`src/FruityLink.Llm/Diagnostics/LlmToolCallRepairHandler.cs`)
re-buffers the JSON body and, when it rewrites tool-call args, re-serializes via
`JsonNode.ToJsonString()` (`:78`), which **preserves** unknown fields like `reasoning_content`.
So the handler chain is *not* what drops reasoning — SK's deserialization is.

### 3. The connector drops `reasoning_content` (verified)

SK `Microsoft.SemanticKernel.Connectors.OpenAI` **1.77.0** builds the metadata dict in
`ClientCore.ChatCompletion.GetChatCompletionMetadata` (verified against the `dotnet-1.77.0`
tag). Both the non-streaming and streaming variants produce exactly:

```csharp
new Dictionary<string, object?>
{
    { nameof(completions.Id), completions.Id },
    { nameof(completions.CreatedAt), completions.CreatedAt },
    { nameof(completions.SystemFingerprint), completions.SystemFingerprint },
    { nameof(completions.Usage), completions.Usage },
    { nameof(completions.Refusal), completions.Refusal },
    { nameof(completions.FinishReason), completions.FinishReason.ToString() },  // ← STRING "Stop"
    { nameof(completions.ContentTokenLogProbabilities), completions.ContentTokenLogProbabilities },
};
```

- **No `reasoning` / `reasoning_content` key.** SK's OpenAI connector deserializes into the
  official OpenAI .NET SDK's strongly-typed `ChatCompletion`, which has no property for the
  non-standard `reasoning_content` field; the value lands in the SDK's internal
  `SerializedAdditionalRawData` and is never exposed (so `ChatMessageContent.InnerContent`
  won't recover it either).
- **Confirmed by SK issue #13054** ("Unable to get Reasoning Summary/Detail … the
  `TextReasoningContent` condition never gets met"): even for OpenAI's own reasoning models,
  SK does not populate reasoning content. There is no version of the stable OpenAI connector
  that surfaces `reasoning_content` into metadata.
- **`FinishReason` is `.ToString()`** → a non-empty `string` (`"Stop"`, `"ToolCalls"`, …),
  and `"FinishReason"` contains the substring `reason` → `ExtractReasoning`'s loop matches it
  and returns it. So the current code's "thought", when anything shows at all, is the literal
  finish reason — never the model's reasoning.

### 4. The UIs are correct (no change needed)

- **App** — `ChatViewModel.SendAsync` (`:173-179`) routes `delta.Kind == Thought →
  assistant.AppendThought`; `ChatMessageViewModel` (`:38-60`) exposes `Thoughts`,
  `HasThoughts`, `ThoughtsExpanded`, `AppendThought`; `ChatPanel.xaml:42-52` binds a
  collapsible "Thoughts" disclosure (`Visibility=HasThoughts`, body `Text={Binding Thoughts}`).
- **Plugin (#66, not yet deployed)** — `FlAgentChatWindow` consumes Thought deltas at
  `:305-311` and renders the teal "thinking…" block at `:464-532` (`AppendThought`), with a
  "Thoughts" toggle checked by default (`:145-153`).

Both are wired to display Thought deltas; they simply never receive any.

### 5. System prompts — note only
`SystemPrompts.cs`: `CavemanModeEnabled = true` (default) compresses *prose + reasoning*
style; `Default`/`SubAgent` say nothing about hiding reasoning. These shape **what** the
reasoning says, not **whether** it is plumbed — they cannot be the cause of *zero* thoughts.
(Behavior is the sibling's lane.)

---

## GLM / Ollama facts (the model side)

- `glm-5.2:cloud` is an **Ollama cloud tag** that routes to **Z.ai's** GLM inference; you call
  it via Ollama's OpenAI-compatible `/v1/chat/completions` with `model="glm-5.2:cloud"`.
- **GLM reasoning** over the OpenAI-compatible API is returned as a separate
  **`reasoning_content`** field on the message — non-streaming `choices[].message.reasoning_content`,
  streaming `choices[].delta.reasoning_content` — **not** inline `<think>` tags. GLM enables
  thinking with the request param **`thinking: {"type": "enabled"}`** (default enabled on
  Z.ai/OpenRouter). Over a *generic* OpenAI-compatible endpoint this param is often not sent,
  and then the model emits no reasoning (Roo-Code issue #8547).
- **Ollama** OpenAI-compat layer: accepts `reasoning_effort` ("low"/"medium"/"high") and a
  `reasoning` request field; **auto-enables thinking** for capable models when no
  `reasoning_effort` is given; maps the model's thinking onto the response **`reasoning`**
  field, and also passes through **`reasoning_content`** for thinking-capable upstreams
  (deepseek-r1, qwen3-thinking, and Z.ai GLM). So for `glm-5.2:cloud` the response field may be
  **either `message.reasoning_content` or `message.reasoning`** — handle both.

**Conclusion on "emitting":** for `glm-5.2:cloud` via Ollama, reasoning is *probably already
being emitted* (auto-enabled). The dominant failure is parse/connector, not the model. But the
fix should also explicitly request thinking so it's deterministic and provider-agnostic.

---

## The fix (concrete, minimal surface)

Route reasoning through the **existing HTTP handler chain** (so SK never has to understand it),
then split it in `FlAgent`. Three changes; the UIs are untouched.

### Fix 1 — explicitly enable thinking on the request
`OpenAIPromptExecutionSettings` has no reasoning field, so inject it into the outgoing JSON in
a request-side branch of the handler chain (simplest: extend `LlmToolCallRepairHandler` or add
a sibling `LlmReasoningHandler`). On a `…/chat/completions` POST, parse the request body and add:

```jsonc
"reasoning_effort": "medium",         // Ollama: enables + sets depth; harmless to OpenAI o-series
"thinking": { "type": "enabled" }     // GLM/Z.ai native switch (ignored by backends that don't read it)
```

(Only add keys that are absent; leave the rest of the body intact and re-serialize.)

### Fix 2 — surface `reasoning_content`/`reasoning` into the content channel
In the **response** path (extend `LlmToolCallRepairHandler.RepairBody`, or a dedicated handler),
since this runs only for non-streaming `application/json` bodies already, for each
`root["choices"][i]["message"]`: if `reasoning_content` **or** `reasoning` is a non-empty
string, fold it into `content` wrapped in `<think>…</think>` so it rides through SK's typed
deserialization without being dropped:

```csharp
var msg = choice?["message"];
string? r = TryGetString(msg?["reasoning_content"]) ?? TryGetString(msg?["reasoning"]);
if (!string.IsNullOrEmpty(r))
{
    string content = TryGetString(msg?["content"]) ?? string.Empty;
    msg!["content"] = $"<think>{r}</think>{content}";   // prepend reasoning, keep answer
    changed = true;
}
```

Run this whenever reasoning is present (today `RepairBody` is gated on `"tool_calls"` at
`LlmToolCallRepairHandler.cs:31` — widen that gate to also fire when `reasoning_content`/
`reasoning` is present). `JsonNode.ToJsonString()` preserves everything else.

*(Alternative if you prefer not to touch `content`: emit reasoning on a side channel the agent
can correlate — but there's no per-call seam between the shared `HttpClient` and `FlAgent`, so
the `<think>` fold is the clean, self-contained route given the current architecture.)*

### Fix 3 — parse `<think>` in `FlAgent.StreamAsync`, delete the metadata path
Replace the `ExtractReasoning(reply.Metadata)` block (`FlAgent.cs:114-116`) and the
`ExtractReasoning` method (`:144-154`) with a content splitter:

```csharp
string content = reply.Content ?? string.Empty;
(string thought, string text) = SplitThink(content);   // pulls <think>…</think> out of content

if (!string.IsNullOrEmpty(thought))
    yield return new AgentDelta(AgentDeltaKind.Thought, thought);
if (!string.IsNullOrEmpty(text))
    yield return new AgentDelta(AgentDeltaKind.Text, text);
```

```csharp
private static (string Thought, string Text) SplitThink(string content)
{
    const string open = "<think>", close = "</think>";
    int a = content.IndexOf(open, StringComparison.OrdinalIgnoreCase);
    int b = content.IndexOf(close, StringComparison.OrdinalIgnoreCase);
    if (a < 0 || b < 0 || b < a) return (string.Empty, content);
    string thought = content.Substring(a + open.Length, b - (a + open.Length)).Trim();
    string text = (content.Remove(a, (b + close.Length) - a)).Trim();
    return (thought, text);
}
```

Notes:
- Delete `ExtractReasoning` — it cannot ever return real reasoning and currently risks
  surfacing the `FinishReason` string. (`EnsureInHistory` should store the **stripped** `text`,
  not the `<think>`-laden content, so reasoning isn't echoed back into history.)
- This also makes the path robust to any backend that emits inline `<think>` tags directly.

After these three changes the existing app disclosure (`ChatPanel.xaml`) and the plugin's
"thinking…" block (`FlAgentChatWindow`) light up automatically — no UI edits required.

---

## Where thoughts are lost + the exact fix

**Lost at the connector/parse boundary.** SK `Connectors.OpenAI` 1.77.0 deserializes the
response into the typed OpenAI SDK `ChatCompletion` and its metadata dict
(`ClientCore.ChatCompletion.GetChatCompletionMetadata`) carries **no** `reasoning_content` —
only `Id, CreatedAt, SystemFingerprint, Usage, Refusal, FinishReason(.ToString()),
ContentTokenLogProbabilities`. `FlAgent.ExtractReasoning` (`FlAgent.cs:144-154`) reads **only**
that metadata, so the model's `reasoning_content`/`reasoning` (which GLM-via-Ollama does emit on
`message.reasoning_content` / `message.reasoning`) is discarded before any `Thought` delta is
made — and its `Contains("reason")` test even mis-matches the `FinishReason` key. The UIs (app +
plugin #66) bind Thought deltas correctly and are not at fault.

**Exact fix:** (1) inject `reasoning_effort` / `thinking:{type:enabled}` into the request body in
the HTTP handler chain so reasoning is deterministically emitted; (2) in the response handler
(`LlmToolCallRepairHandler`) fold each `message.reasoning_content`/`reasoning` into `content` as
`<think>…</think>`; (3) in `FlAgent.StreamAsync` replace the metadata-based `ExtractReasoning`
with a `<think>` splitter that yields a `Thought` delta + a clean `Text` delta (and store the
stripped text in history). The current app + plugin thought views then render reasoning with no
further changes.
