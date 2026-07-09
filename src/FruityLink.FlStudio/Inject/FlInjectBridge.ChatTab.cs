using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// In-FL chat tab comms (native bridge commands).
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
public sealed partial class FlInjectBridge
{
    // ============================ In-FL chat tab (native browser tab; re/14) ============================
    // (The Stage-A managed clone-tab experiment — CreateChatTabAsync — was superseded by the native
    // chattab_open/chattab_close bridge commands below; its recipe is preserved in re/14 §4.)

    // ---- in-FL chat tab comms (bridge commands implemented natively in FlBridge.dll) ----
    /// <summary>Open (or focus) the native "FruityLink AI" browser tab.</summary>
    public Task OpenChatTabAsync(CancellationToken ct = default) => RawAsync("chattab_open", 8000, ct);
    /// <summary>Hide the chat tab + restore the browser hook.</summary>
    public Task CloseChatTabAsync(CancellationToken ct = default) => RawAsync("chattab_close", 8000, ct);
    /// <summary>Returns the user's submitted chat message (and clears it), or empty if none pending.</summary>
    public async Task<string> ChatPollAsync(CancellationToken ct = default) => (await RawAsync("chat_poll", 4000, ct)).Trim();
    /// <summary>Append a line to the chat display (the bridge runs it on FL's main thread).</summary>
    public Task ChatSayAsync(string text, CancellationToken ct = default)
        => RawAsync("chat_say " + (text ?? string.Empty), 8000, ct);
}
