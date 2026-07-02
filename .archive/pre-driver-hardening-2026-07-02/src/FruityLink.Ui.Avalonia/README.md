# FruityLink.Ui.Avalonia

The flagship product's **interior UI** (layer 2): the LLM-driven FL Studio co-pilot chat, built as
one cross-platform **Avalonia** (.NET / C# + XAML + Skia) codebase and themed to match the marketing
**hero** exactly. Right now it runs as a standalone desktop window; embedding this Avalonia surface
into FL Studio's native host window (layer 1) is a later step and is intentionally not attempted here.

## Run it

```
dotnet run --project src/FruityLink.Ui.Avalonia
```

(A clean build is `dotnet build src/FruityLink.Ui.Avalonia`.) The window opens with a seeded sample
conversation so the theme is visible without any backend. In `Debug`, press **F12** for the Avalonia
dev-tools inspector.

## What's here

```
Theme/Tokens.axaml     colours, brand + headline gradients, aurora glows, fonts  (mirrors docs/ui-design-tokens.md)
Theme/Controls.axaml   hero-themed styles: buttons, glass panels, bubbles, composer, toggles
Views/ChatWindow.axaml the chat shell — header, transcript, status/toggles bar, composer
ViewModels/            ChatViewModel + ChatMessage (hand-rolled MVVM, no LLM logic)
Assets/Fonts/          bundled brand fonts (see below)
```

The look: dark ink-950 base, aurora violet/magenta/cyan glow blobs behind the transcript, a glass
header with a gradient logo mark + "FL Automate" wordmark (headline gradient) + a status pill,
right-aligned **user** bubbles in the brand gradient with a soft violet glow, left-aligned **glass**
assistant bubbles carrying accent-cyan tool-call chips and a dim italic "thinking" block, and a glass
composer with a round mic button and a gradient **Send** button (turns to a magenta **Cancel** while
busy). Thoughts / Tool-calls debug toggles sit on the status bar. It matches the tokens in
`docs/ui-design-tokens.md`.

## Fonts

Both brand fonts are **bundled** in `Assets/Fonts/` and referenced via `avares://` in `Theme/Tokens.axaml`:

| File | Family (referenced as) | Role |
| --- | --- | --- |
| `SpaceGrotesk-Variable.ttf` | `Space Grotesk` | headings / display / wordmark |
| `Inter-Variable.ttf` | `Inter` | body / UI text |

Both are variable fonts (weight is selected per use site). They were fetched from the
[google/fonts](https://github.com/google/fonts) repo (SIL OFL). As a belt-and-braces fallback, the
app also calls `.WithInterFont()` (the `Avalonia.Fonts.Inter` package) so body text still renders in
Inter even if `Assets/Fonts/` is ever stripped.

**If you ever need to replace them:** drop the `.ttf`/`.otf` into `Assets/Fonts/` keeping the embedded
family name `Space Grotesk` / `Inter` (or update the `FontDisplay` / `FontBody` `avares://…#Family`
keys in `Theme/Tokens.axaml`). No csproj change is needed — `Assets/**` is globbed as `AvaloniaResource`.

## Wiring the agent later (seams — not done here)

This is a themed **shell only**; there is no LLM logic. The clean seams for wiring the real agent:

- `ChatViewModel.MessageSubmitted` (event) fires with the user's text on Send — subscribe to feed the agent.
- Append the reply as a new `ChatMessage(MessageRole.Assistant, …)` and stream tokens into its
  `Text` / `Thoughts` / `ToolCalls` (all raise change notifications, so streaming just works).
- Flip `ChatViewModel.IsBusy` for the Send↔Cancel affordance; wire the mic to `MicCommand` /
  `IsRecording` (Whisper dictation, same as the WPF window).
- Remove the placeholder reply block in `ChatViewModel.OnSendOrCancel`.

Next steps to reach parity: (1) wire `FruityLink.Agent` streaming into the seams above; (2) host/embed
this Avalonia surface inside FL Studio's window (layer 1).
```
