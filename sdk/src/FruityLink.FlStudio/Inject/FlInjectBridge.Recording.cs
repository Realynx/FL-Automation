using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// FL's recording preconditions: the global recording filter (the bitmask behind the record button's
// right-click "Recording filter" submenu that decides what a recording pass may capture) and whether the
// transport record button is engaged. Live audio capture depends on both and sets both for itself.
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
//
// Recovered 2026-09-17 (static pass over the installed FLEngine_x64.dll of 26.1.3.5570 and 25.2.5.5319).
// The registry name RecordingFilter2 appears exactly once in the image, inside FL's settings read/write
// table, paired with a .data slot that holds a pointer to one int32; the registry is only consulted at FL
// start and rewritten at exit, so the live value has to come from that int. The int is reached from
// TFruityLoopsMainForm.Recfilter1MenuClick, the one OnClick every filter menu item shares:
//
//     flags = item.Checked ? filter | item.Tag : filter & ~item.Tag;  SetRecordingFilter(flags)
//
// so one signature over that handler yields both the pointer slot and FL's own setter (which stores the
// int and refreshes the menu checkmarks). The bits are the menu items' DFM tags, which is where the
// meaning below is from, not from guessing: Recfilter1Menu Tag 1 "&Automation", Recfilter2Menu Tag 2
// "&Notes", Recfilter3Menu Tag 4 "A&udio", Recfilter4Menu Tag 8 "&Clips" (FL 2025 has no Clips item).
public sealed partial class FlInjectBridge
{
    private const string RecordingFilterPtrSymbol = "RecordingFilterPtr";
    private const string SetRecordingFilterSymbol = "FLrec_SetRecordingFilter";
    private const string RecordingActiveCountSymbol = "RecordingActiveCountPtr";
    private const string RecordButtonFormSymbol = "RecordButtonFormPtr";
    private const string RecordButtonOffsetSymbol = "RecordButtonOffset";
    private const string RecordButtonPressedOffsetSymbol = "RecordButtonPressedOffset";

    /// <summary>Every bit FL's own "Recording filter" submenu can set; anything else is refused rather than
    /// written, because FL persists whatever is in this global to the registry when it exits.</summary>
    internal const int RecordingFilterKnownBits = 0xF;

    /// <summary>The live int behind FL's recording filter: the .data slot resolved from FL's own menu
    /// handler holds a pointer to it. Both the slot and the target are checked against the loaded engine
    /// image so a mis-resolved symbol is reported instead of read from (or written to) blindly.</summary>
    private async Task<ulong> RecordingFilterFieldAsync(CancellationToken ct)
    {
        ulong slot = await ResolveSymbolAddressAsync(RecordingFilterPtrSymbol, ct);
        EnsureInModule("Recording-filter pointer slot", slot);
        ulong field = BitConverter.ToUInt64(await PeekAbsAsync(slot, 8, ct), 0);
        EnsureInModule("Recording-filter value", field);
        return field;
    }

    /// <summary><see cref="INativeFlControl.GetRecordingActiveAsync"/>: the engine's recording-pass counter,
    /// which is what FL's own apply-recording-filter routine gates on. Independent of the toolbar button, so it
    /// answers "is FL actually recording" even when the button's state is being argued over.</summary>
    public async Task<bool> GetRecordingActiveAsync(CancellationToken ct = default)
    {
        ulong slot = await ResolveSymbolAddressAsync(RecordingActiveCountSymbol, ct);
        EnsureInModule("Recording-state counter slot", slot);
        ulong field = BitConverter.ToUInt64(await PeekAbsAsync(slot, 8, ct), 0);
        EnsureInModule("Recording-state counter", field);
        return BitConverter.ToInt32(await PeekAbsAsync(field, 4, ct), 0) > 0;
    }

    /// <summary><see cref="INativeFlControl.GetRecordPressedAsync"/>: the record button's pressed byte, read
    /// exactly the way FL's own <c>ui.isRecording</c> reads it (toolbar form -> record button -> state byte).
    /// Both field offsets come from the same signature as the form pointer, so nothing is hardcoded here.</summary>
    public async Task<bool> GetRecordPressedAsync(CancellationToken ct = default)
    {
        // The toolbar form is reached through TWO loads, not one: `mov rax,[rip+d]; mov rax,[rax]`. The first
        // lands on FL's .data -> .bss indirection slot, the second on the live heap form. Reading only the first
        // (the bug this fixes, RE 2026-09-18) indexes +0x788 into module data and returns an unrelated byte --
        // live it read true with the record button dark and false with it lit. The in-module / out-of-module
        // assertions below make a wrong deref depth fail loudly instead of returning noise.
        ulong slot = await ResolveSymbolAddressAsync(RecordButtonFormSymbol, ct);
        EnsureInModule("Record-button form pointer slot", slot);
        ulong indirection = BitConverter.ToUInt64(await PeekAbsAsync(slot, 8, ct), 0);
        EnsureInModule("Record-button form indirection slot", indirection);
        ulong form = BitConverter.ToUInt64(await PeekAbsAsync(indirection, 8, ct), 0);
        if (form == 0) throw new InvalidOperationException("FL's toolbar form is not up yet; the record state cannot be read.");
        if (IsInModule(form))
            throw new InvalidOperationException(
                $"FL's toolbar form resolved to 0x{form:x}, which is inside FLEngine rather than the heap; the record-button symbol does not describe this build.");
        ulong button = BitConverter.ToUInt64(
            await PeekAbsAsync(form + await ResolveSymbolAddressAsync(RecordButtonOffsetSymbol, ct), 8, ct), 0);
        if (button == 0 || IsInModule(button))
            throw new InvalidOperationException("FL's transport record button is not built yet; the record state cannot be read.");
        return (await PeekAbsAsync(button + await ResolveSymbolAddressAsync(RecordButtonPressedOffsetSymbol, ct), 1, ct))[0] != 0;
    }

    /// <summary><see cref="INativeFlControl.GetRecordingFilterAsync"/>: read the live bitmask.</summary>
    public async Task<int> GetRecordingFilterAsync(CancellationToken ct = default)
        => BitConverter.ToInt32(await PeekAbsAsync(await RecordingFilterFieldAsync(ct), 4, ct), 0);

    /// <summary><see cref="INativeFlControl.SetRecordingFilterAsync"/>: invoke FL's own setter on FL's main
    /// thread (exactly what clicking a "Recording filter" menu item does, so the checkmarks follow), then
    /// re-read the value so a refused write is reported instead of assumed. No call when it already matches.</summary>
    public async Task SetRecordingFilterAsync(int flags, CancellationToken ct = default)
    {
        if ((flags & ~RecordingFilterKnownBits) != 0 || flags < 0)
            throw new ArgumentOutOfRangeException(nameof(flags), flags,
                $"The recording filter only has the bits FL's own menu sets (1 Automation, 2 Notes, 4 Audio, 8 Clips); 0..{RecordingFilterKnownBits} is the whole range.");
        LogOp("SetRecordingFilter", $"flags={flags}");
        ulong field = await RecordingFilterFieldAsync(ct);
        if (BitConverter.ToInt32(await PeekAbsAsync(field, 4, ct), 0) == flags) return;
        await CallAsync("sym:" + SetRecordingFilterSymbol, new[] { (ulong)(uint)flags }, ct);
        int readBack = BitConverter.ToInt32(await PeekAbsAsync(field, 4, ct), 0);
        if (readBack != flags)
            throw new InvalidOperationException(
                $"FL's recording filter read back {readBack} after it was set to {flags}; the setter did not take. " +
                "Check the record button's right-click Recording filter submenu before retrying.");
    }
}
