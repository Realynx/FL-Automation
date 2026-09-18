using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// FL's global transport toggles: metronome, countdown (precount), wait-for-input, loop record and overdub.
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
//
// Recovered 2026-09-18 from FL's own TShortcutsModule toggle Actions, which are all the same shape:
//
//     obj    = *(*(TransportOptionsPtr));              // .data -> .bss indirection, then the heap manager
//     target = obj[TransportOptionsFieldOffset];       // the options object the setters live on
//     next   = (*<toggle>StatePtr == 0);               // FL always writes the negation of the current value
//     (*target)[<toggle>SetterSlot](target, next);     // FL's own setter, by vtable slot
//
// so one signature per toggle yields both its state pointer and its setter's vtable slot, and the shared
// manager slot/field offset come from the metronome copy. The state pointers agree exactly with the globals
// FL's own ui.isMetronomeEnabled / isPrecountEnabled / isStartOnInputEnabled / isLoopRecEnabled read, which
// is what proves they are the right globals rather than merely unique matches. Writes go through FL's own
// setter on FL's main thread, never by poking the bool, so FL's bookkeeping (toolbar repaint, menu
// checkmarks, engine side effects) runs exactly as it does for a click.
//
// Why an autonomous agent needs these: countdown and wait-for-input silently break a recording pass (FL
// waits for a count-in or for input before it starts), loop record changes what a pass writes (takes), and
// the metronome bleeds into a live capture. Live audio capture sets all four for itself and restores them.
public sealed partial class FlInjectBridge
{
    private const string TransportOptionsPtrSymbol = "TransportOptionsPtr";
    private const string TransportOptionsFieldSymbol = "TransportOptionsFieldOffset";

    /// <summary>One toggle: the symbol naming its state pointer and the symbol naming its setter's vtable slot.</summary>
    private sealed record TransportToggle(string Name, string StateSymbol, string SetterSlotSymbol);

    private static readonly TransportToggle Metronome = new("metronome", "MetronomeStatePtr", "MetronomeSetterSlot");
    private static readonly TransportToggle Countdown = new("countdown", "PrecountStatePtr", "PrecountSetterSlot");
    private static readonly TransportToggle WaitForInput = new("wait_for_input", "WaitForInputStatePtr", "WaitForInputSetterSlot");
    private static readonly TransportToggle LoopRecord = new("loop_record", "LoopRecordStatePtr", "LoopRecordSetterSlot");
    private static readonly TransportToggle BlendRecordedNotes = new("blend_recorded_notes", "BlendRecordedStatePtr", "BlendRecordedSetterSlot");

    /// <summary>The bool behind a toggle. The resolved slot holds a pointer to it, and both live in FL's own
    /// image, so a mis-resolved symbol is refused instead of read.</summary>
    private async Task<ulong> ToggleFieldAsync(TransportToggle toggle, CancellationToken ct)
    {
        ulong slot = await ResolveSymbolAddressAsync(toggle.StateSymbol, ct);
        EnsureInModule($"{toggle.Name} state pointer slot", slot);
        ulong field = BitConverter.ToUInt64(await PeekAbsAsync(slot, 8, ct), 0);
        EnsureInModule($"{toggle.Name} state", field);
        return field;
    }

    private async Task<bool> GetToggleAsync(TransportToggle toggle, CancellationToken ct)
        => (await PeekAbsAsync(await ToggleFieldAsync(toggle, ct), 1, ct))[0] != 0;

    /// <summary>FL's options object: <c>*(*(slot))</c> then its field. The indirection is inside the image and
    /// the objects are not, so a wrong deref depth fails loudly rather than handing a bogus vtable to a call.</summary>
    private async Task<ulong> TransportOptionsAsync(CancellationToken ct)
    {
        ulong slot = await ResolveSymbolAddressAsync(TransportOptionsPtrSymbol, ct);
        EnsureInModule("Transport-options pointer slot", slot);
        ulong indirection = BitConverter.ToUInt64(await PeekAbsAsync(slot, 8, ct), 0);
        EnsureInModule("Transport-options indirection slot", indirection);
        ulong manager = BitConverter.ToUInt64(await PeekAbsAsync(indirection, 8, ct), 0);
        if (manager == 0 || IsInModule(manager))
            throw new InvalidOperationException("FL's transport-options manager is not up yet; the toggle cannot be set.");
        ulong offset = await ResolveSymbolAddressAsync(TransportOptionsFieldSymbol, ct);
        ulong target = BitConverter.ToUInt64(await PeekAbsAsync(manager + offset, 8, ct), 0);
        if (target == 0 || IsInModule(target))
            throw new InvalidOperationException("FL's transport-options object is not built yet; the toggle cannot be set.");
        return target;
    }

    private async Task SetToggleAsync(TransportToggle toggle, bool on, CancellationToken ct)
    {
        LogOp("SetTransportToggle", $"{toggle.Name}={on}");
        ulong field = await ToggleFieldAsync(toggle, ct);
        if (((await PeekAbsAsync(field, 1, ct))[0] != 0) == on) return;   // FL's own handler is a no-op too
        ulong target = await TransportOptionsAsync(ct);
        ulong vtable = BitConverter.ToUInt64(await PeekAbsAsync(target, 8, ct), 0);
        EnsureInModule($"{toggle.Name} setter vtable", vtable);
        ulong slotOffset = await ResolveSymbolAddressAsync(toggle.SetterSlotSymbol, ct);
        ulong setter = BitConverter.ToUInt64(await PeekAbsAsync(vtable + slotOffset, 8, ct), 0);
        EnsureInModule($"{toggle.Name} setter", setter);
        await CallAbsAsync(setter, new[] { target, on ? 1UL : 0UL }, ct);
        if (((await PeekAbsAsync(field, 1, ct))[0] != 0) != on)
            throw new InvalidOperationException(
                $"FL's {toggle.Name} toggle read back unchanged after it was set to {on}; FL refused the change.");
    }

    /// <summary><see cref="INativeFlControl.GetMetronomeAsync"/></summary>
    public Task<bool> GetMetronomeAsync(CancellationToken ct = default) => GetToggleAsync(Metronome, ct);
    /// <summary><see cref="INativeFlControl.SetMetronomeAsync"/></summary>
    public Task SetMetronomeAsync(bool on, CancellationToken ct = default) => SetToggleAsync(Metronome, on, ct);
    /// <summary><see cref="INativeFlControl.GetCountdownAsync"/></summary>
    public Task<bool> GetCountdownAsync(CancellationToken ct = default) => GetToggleAsync(Countdown, ct);
    /// <summary><see cref="INativeFlControl.SetCountdownAsync"/></summary>
    public Task SetCountdownAsync(bool on, CancellationToken ct = default) => SetToggleAsync(Countdown, on, ct);
    /// <summary><see cref="INativeFlControl.GetWaitForInputAsync"/></summary>
    public Task<bool> GetWaitForInputAsync(CancellationToken ct = default) => GetToggleAsync(WaitForInput, ct);
    /// <summary><see cref="INativeFlControl.SetWaitForInputAsync"/></summary>
    public Task SetWaitForInputAsync(bool on, CancellationToken ct = default) => SetToggleAsync(WaitForInput, on, ct);
    /// <summary><see cref="INativeFlControl.GetLoopRecordAsync"/></summary>
    public Task<bool> GetLoopRecordAsync(CancellationToken ct = default) => GetToggleAsync(LoopRecord, ct);
    /// <summary><see cref="INativeFlControl.SetLoopRecordAsync"/></summary>
    public Task SetLoopRecordAsync(bool on, CancellationToken ct = default) => SetToggleAsync(LoopRecord, on, ct);
    /// <summary><see cref="INativeFlControl.GetBlendRecordedNotesAsync"/></summary>
    public Task<bool> GetBlendRecordedNotesAsync(CancellationToken ct = default) => GetToggleAsync(BlendRecordedNotes, ct);
    /// <summary><see cref="INativeFlControl.SetBlendRecordedNotesAsync"/></summary>
    public Task SetBlendRecordedNotesAsync(bool on, CancellationToken ct = default) => SetToggleAsync(BlendRecordedNotes, on, ct);
}
