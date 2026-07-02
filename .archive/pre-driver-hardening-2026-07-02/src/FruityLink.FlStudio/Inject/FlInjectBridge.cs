using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

/// <summary>
/// C# client for the injected <c>FlBridge.dll</c> (named pipe <c>\\.\pipe\FruityLinkBridge</c>).
///
/// The bridge is a THIN executor: it runs raw calls / memory reads on FL Studio's main (UI)
/// thread, SEH-guarded. This side holds the reverse-engineered command map and exposes typed
/// control operations. Each request opens a short-lived connection (the bridge serves one
/// client at a time). See <c>re/08-command-bus.md</c>.
///
/// Control goes through FL's central command bus <c>FL_DispatchCommand(cmdId, value, flags)</c>
/// (Ghidra addr 0xF53FE0). cmdId encodes the target: global params = 0x4000xxxx; channel params =
/// channelBase + idx + 0x8000; mixer FX params = mixerEffectParamBase(track,slot) + idx + 0x70008000.
/// </summary>
public sealed class FlInjectBridge : FruityLink.Core.Abstractions.INativeFlControl
{
    public const string PipeName = "FruityLinkBridge";

    /// <summary>Ghidra address of FL_DispatchCommand; the bridge maps it to the live FLEngine base.</summary>
    private const string CmdBusAddr = "f53fe0";

    // Dispatch flags observed on the FL_DispatchCommand call sites.
    public const uint FlagWheelOrScript = 0x3DD;
    public const uint FlagSlider = 0x185;

    // Generic param protocol on the command bus (live-verified): GET returns current value; SET writes it.
    public const uint FlagSet = 0x11; // bit0 = SET
    public const uint FlagGet = 0x02; // bit1 = GET (returns current value in RAX)

    // Global command ids (0x4000xxxx) — the complete master/global group, all live-verified.
    public const uint CmdMasterVolume = 0x40000000; // 0..12800 (~7624 = 0 dB-ish default)
    public const uint CmdShuffle      = 0x40000001; // 0..128 swing
    public const uint CmdMasterPitch  = 0x40000002; // cents, -1200..+1200
    public const uint CmdSetTempo     = 0x40000005; // value = BPM * 1000 (10000..0x7F710)

    // ---- low-level transport -------------------------------------------------

    // Serializes all bridge I/O. The bridge runs each command on FL's main (UI) thread and serves
    // one caller at a time, so concurrent callers — e.g. parallel sub-agents — must not collide on it.
    private readonly SemaphoreSlim _pipeGate = new(1, 1);

    /// <summary>
    /// Pluggable command transport. When null (default), commands go over the named pipe to the
    /// injected bridge. When the bridge is hosted IN-PROCESS (the version.dll proxy → CLR-host
    /// install), <see cref="UseInProcessTransport"/> routes commands through a direct P/Invoke of
    /// the bridge's <c>FlBridge_Command</c> export — same protocol, same process, no named pipe.
    /// Static so it applies to every <see cref="FlInjectBridge"/> instance the DI container makes.
    /// </summary>
    public static Func<string, int, CancellationToken, Task<string>>? Transport { get; set; }

    /// <summary>Route all bridge commands through the in-process P/Invoke transport (no named pipe).
    /// Called by FruityLink.Host once it is running inside FL Studio's process.</summary>
    public static void UseInProcessTransport() => Transport = InProcBridge.RawAsync;

    /// <summary>Send a raw bridge command and return its UTF-8 response. INTERNAL (capability lockdown,
    /// re/17 #50): raw command/memory access is not part of the plugin-facing surface — third-party
    /// plugins receive only the typed <see cref="INativeFlControl"/>, so they cannot reach poke/call by
    /// casting to this concrete type. Exposed to the in-proc host/mcp via InternalsVisibleTo.</summary>
    internal async Task<string> RawAsync(string message, int timeoutMs = 4000, CancellationToken ct = default)
    {
        await _pipeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // In-process transport (proxy/CLR-host install): bypass the named pipe entirely.
            var transport = Transport;
            if (transport is not null)
                return await transport(message, timeoutMs, ct).ConfigureAwait(false);

            // Bound the ENTIRE pipe exchange (connect + write + read) by timeoutMs, not just the
            // connect. The bridge runs each command on FL's main thread; if that thread is wedged
            // (a modal dialog, or an FL-internal repaint/message storm) the response never comes and
            // the read would otherwise block until ct — hanging the agent turn. A linked CTS turns
            // "no response in timeoutMs" into a bounded TimeoutException so the tool fails fast.
            using var timeoutCts = timeoutMs > 0 ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
            timeoutCts?.CancelAfter(timeoutMs);
            var io = timeoutCts?.Token ?? ct;
            try
            {
                await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(timeoutMs, io);
                pipe.ReadMode = PipeTransmissionMode.Message;
                byte[] outb = Encoding.UTF8.GetBytes(message);
                await pipe.WriteAsync(outb, io);
                await pipe.FlushAsync(io);

                var sb = new StringBuilder();
                var buf = new byte[16384];
                do
                {
                    int n = await pipe.ReadAsync(buf, io);
                    if (n > 0) sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                } while (!pipe.IsMessageComplete);
                return sb.ToString();
            }
            catch (OperationCanceledException) when (timeoutCts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"FL Studio's native bridge did not respond within {timeoutMs} ms (it may be busy or showing a dialog): {message}");
            }
        }
        finally { _pipeGate.Release(); }
    }

    /// <summary>True if the bridge is injected and its worker is responding.</summary>
    public async Task<bool> IsLoadedAsync(CancellationToken ct = default)
    {
        try { return (await RawAsync("ping", 1200, ct)).Trim() == "pong"; }
        catch { return false; }
    }

    /// <summary>INativeFlControl: same as <see cref="IsLoadedAsync"/>.</summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => IsLoadedAsync(ct);

    /// <summary>Engine info: { pid, bridgeBase, flEngineBase, flEngineSize, mainTid }.</summary>
    public Task<string> InfoAsync(CancellationToken ct = default) => RawAsync("info", 2000, ct);

    /// <summary>
    /// Call a function (by its Ghidra address) on FL's main thread with up to 4 integer args.
    /// Returns RAX. Throws if the call SEH-faulted inside FL (ok:0).
    /// </summary>
    internal async Task<ulong> CallAsync(string ghidraHexAddr, ulong[] args, CancellationToken ct = default)
    {
        string msg = "call " + ghidraHexAddr + string.Concat(args.Select(a => " " + a.ToString("x")));
        string resp = await RawAsync(msg, 5000, ct);
        using var doc = JsonDocument.Parse(resp);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.GetInt32() == 0)
            throw new InvalidOperationException($"bridge call faulted (ok:0): {msg}");
        string ret = root.GetProperty("ret").GetString() ?? "0x0";
        return Convert.ToUInt64(ret.Replace("0x", string.Empty), 16);
    }

    /// <summary>Read <paramref name="len"/> bytes at a Ghidra address (mapped to the live FLEngine base).</summary>
    internal async Task<byte[]> PeekAsync(string ghidraHexAddr, int len, CancellationToken ct = default)
    {
        string hex = (await RawAsync($"peek {ghidraHexAddr} {len}", 4000, ct)).Trim();
        if (hex.StartsWith("err", StringComparison.Ordinal)) throw new InvalidOperationException($"peek failed: {hex}");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    /// <summary>Write raw bytes at a Ghidra address (direct struct edit; use only where no FL command exists).</summary>
    internal async Task PokeAsync(string ghidraHexAddr, byte[] data, CancellationToken ct = default)
    {
        string hex = Convert.ToHexString(data).ToLowerInvariant();
        string resp = await RawAsync($"poke {ghidraHexAddr} {hex}", 4000, ct);
        using var doc = JsonDocument.Parse(resp);
        if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetInt32() == 0)
            throw new InvalidOperationException($"poke failed at {ghidraHexAddr}: {resp}");
    }

    /// <summary>Write raw bytes at an absolute live address (e.g. a resolved heap-object field).</summary>
    internal async Task PokeAbsAsync(ulong absAddr, byte[] data, CancellationToken ct = default)
    {
        string hex = Convert.ToHexString(data).ToLowerInvariant();
        string resp = await RawAsync($"pokeabs {absAddr:x} {hex}", 4000, ct);
        using var doc = JsonDocument.Parse(resp);
        if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetInt32() == 0)
            throw new InvalidOperationException($"pokeabs failed at 0x{absAddr:x}: {resp}");
    }

    // ---- refresh helpers (safe; no audio side effects) -----------------------
    /// <summary>Repaint the channel rack + piano roll (FUN_0107EAD0).</summary>
    private Task RefreshRackAsync(CancellationToken ct) => CallAsync("107ead0", Array.Empty<ulong>(), ct);

    /// <summary>Safe pattern refresh after note edits: rebuild pattern + notify changed + refresh editors + repaint.
    /// Never use FUN_0107EB90 (corrupts audio) or d51810 (markers only — won't repaint notes).</summary>
    private async Task RefreshPatternAsync(int patIdx, CancellationToken ct)
    {
        await CallAsync("11d4140", new ulong[] { (uint)patIdx, 1 }, ct);  // rebuild pattern (length + playlist block)
        await CallAsync("f53d30", new ulong[] { (uint)patIdx }, ct);     // notify pattern changed
        await CallAsync("d421c0", Array.Empty<ulong>(), ct);             // refresh editor views
        await RefreshRackAsync(ct);
    }

    // ---- typed control ops (grows as the RE catalog lands) -------------------

    /// <summary>FL_DispatchCommand(cmdId, value, flags) on the main thread.</summary>
    public Task DispatchCommandAsync(uint cmdId, ulong value, uint flags = FlagWheelOrScript, CancellationToken ct = default)
        => CallAsync(CmdBusAddr, new ulong[] { cmdId, value, flags }, ct);

    /// <summary>Read a parameter's current value via the bus GET protocol (flags 0x2). Live-verified.</summary>
    public async Task<long> GetParamAsync(uint cmdId, CancellationToken ct = default)
        => (long)await CallAsync(CmdBusAddr, new ulong[] { cmdId, 0, FlagGet }, ct);

    /// <summary>Set a parameter's value via the bus SET protocol (flags 0x11). Live-verified.</summary>
    public Task SetParamAsync(uint cmdId, long value, CancellationToken ct = default)
        => DispatchCommandAsync(cmdId, unchecked((ulong)value), FlagSet, ct);

    /// <summary>Set project tempo in BPM (10..522). Live-verified.</summary>
    public Task SetTempoAsync(double bpm, CancellationToken ct = default)
        => SetParamAsync(CmdSetTempo, (long)Math.Round(Math.Clamp(bpm, 10.0, 522.0) * 1000.0), ct);

    /// <summary>Get project tempo in BPM.</summary>
    public async Task<double> GetTempoAsync(CancellationToken ct = default)
        => await GetParamAsync(CmdSetTempo, ct) / 1000.0;

    /// <summary>Master volume, 0..12800 (≈7624 default). Live-verified.</summary>
    public Task SetMasterVolumeAsync(int value, CancellationToken ct = default)
        => SetParamAsync(CmdMasterVolume, Math.Clamp(value, 0, 12800), ct);

    /// <summary>Master pitch in cents, -1200..+1200. Live-verified.</summary>
    public Task SetMasterPitchAsync(int cents, CancellationToken ct = default)
        => SetParamAsync(CmdMasterPitch, Math.Clamp(cents, -1200, 1200), ct);

    /// <summary>Global shuffle/swing, 0..128. Live-verified.</summary>
    public Task SetShuffleAsync(int value, CancellationToken ct = default)
        => SetParamAsync(CmdShuffle, Math.Clamp(value, 0, 128), ct);

    /// <summary>Set a channel-plugin parameter value (paramId = channelBase + index + 0x8000).</summary>
    public Task SetChannelParamAsync(uint paramId, ulong fixedValue, CancellationToken ct = default)
        => DispatchCommandAsync(paramId, fixedValue, FlagWheelOrScript, ct);

    // ---- mixer (param-id protocol; track volume live-verified) ----------------
    // base(track,slot) = (track*0x40 + slot) << 16. Track controls live in slot 0's namespace.
    public const uint MixerVolOffset = 0x70001FC0, MixerPanOffset = 0x70001FC1, MixerStereoSepOffset = 0x70001FC2;

    /// <summary>Mixer track-control param id (track 0 = master); add a control offset to the track base.</summary>
    public static uint MixerTrackParamId(int track, uint controlOffset) => (uint)((track * 0x40) << 16) + controlOffset;

    /// <summary>Mixer FX-slot plugin param id (paramIndex within an FX slot).</summary>
    public static uint MixerFxParamId(int track, int slot, int paramIndex)
        => (uint)(((track * 0x40 + slot) << 16) + paramIndex) + 0x70008000u;

    /// <summary>Set a mixer track volume 0..12800 (track 0 = master). Live-verified.</summary>
    public Task SetMixerVolumeAsync(int track, int value, CancellationToken ct = default)
        => SetParamAsync(MixerTrackParamId(track, MixerVolOffset), Math.Clamp(value, 0, 12800), ct);

    /// <summary>Read a mixer track volume 0..12800.</summary>
    public Task<long> GetMixerVolumeAsync(int track, CancellationToken ct = default)
        => GetParamAsync(MixerTrackParamId(track, MixerVolOffset), ct);

    /// <summary>Set a mixer track pan 0..12800 (6400 = center).</summary>
    public Task SetMixerPanAsync(int track, int value, CancellationToken ct = default)
        => SetParamAsync(MixerTrackParamId(track, MixerPanOffset), Math.Clamp(value, 0, 12800), ct);

    /// <summary>Set a mixer FX-slot plugin parameter (normalized fixed-point value).</summary>
    public Task SetMixerFxParamAsync(int track, int slot, int paramIndex, long value, CancellationToken ct = default)
        => SetParamAsync(MixerFxParamId(track, slot, paramIndex), value, ct);

    // ---- channel rack (cmd = (channel<<16) + paramIndex; recTag==index for normal projects) -----
    // Live-verified: vol/pan/pitch/mute on channels 0..3.
    public const uint ChanVol = 0, ChanPan = 1, ChanPitch = 4, ChanMute = 7, ChanFxRoute = 8;

    /// <summary>Channel param id. NOTE: assumes recTag==channel index (true for unreordered projects);
    /// for reordered/deleted-channel projects, resolve the channel's recEventId first.</summary>
    public static uint ChannelParamId(int channel, uint paramIndex) => (uint)(channel << 16) + paramIndex;

    /// <summary>Set channel volume 0..12800 (10000 = default 78%). Live-verified.</summary>
    public Task SetChannelVolumeAsync(int channel, int value, CancellationToken ct = default)
        => SetParamAsync(ChannelParamId(channel, ChanVol), Math.Clamp(value, 0, 12800), ct);

    /// <summary>Get channel volume 0..12800.</summary>
    public Task<long> GetChannelVolumeAsync(int channel, CancellationToken ct = default)
        => GetParamAsync(ChannelParamId(channel, ChanVol), ct);

    /// <summary>Set channel pan 0..12800 (6400 = center). Live-verified.</summary>
    public Task SetChannelPanAsync(int channel, int value, CancellationToken ct = default)
        => SetParamAsync(ChannelParamId(channel, ChanPan), Math.Clamp(value, 0, 12800), ct);

    /// <summary>Set channel pitch in cents (0 = center). Live-verified.</summary>
    public Task SetChannelPitchAsync(int channel, int cents, CancellationToken ct = default)
        => SetParamAsync(ChannelParamId(channel, ChanPitch), cents, ct);

    /// <summary>Mute/unmute a channel (engine "enabled" flag: 1=unmuted). Live-verified.</summary>
    public Task SetChannelMutedAsync(int channel, bool muted, CancellationToken ct = default)
        => SetParamAsync(ChannelParamId(channel, ChanMute), muted ? 0 : 1, ct);

    /// <summary>Route a channel to a mixer track (0..125). </summary>
    public Task SetChannelFxRouteAsync(int channel, int mixerTrack, CancellationToken ct = default)
        => SetParamAsync(ChannelParamId(channel, ChanFxRoute), Math.Clamp(mixerTrack, 0, 500), ct);

    // ---- low-level helpers for struct/out-param calls (scratch + absolute peek/call) ----
    private async Task<ulong> ScratchAsync(CancellationToken ct = default)
        => Convert.ToUInt64((await RawAsync("scratch", 2000, ct)).Trim().Replace("0x", string.Empty), 16);

    private async Task<byte[]> PeekAbsAsync(ulong addr, int len, CancellationToken ct = default)
    {
        string hex = (await RawAsync($"peekabs {addr:x} {len}", 4000, ct)).Trim();
        if (hex.StartsWith("err", StringComparison.Ordinal)) throw new InvalidOperationException($"peekabs failed: {hex}");
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }

    private async Task<ulong> CallAbsAsync(ulong addr, ulong[] args, CancellationToken ct = default, int timeoutMs = 5000)
    {
        string msg = "callabs " + addr.ToString("x") + string.Concat(args.Select(a => " " + a.ToString("x")));
        string resp = await RawAsync(msg, timeoutMs, ct);
        using var doc = JsonDocument.Parse(resp);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.GetInt32() == 0)
            throw new InvalidOperationException($"callabs faulted (ok:0): {msg}");
        return Convert.ToUInt64((root.GetProperty("ret").GetString() ?? "0x0").Replace("0x", string.Empty), 16);
    }

    /// <summary>XMM-capable call (loads args into GP + XMM0-3 via the bridge thunk). Returns RAX and the
    /// raw 8-byte XMM0 (use <see cref="XmmFloat"/>/<see cref="XmmDouble"/>). For float-arg/float-return engine fns.</summary>
    private async Task<(ulong rax, ulong xmm0)> CallFAbsAsync(ulong addr, ulong[] argBits, CancellationToken ct = default)
    {
        string msg = "callfabs " + addr.ToString("x") + string.Concat(argBits.Select(a => " " + a.ToString("x")));
        string resp = await RawAsync(msg, 5000, ct);
        using var doc = JsonDocument.Parse(resp);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.GetInt32() == 0)
            throw new InvalidOperationException($"callfabs faulted (ok:0): {msg}");
        ulong rax = Convert.ToUInt64((root.GetProperty("ret").GetString() ?? "0x0").Replace("0x", string.Empty), 16);
        ulong xmm = Convert.ToUInt64((root.GetProperty("xmm0").GetString() ?? "0x0").Replace("0x", string.Empty), 16);
        return (rax, xmm);
    }

    /// <summary>Encode a double as its 64-bit pattern for passing to <see cref="CallFAbsAsync"/>.</summary>
    private static ulong Bits(double d) => unchecked((ulong)BitConverter.DoubleToInt64Bits(d));
    /// <summary>Interpret the low 32 bits of an XMM0 result as a 32-bit float (Win64 float returns).</summary>
    private static float XmmFloat(ulong xmm0) => BitConverter.Int32BitsToSingle(unchecked((int)(uint)xmm0));
    /// <summary>Interpret an XMM0 result as a 64-bit double.</summary>
    private static double XmmDouble(ulong xmm0) => BitConverter.Int64BitsToDouble(unchecked((long)xmm0));

    // ---- piano-roll notes (current pattern) ----------------------------------
    // Current pattern note store: arr = *(*(0x14ABA80)+0xC40)+0x57C ; arr is a vtable object whose
    // Add(NoteRec*) is at vtbl+8. NoteRec = 24 bytes (FLP note format). Refresh via 0xD37800 + 0xD51810.

    /// <summary>
    /// Add a note to a pattern's piano roll via the per-pattern note recorder (auto-creates it —
    /// no UI needed). <paramref name="pattern"/> is the 1-based pattern index, or &lt;= 0 for the
    /// current pattern. key = MIDI 0..131 (60 = middle C), startTick/lengthTick in PPQ ticks
    /// (see <see cref="GetPpqAsync"/>), velocity 0..127. Live-verified.
    /// </summary>
    public Task AddNoteAsync(int pattern, int channel, int key, int startTick, int lengthTick, int velocity, CancellationToken ct = default)
        => AddNotesAsync(pattern, new[] { new NoteSpec(channel, key, startTick, lengthTick, velocity) }, ct);

    /// <summary>
    /// Batch note authoring: resolve the pattern's note store and refresh the editor ONCE for the whole
    /// set (only the two per-note record calls scale with note count). Each note carries its own channel.
    /// </summary>
    public async Task AddNotesAsync(int pattern, IReadOnlyList<NoteSpec> notes, CancellationToken ct = default)
    {
        if (notes is null || notes.Count == 0) return;

        int patIdx = pattern;
        if (patIdx <= 0)
        {
            ulong cur = BitConverter.ToUInt64(await PeekAsync("14ab580", 8, ct), 0);
            if (cur == 0) throw new InvalidOperationException("No project open in FL Studio.");
            patIdx = BitConverter.ToInt32(await PeekAbsAsync(cur, 4, ct), 0);
        }
        ValidatePattern(patIdx);

        // FLpat_GetOrCreateNoteRecorder(patternIdx, 1) -> the pattern's note store (creates if absent).
        ulong rec = await CallAsync("11d4080", new ulong[] { (uint)patIdx, 1UL }, ct);
        if (rec == 0) throw new InvalidOperationException($"Could not get/create the note store for pattern {patIdx} (open a project in FL).");

        // Each note: an on/off pair appended to the recorder. Note event (24B) — matched byte-for-byte
        // against a UI-drawn note:  +0x4 = (channel<<16) | 0x4000  (0x4000 = "real note" flag; without it
        // notes are ghosts); +0x8 = 0x400078 pending (the off rewrites it to length); +0xC = key;
        // +0x10 finePitch=120, +0x12 release=64, +0x14 pan=64 / +0x15 vel / +0x16 cut=128 / +0x17 res=128.
        foreach (var n in notes)
        {
            // Clamp to valid ranges so a stray LLM value can't fault the native call.
            int key = Math.Clamp(n.Key, 0, 131);
            int start = Math.Max(0, n.StartTick);
            int len = Math.Max(1, n.LengthTick);
            uint p3 = ((uint)(n.Channel & 0xFFFF) << 16) | 0x4000u;
            uint vel = (uint)Math.Clamp(n.Velocity, 0, 127);
            uint p7 = 0x80800040u | (vel << 8);
            await CallAsync("f6d740", new ulong[] { rec, (uint)start, p3, 0x400078UL, (uint)key, 0x78UL, p7 }, ct);                       // RecordNoteOn
            await CallAsync("f6d880", new ulong[] { rec, (uint)(start + len), p3, 0x80400088UL, (uint)key, 0x40UL, 1UL }, ct);            // RecordNoteOff -> length
        }

        // Commit ONCE (recorder->vtbl[3]; compaction) + LIGHT redraw only. Do NOT call FUN_0107EB90 — it
        // corrupts FL's audio engine. The piano roll picks up the new notes on redraw.
        ulong vt = await APtrAsync(rec, ct);
        ulong commitFn = await APtrAsync(vt + 0x18, ct);
        EnsureInModule("Note commit", commitFn);
        await CallAbsAsync(commitFn, new[] { rec }, ct);

        // Safe auto-refresh (verified live) — never FUN_0107EB90 (corrupts audio).
        await RefreshPatternAsync(patIdx, ct);
    }

    /// <summary>Project timebase: ticks per quarter note (PPQ). Falls back to 96.</summary>
    public async Task<int> GetPpqAsync(CancellationToken ct = default)
    {
        ulong p = BitConverter.ToUInt64(await PeekAsync("14a79f8", 8, ct), 0);
        if (p == 0) return 96;
        return BitConverter.ToInt32(await PeekAbsAsync(p, 4, ct), 0);
    }

    // ============================ Patterns ============================

    /// <summary>Current (selected) pattern index, 1-based.</summary>
    public async Task<int> GetCurrentPatternAsync(CancellationToken ct = default)
    {
        ulong p = BitConverter.ToUInt64(await PeekAsync("14ab580", 8, ct), 0);
        return p == 0 ? 0 : BitConverter.ToInt32(await PeekAbsAsync(p, 4, ct), 0);
    }

    /// <summary>Select/switch to a pattern (1-based); full UI switch. FLpat_SetCurrentPattern.</summary>
    public Task SelectPatternAsync(int index, CancellationToken ct = default)
    {
        ValidatePattern(index);
        return CallAsync("cbb300", new ulong[] { (uint)index }, ct);
    }

    /// <summary>True if a pattern has no notes/automation/name. FLpat_IsPatternEmpty.</summary>
    public async Task<bool> IsPatternEmptyAsync(int index, CancellationToken ct = default)
    {
        ValidatePattern(index);
        return await CallAsync("11db510", new ulong[] { (uint)index, 1, 0, 0 }, ct) != 0;
    }

    /// <summary>Select the first empty pattern (a "new" pattern); returns its index.</summary>
    public async Task<int> CreatePatternAsync(CancellationToken ct = default)
    {
        for (int i = 1; i <= 999; i++)
            if (await IsPatternEmptyAsync(i, ct)) { await SelectPatternAsync(i, ct); return i; }
        throw new InvalidOperationException("No empty pattern slot available.");
    }

    /// <summary>Delete all notes in a pattern (recorder clear + commit + refresh).</summary>
    public async Task ClearPatternAsync(int index, CancellationToken ct = default)
    {
        ValidatePattern(index);
        ulong rec = await CallAsync("11d4080", new ulong[] { (uint)index, 1 }, ct);
        if (rec == 0) return;
        await CallAsync("11e0930", new ulong[] { rec }, ct);   // FLpat_NoteArray_Clear
        ulong vt = await APtrAsync(rec, ct);
        ulong commitFn = await APtrAsync(vt + 0x18, ct);
        EnsureInModule("Note commit", commitFn);
        await CallAbsAsync(commitFn, new[] { rec }, ct);
        await RefreshPatternAsync(index, ct);
    }

    /// <summary>A pattern's display name.</summary>
    public async Task<string> GetPatternNameAsync(int index, CancellationToken ct = default)
    {
        ulong namePtr = BitConverter.ToUInt64(await PeekAsync((0x1803B68 + (long)index * 0xC0).ToString("x"), 8, ct), 0);
        string s = await ReadDelphiStringAsync(namePtr, ct);
        return string.IsNullOrEmpty(s) ? $"Pattern {index}" : s;
    }

    /// <summary>List patterns that have content: "idx: name (current)".</summary>
    public async Task<string> ListPatternsAsync(CancellationToken ct = default)
    {
        int cur = await GetCurrentPatternAsync(ct);
        var sb = new System.Text.StringBuilder();
        for (int i = 1; i <= 256; i++)
        {
            if (await IsPatternEmptyAsync(i, ct)) continue;
            sb.Append(i).Append(": ").Append(await GetPatternNameAsync(i, ct));
            if (i == cur) sb.Append(" (current)");
            sb.Append('\n');
        }
        return sb.Length == 0 ? "(no patterns with content)" : sb.ToString().TrimEnd();
    }

    // ============================ Channels ============================
    // Channel list = *(*(0x14A98D8)) (double-deref); count @ +0x10, items @ +0x8.

    private async Task<ulong> ChannelListAsync(CancellationToken ct)
    {
        ulong p0 = BitConverter.ToUInt64(await PeekAsync("14a98d8", 8, ct), 0);
        return p0 == 0 ? 0 : BitConverter.ToUInt64(await PeekAbsAsync(p0, 8, ct), 0);
    }

    public async Task<int> GetChannelCountAsync(CancellationToken ct = default)
    {
        ulong list = await ChannelListAsync(ct);
        return list == 0 ? 0 : BitConverter.ToInt32(await PeekAbsAsync(list + 0x10, 4, ct), 0);
    }

    private async Task<ulong> ChannelObjAsync(int index, CancellationToken ct)
    {
        if (index < 0) throw new InvalidOperationException($"Channel index {index} is invalid (must be >= 0).");
        ulong list = await ChannelListAsync(ct);
        if (list == 0) return 0;
        int count = BitConverter.ToInt32(await PeekAbsAsync(list + 0x10, 4, ct), 0);
        if (index >= count) throw new InvalidOperationException($"Channel {index} does not exist (only {count} channel(s)).");
        return await CallAsync("f00f80", new ulong[] { list, (uint)index }, ct);  // FLcr_ChannelListGetItem
    }

    /// <summary>Exclusively select a channel so the piano roll edits it. FLcr_SelectOneChannelByIndex.</summary>
    public async Task SelectChannelAsync(int index, CancellationToken ct = default)
    {
        int count = await GetChannelCountAsync(ct);
        if (index < 0 || index >= count) throw new InvalidOperationException($"Channel {index} does not exist (only {count} channel(s)).");
        await CallAsync("10e3eb0", new ulong[] { (uint)index }, ct);
    }

    public async Task<string> GetChannelNameAsync(int index, CancellationToken ct = default)
    {
        ulong ch = await ChannelObjAsync(index, ct);
        if (ch == 0) return $"Channel {index}";
        ulong vt = BitConverter.ToUInt64(await PeekAbsAsync(ch, 8, ct), 0);
        ulong getName = BitConverter.ToUInt64(await PeekAbsAsync(vt + 0x68, 8, ct), 0);
        // Delphi `function GetName: string` returns via a hidden out-param: getName(self, @result).
        // The result slot must be a valid (zeroed) UnicodeString var, or the assign derefs garbage and faults.
        EnsureInModule("Channel getName", getName);
        ulong sc = await ScratchAsync(ct);
        await PokeAbsAsync(sc, new byte[8], ct);
        await CallAbsAsync(getName, new[] { ch, sc }, ct);
        ulong strPtr = BitConverter.ToUInt64(await PeekAbsAsync(sc, 8, ct), 0);
        string s = await ReadDelphiStringAsync(strPtr, ct);
        return string.IsNullOrEmpty(s) ? $"Channel {index}" : s;
    }

    public async Task<string> ListChannelsAsync(CancellationToken ct = default)
    {
        int n = await GetChannelCountAsync(ct);
        if (n <= 0 || n > 1000) return "(no channels)";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(i).Append(": ").Append(await GetChannelNameAsync(i, ct)).Append('\n');
        return sb.ToString().TrimEnd();
    }

    // ============================ Mixer sends ============================

    /// <summary>Set a mixer send srcTrack-&gt;dstTrack at level (1.0 ≈ unity). Engine funcs only (no Python ctx).</summary>
    public async Task SetMixerSendAsync(int srcTrack, int dstTrack, double level, CancellationToken ct = default)
    {
        ulong mgr = BitConverter.ToUInt64(await PeekAbsAsync(BitConverter.ToUInt64(await PeekAsync("14a99a0", 8, ct), 0), 8, ct), 0); // *(*(0x14A99A0))
        ulong baseArr = BitConverter.ToUInt64(await PeekAsync("14a7eb0", 8, ct), 0);  // *(0x14A7EB0)
        await CallAsync("11a67f0", new ulong[] { mgr, (uint)srcTrack, (uint)dstTrack, 1, 1 }, ct);  // FLmx_SetRouteActiveCore
        ulong slot = baseArr + (ulong)srcTrack * 0x1474 + (ulong)dstTrack * 8 + 0x2E4;
        await PokeAbsAsync(slot, BitConverter.GetBytes((int)Math.Round(level * 16000)), ct);
        await CallAsync("11a5d20", new ulong[] { mgr }, ct);  // FLmx_RefreshRouting
    }

    // ============================ Transport ============================
    // FLgl_GlobalCommandDispatch(op, value, mode, flag) — scalar args, uses internal song/transport
    // globals (no context → cannot crash like the earlier fake-ctx attempt).

    // args: op, value(must be >0), mode(must have bit 0x2 or it returns -1), flag(0x8 to reach dispatch).
    // Verified live via the play-state flag (*(*0x14A81C0)): PLAY 0->1, STOP ->0.
    public Task TransportPlayAsync(CancellationToken ct = default)
        => CallAsync("ef7b20", new ulong[] { 10, 1, 2, 8 }, ct);
    public Task TransportStopAsync(CancellationToken ct = default)
        => CallAsync("ef7b20", new ulong[] { 11, 1, 2, 8 }, ct);
    public Task TransportToggleRecordAsync(CancellationToken ct = default)
        => CallAsync("ef7b20", new ulong[] { 12, 1, 2, 8 }, ct);

    // ============================ Mixer EQ gain ============================

    /// <summary>Set a mixer track EQ band gain (band 0=low,1=mid,2=high; value 0..0x40000000, ~0x20000000 = 0 dB).</summary>
    public Task SetMixerEqGainAsync(int track, int band, int value, CancellationToken ct = default)
        => DispatchCommandAsync((uint)(((long)track << 22) + 0x70001FD0 + Math.Clamp(band, 0, 2)), unchecked((ulong)(uint)value), FlagSet, ct);

    // ============================ Plugins / inserts ============================
    // Plugin database = .fst files under the user's "Plugin database\{Generators,Effects}" tree.
    // Loading a plugin = pass the full .fst path (Delphi string) to the host's load method:
    //   channel generator: (*(*ch + 0x150))(ch, fstPath, 0, 0x42)
    //   mixer FX slot:      (*(*slot + 0xF0))(slot, mode, fstPath, 0, 1, 1)  mode -3 insert, -2 clear

    private static string PluginDbDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Image-Line", "FL Studio", "Presets", "Plugin database");

    /// <summary>Lists installed plugins of a kind (generators or effects) by display name.</summary>
    public Task<string> ListAvailablePluginsAsync(bool effects, CancellationToken ct = default)
    {
        string dir = Path.Combine(PluginDbDir, effects ? "Effects" : "Generators");
        if (!Directory.Exists(dir)) return Task.FromResult($"(plugin database not found at {dir})");
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.EnumerateFiles(dir, "*.fst", SearchOption.AllDirectories))
        {
            string n = Path.GetFileNameWithoutExtension(f);
            if (!string.IsNullOrEmpty(n)) names.Add(n);
        }
        return Task.FromResult(names.Count == 0 ? "(none found)" : string.Join(", ", names));
    }

    /// <summary>Resolves a plugin display name to its .fst path in the plugin database.</summary>
    private static string? ResolveFstPath(string name, bool effects)
    {
        string dir = Path.Combine(PluginDbDir, effects ? "Effects" : "Generators");
        if (!Directory.Exists(dir)) return null;
        return Directory.EnumerateFiles(dir, "*.fst", SearchOption.AllDirectories)
            .FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FlInstallDir()
    {
        try { return Path.GetDirectoryName(Process.GetProcessesByName("FL64").FirstOrDefault()?.MainModule?.FileName ?? string.Empty); }
        catch { return null; }
    }

    /// <summary>Builds a Delphi UnicodeString (refcnt -1 constant) in the scratch buffer; returns the chars ptr.</summary>
    private async Task<ulong> WriteDelphiStringAsync(string s, CancellationToken ct)
    {
        byte[] chars = Encoding.Unicode.GetBytes(s);
        var buf = new byte[12 + chars.Length + 2];
        BitConverter.GetBytes((ushort)0x04B0).CopyTo(buf, 0);  // codePage 1200
        BitConverter.GetBytes((ushort)0x0002).CopyTo(buf, 2);  // elemSize
        BitConverter.GetBytes(-1).CopyTo(buf, 4);              // refCnt = -1 (constant)
        BitConverter.GetBytes(s.Length).CopyTo(buf, 8);        // length
        chars.CopyTo(buf, 12);
        ulong sc = await ScratchAsync(ct);
        await PokeAbsAsync(sc, buf, ct);
        return sc + 12;
    }

    /// <summary>Load a file (.fst plugin or audio) into a channel via its host load method:
    /// (*(*ch+0x150))(ch, path, mode, 0x42). mode 0 = plugin/preset, 1 = load sample.</summary>
    private async Task LoadIntoChannelAsync(ulong ch, string path, uint mode, CancellationToken ct)
    {
        ulong strPtr = await WriteDelphiStringAsync(path, ct);
        ulong loadFn = await APtrAsync(await APtrAsync(ch, ct) + 0x150, ct);
        EnsureInModule("Channel load", loadFn);
        await CallAbsAsync(loadFn, new ulong[] { ch, strPtr, mode, 0x42 }, ct);
    }

    /// <summary>Load/clear a plugin in a mixer FX slot: (*(*slot+0xF0))(slot, mode, path, 0, 1, 1).
    /// mode 0xFFFFFFFD = insert, 0xFFFFFFFE = clear.</summary>
    private async Task LoadIntoMixerSlotAsync(ulong slot, uint mode, string path, CancellationToken ct)
    {
        ulong strPtr = await WriteDelphiStringAsync(path, ct);
        ulong loadFn = await APtrAsync(await APtrAsync(slot, ct) + 0xF0, ct);
        EnsureInModule("Mixer slot load", loadFn);
        await CallAbsAsync(loadFn, new ulong[] { slot, mode, strPtr, 0, 1, 1 }, ct);
    }

    // ---- channel rack ----

    /// <summary>Describes a channel's loaded generator plugin.</summary>
    public async Task<string> GetChannelPluginAsync(int channel, CancellationToken ct = default)
    {
        ulong ch = await ChannelObjAsync(channel, ct);
        if (ch == 0) return $"Channel {channel}: not found.";
        int gen = BitConverter.ToInt32(await PeekAbsAsync(ch + 0x64, 4, ct), 0);
        string name = await GetChannelNameAsync(channel, ct);
        return gen < 0 ? $"{name}: no generator (bus/automation)" : $"{name}: has a generator plugin";
    }

    /// <summary>Adds a new channel-rack channel hosting the named generator plugin; returns its index.</summary>
    public async Task<int> AddChannelAsync(string pluginName, CancellationToken ct = default)
    {
        string? path = ResolveFstPath(pluginName, effects: false)
            ?? throw new InvalidOperationException($"Generator plugin '{pluginName}' not found (try native_list_available_plugins).");
        int before = await GetChannelCountAsync(ct);
        ulong ch = await CallAsync("f215e0", new ulong[] { (uint)before, 0, 0 }, ct);  // FLcr_InsertChannel
        if (ch == 0) throw new InvalidOperationException("Could not insert a channel.");
        await LoadIntoChannelAsync(ch, path, 0, ct);
        await RefreshRackAsync(ct);
        return before;
    }

    // ---- samples ----
    private static readonly string[] SampleExts = { ".wav", ".aif", ".aiff", ".mp3", ".ogg", ".flac", ".rx2" };

    /// <summary>Lists audio samples from FL's factory packs + the user's content, optionally filtered by name (path substring).</summary>
    public Task<string> ListSamplesAsync(string? filter, CancellationToken ct = default)
    {
        var roots = new List<string>();
        string? install = FlInstallDir();
        if (install != null) roots.Add(Path.Combine(install, "Data", "Patches", "Packs"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Image-Line", "FL Studio"));
        var hits = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (string f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) break;
                    if (Array.IndexOf(SampleExts, Path.GetExtension(f).ToLowerInvariant()) < 0) continue;
                    if (!string.IsNullOrEmpty(filter) && f.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hits.Add(f);
                    if (hits.Count >= 200) break;
                }
            }
            catch { /* skip inaccessible trees, keep what we found */ }
            if (hits.Count >= 200) break;
        }
        if (hits.Count == 0) return Task.FromResult(string.IsNullOrEmpty(filter) ? "(no samples found)" : $"(no samples matching '{filter}')");
        string head = string.IsNullOrEmpty(filter) ? $"{hits.Count} samples (up to 200):\n" : $"{hits.Count} samples matching '{filter}':\n";
        return Task.FromResult(head + string.Join("\n", hits));
    }

    /// <summary>Adds a new channel that plays the given audio sample file; returns its index.</summary>
    public async Task<int> AddSampleChannelAsync(string samplePath, CancellationToken ct = default)
    {
        if (!File.Exists(samplePath)) throw new InvalidOperationException($"Sample file not found: {samplePath}");
        int before = await GetChannelCountAsync(ct);
        ulong ch = await CallAsync("f215e0", new ulong[] { (uint)before, 0, 0 }, ct);  // FLcr_InsertChannel (default Sampler)
        if (ch == 0) throw new InvalidOperationException("Could not insert a channel.");
        await LoadIntoChannelAsync(ch, samplePath, 1, ct);  // mode 1 = load sample
        await RefreshRackAsync(ct);
        return before;
    }

    /// <summary>Replaces an existing channel's sample with a new audio file.</summary>
    public async Task ReplaceChannelSampleAsync(int channel, string samplePath, CancellationToken ct = default)
    {
        if (!File.Exists(samplePath)) throw new InvalidOperationException($"Sample file not found: {samplePath}");
        ulong ch = await ChannelObjAsync(channel, ct);
        if (ch == 0) throw new InvalidOperationException($"Channel {channel} not found.");
        await LoadIntoChannelAsync(ch, samplePath, 1, ct);
        await RefreshRackAsync(ct);
    }

    // ---- mixer FX slots ----

    private async Task<ulong> MixerSlotObjAsync(int track, int slot, CancellationToken ct)
    {
        if (track < 0 || track > 199) throw new InvalidOperationException($"Mixer track {track} out of range (0..199).");
        if (slot < 0 || slot > 9) throw new InvalidOperationException($"FX slot {slot} out of range (0..9).");
        ulong baseArr = BitConverter.ToUInt64(await PeekAsync("14a7eb0", 8, ct), 0);
        if (baseArr == 0) return 0;
        return BitConverter.ToUInt64(await PeekAbsAsync(baseArr + (ulong)track * 0x1474 + 0x1324 + (ulong)slot * 8, 8, ct), 0);
    }

    /// <summary>Lists the effect plugins loaded in a mixer track's 10 FX slots.</summary>
    public async Task<string> ListMixerEffectsAsync(int track, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        for (int s = 0; s < 10; s++)
        {
            ulong so = await MixerSlotObjAsync(track, s, ct);
            if (so == 0) continue;
            int idx = BitConverter.ToInt32(await PeekAbsAsync(so + 0x64, 4, ct), 0);
            if (idx < 0) continue;
            ulong namePtr = BitConverter.ToUInt64(await PeekAbsAsync(so + 0x58, 8, ct), 0);
            sb.Append($"slot {s}: {await ReadDelphiStringAsync(namePtr, ct)}\n");
        }
        return sb.Length == 0 ? $"Mixer track {track}: no effects loaded." : sb.ToString().TrimEnd();
    }

    /// <summary>Loads/replaces the named effect plugin into a mixer track's FX slot (0-9).</summary>
    public async Task AddMixerEffectAsync(int track, int slot, string pluginName, CancellationToken ct = default)
    {
        string? path = ResolveFstPath(pluginName, effects: true)
            ?? throw new InvalidOperationException($"Effect plugin '{pluginName}' not found (try native_list_available_plugins).");
        ulong so = await MixerSlotObjAsync(track, slot, ct);
        if (so == 0) throw new InvalidOperationException($"Mixer slot {track}/{slot} not found.");
        await LoadIntoMixerSlotAsync(so, 0xFFFFFFFDu, path, ct);  // mode -3 = insert
        await DispatchCommandAsync((uint)(((track * 0x40 + slot) << 16) + 0x70001F00), 1, 0x251, ct);
    }

    /// <summary>Clears the plugin from a mixer track's FX slot.</summary>
    public async Task RemoveMixerEffectAsync(int track, int slot, CancellationToken ct = default)
    {
        string? install = FlInstallDir() ?? throw new InvalidOperationException("FL Studio not running.");
        string del = Path.Combine(install, "Data", "System", "(delete) effect.fst");
        ulong so = await MixerSlotObjAsync(track, slot, ct);
        if (so == 0) return;
        await LoadIntoMixerSlotAsync(so, 0xFFFFFFFEu, del, ct);  // mode -2 = clear
        await DispatchCommandAsync((uint)(((track * 0x40 + slot) << 16) + 0x70001F00), 1, 0x251, ct);
    }

    /// <summary>Copies the effect type from one FX slot to another (type only, not parameter state).</summary>
    public async Task CloneMixerEffectAsync(int track, int fromSlot, int toSlot, CancellationToken ct = default)
    {
        ulong so = await MixerSlotObjAsync(track, fromSlot, ct);
        if (so == 0) throw new InvalidOperationException($"Mixer slot {track}/{fromSlot} not found.");
        int idx = BitConverter.ToInt32(await PeekAbsAsync(so + 0x64, 4, ct), 0);
        if (idx < 0) throw new InvalidOperationException($"Mixer slot {track}/{fromSlot} is empty.");
        string name = await ReadDelphiStringAsync(BitConverter.ToUInt64(await PeekAbsAsync(so + 0x58, 8, ct), 0), ct);
        await AddMixerEffectAsync(track, toSlot, name, ct);
    }

    // ---- plugin parameters ----
    // Resolve a plugin instance + its param command base. slot < 0 => channel generator, else mixer FX slot.
    //   instance = (*(*(*(obj+0x38)+0x48)+0x20))(host);  count = *(int*)(obj+0x68)
    //   cmd base = channel: *(int*)(obj+0x9c)+0x8000 ; mixer: ((track*0x40+slot)<<16)+0x70008000
    private async Task<(ulong inst, int count, uint cmdBase)> ResolvePluginAsync(int channelOrTrack, int slot, CancellationToken ct)
    {
        ulong obj; uint cmdBase;
        if (slot < 0)
        {
            obj = await ChannelObjAsync(channelOrTrack, ct);
            if (obj == 0) return (0, 0, 0);
            int recEvt = BitConverter.ToInt32(await PeekAbsAsync(obj + 0x9c, 4, ct), 0);
            cmdBase = unchecked((uint)(recEvt + 0x8000));
        }
        else
        {
            if (channelOrTrack < 0 || channelOrTrack > 199) throw new InvalidOperationException($"Mixer track {channelOrTrack} out of range (0..199).");
            if (slot > 9) throw new InvalidOperationException($"FX slot {slot} out of range (0..9).");
            ulong baseArr = BitConverter.ToUInt64(await PeekAsync("14a7eb0", 8, ct), 0);
            if (baseArr == 0) return (0, 0, 0);
            obj = BitConverter.ToUInt64(await PeekAbsAsync(baseArr + (ulong)channelOrTrack * 0x1474 + 0x1324 + (ulong)slot * 8, 8, ct), 0);
            cmdBase = unchecked((uint)(((channelOrTrack * 0x40 + slot) << 16) + 0x70008000));
        }
        if (obj == 0) return (0, 0, cmdBase);
        int count = BitConverter.ToInt32(await PeekAbsAsync(obj + 0x68, 4, ct), 0);
        ulong p38 = BitConverter.ToUInt64(await PeekAbsAsync(obj + 0x38, 8, ct), 0);
        if (p38 == 0) return (0, count, cmdBase);
        ulong host = p38 + 0x48;
        ulong hostVt = BitConverter.ToUInt64(await PeekAbsAsync(host, 8, ct), 0);
        ulong getInst = BitConverter.ToUInt64(await PeekAbsAsync(hostVt + 0x20, 8, ct), 0);
        EnsureInModule("Plugin getInstance", getInst);
        ulong inst = await CallAbsAsync(getInst, new[] { host }, ct);
        return (inst, count, cmdBase);
    }

    private async Task<string> ReadParamNameAsync(ulong inst, int i, ulong charBuf, CancellationToken ct)
    {
        ulong vti = BitConverter.ToUInt64(await PeekAbsAsync(inst, 8, ct), 0);
        ulong nameFn = BitConverter.ToUInt64(await PeekAbsAsync(vti + 0x20, 8, ct), 0);
        // NB: no EnsureInModule here — for a hosted VST (e.g. Serum 2) the plugin-instance methods
        // legitimately live in the PLUGIN's own DLL, not FLEngine. The native callabs is SEH-guarded,
        // so a bad pointer returns ok:0 (a clean exception) rather than crashing FL.
        if (nameFn == 0) throw new InvalidOperationException("Plugin getParamName pointer is null.");
        await PokeAbsAsync(charBuf, new byte[64], ct);
        await CallAbsAsync(nameFn, new ulong[] { inst, 0, (uint)i, 0, charBuf }, ct);  // GetParamName(?, i, ?, buf)
        byte[] raw = await PeekAbsAsync(charBuf, 64, ct);
        int end = Array.IndexOf(raw, (byte)0); if (end < 0) end = raw.Length;
        int start = 0; while (start < end && raw[start] < 0x20) start++;   // strip FL "^b^a" formatting codes
        return end > start ? Encoding.ASCII.GetString(raw, start, end - start) : $"param {i}";
    }

    /// <summary>Reads a plugin param's current raw native value: getParamValue = (*(*inst+0x30))(inst,i,0,2) returns it in RAX.</summary>
    private async Task<int> ReadParamValueAsync(ulong inst, int i, CancellationToken ct)
    {
        ulong vti = BitConverter.ToUInt64(await PeekAbsAsync(inst, 8, ct), 0);
        ulong valFn = BitConverter.ToUInt64(await PeekAbsAsync(vti + 0x30, 8, ct), 0);
        if (valFn == 0) throw new InvalidOperationException("Plugin getParamValue pointer is null."); // plugin-module ptr; callabs is SEH-guarded
        ulong rax = await CallAbsAsync(valFn, new ulong[] { inst, (uint)i, 0, 2 }, ct);
        return unchecked((int)rax);
    }

    /// <summary>Reads a param's human-readable display string (with units), the sound-design feedback
    /// loop — e.g. "1.2 kHz", "-6.0 dB", "62 %". Uses the wrapper's mode-1 text method (same shared
    /// TBaseAudioPlugin base as getParamName, so it is flavor-agnostic: native / VST2 / VST3 alike):
    /// (*(*inst+0x20))(inst, mode=1, paramIdx, rawValue, buf). Returns "" if the plugin supplies none.</summary>
    private async Task<string> ReadParamValueStringAsync(ulong inst, int i, int raw, ulong charBuf, CancellationToken ct)
    {
        ulong vti = BitConverter.ToUInt64(await PeekAbsAsync(inst, 8, ct), 0);
        ulong fn = BitConverter.ToUInt64(await PeekAbsAsync(vti + 0x20, 8, ct), 0);
        if (fn == 0) throw new InvalidOperationException("Plugin getParamValueString pointer is null."); // plugin-module ptr; callabs is SEH-guarded
        await PokeAbsAsync(charBuf, new byte[64], ct);
        await CallAbsAsync(fn, new ulong[] { inst, 1, (uint)i, unchecked((uint)raw), charBuf }, ct);  // mode 1 = value display
        byte[] rawb = await PeekAbsAsync(charBuf, 64, ct);
        int end = Array.IndexOf(rawb, (byte)0); if (end < 0) end = rawb.Length;
        int start = 0; while (start < end && rawb[start] < 0x20) start++;   // strip FL "^b^a" formatting codes
        return end > start ? Encoding.ASCII.GetString(rawb, start, end - start).Trim() : "";
    }

    /// <summary>Lists a plugin's parameters as "index: name" (slot &lt; 0 = channel generator). Optional name filter.</summary>
    public async Task<string> ListPluginParamsAsync(int channelOrTrack, int slot, string? filter, CancellationToken ct = default)
    {
        var (inst, count, _) = await ResolvePluginAsync(channelOrTrack, slot, ct);
        if (inst == 0) return "No plugin found (the slot is empty or the channel has no generator).";
        if (count <= 0) return "This plugin exposes no parameters.";
        ulong charBuf = await ScratchAsync(ct) + 0x40;
        var sb = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < count && shown < 200; i++)
        {
            string name = await ReadParamNameAsync(inst, i, charBuf, ct);
            if (!string.IsNullOrEmpty(filter) && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            int val = await ReadParamValueAsync(inst, i, ct);
            // Prefer the plugin's own display string (units) so the LLM can reason about real targets
            // ("Cutoff = 1.2 kHz"). Fallback when a plugin gives none: VST params return their raw value
            // as normalized float bits, so reinterpret to a 0..1 figure rather than print a huge integer.
            string disp = await ReadParamValueStringAsync(inst, i, val, charBuf, ct);
            if (disp.Length == 0)
            {
                float f = BitConverter.Int32BitsToSingle(val);
                disp = float.IsFinite(f) && Math.Abs(f) <= 1e6f ? f.ToString("0.###") : val.ToString();
            }
            sb.Append(i).Append(": ").Append(name).Append(" = ").Append(disp).Append('\n');
            shown++;
        }
        if (shown == 0) return $"No parameters match '{filter}' ({count} total).";
        string head = string.IsNullOrEmpty(filter)
            ? $"{count} parameters{(shown < count ? " (showing first 200; use a name filter)" : "")}:\n"
            : $"{count} parameters, {shown} matching '{filter}':\n";
        return head + sb.ToString().TrimEnd();
    }

    /// <summary>Sets a plugin parameter to a normalized value 0..1 (slot &lt; 0 = channel generator).</summary>
    public async Task SetPluginParamAsync(int channelOrTrack, int slot, int paramIndex, double value, CancellationToken ct = default)
    {
        var (inst, count, cmdBase) = await ResolvePluginAsync(channelOrTrack, slot, ct);
        if (inst == 0) throw new InvalidOperationException("No plugin found (the slot is empty or the channel has no generator).");
        if (paramIndex < 0 || paramIndex >= count) throw new InvalidOperationException($"paramIndex out of range (0..{count - 1}).");
        uint fixedVal = (uint)Math.Round(Math.Clamp(value, 0.0, 1.0) * 1073741824.0);  // norm * 2^30
        await DispatchCommandAsync(cmdBase + (uint)paramIndex, fixedVal, 0x3fd, ct);
    }

    // ---- shared helper ----
    private async Task<string> ReadDelphiStringAsync(ulong ptr, CancellationToken ct)
    {
        if (ptr == 0) return "";
        int len = BitConverter.ToInt32(await PeekAbsAsync(ptr - 4, 4, ct), 0);
        if (len <= 0 || len > 256) return "";
        return System.Text.Encoding.Unicode.GetString(await PeekAbsAsync(ptr, len * 2, ct));
    }

    // ============================ Arrangement / song / project (turn 8 RE harvest) ============================
    // Recipes: re/12-controls-harvest.md. Peek helpers below; globals are Ghidra addresses (bridge rebases).

    private async Task<ulong> GPtrAsync(string hexGlobal, CancellationToken ct) => BitConverter.ToUInt64(await PeekAsync(hexGlobal, 8, ct), 0);
    private async Task<ulong> APtrAsync(ulong addr, CancellationToken ct) => BitConverter.ToUInt64(await PeekAbsAsync(addr, 8, ct), 0);
    private async Task<int> AI32Async(ulong addr, CancellationToken ct) => BitConverter.ToInt32(await PeekAbsAsync(addr, 4, ct), 0);

    private async Task<int> CurrentPatternIndexAsync(CancellationToken ct)
    {
        ulong p = await GPtrAsync("14ab580", ct);
        return p == 0 ? 0 : BitConverter.ToInt32(await PeekAbsAsync(p, 4, ct), 0);
    }

    private static string KeyName(int key)
    {
        string[] n = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        return n[((key % 12) + 12) % 12] + (key / 12);  // FL labels MIDI 60 as C5
    }

    // ---- read notes ----
    /// <summary>Reads piano-roll notes of a pattern (pattern: 1-based, or &lt;=0 = current). channel&lt;0 = all channels.</summary>
    public async Task<string> GetNotesAsync(int pattern, int channel, CancellationToken ct = default)
    {
        // The note-recorder static array 0x1803B90 is indexed by the 1-based pattern number directly
        // (verified: 11d4080(N) <-> static[N]), the SAME index AddNotesAsync writes to — so add/read
        // round-trips on any pattern. (No -1: that read static[pattern-1] and reported "no notes".)
        int patIdx = pattern <= 0 ? await CurrentPatternIndexAsync(ct) : pattern;
        ValidatePattern(patIdx);
        ulong rec = await GPtrAsync((0x1803B90 + (ulong)patIdx * 0xC0).ToString("x"), ct);
        if (rec == 0) return $"(pattern {patIdx} has no notes)";
        int count = await AI32Async(rec + 0x14, ct);
        ulong data = await APtrAsync(rec + 8, ct);
        if (count <= 0 || data == 0) return $"(pattern {patIdx} has no notes)";
        int n = Math.Min(count, 300);
        byte[] all = await PeekAbsAsync(data, n * 0x18, ct);
        var sb = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < n; i++)
        {
            int o = i * 0x18;
            int ch = BitConverter.ToUInt16(all, o + 6);
            if (channel >= 0 && ch != channel) continue;
            int pos = BitConverter.ToInt32(all, o + 0);
            int len = BitConverter.ToInt32(all, o + 8);
            int key = BitConverter.ToUInt16(all, o + 0xC);
            int vel = all[o + 0x15];
            bool muted = (all[o + 0x13] & 0x20) != 0;
            sb.Append($"ch{ch} {KeyName(key)}({key}) pos={pos} len={len} vel={vel}{(muted ? " muted" : "")}\n");
            shown++;
        }
        if (shown == 0) return channel >= 0 ? $"(no notes on channel {channel} in pattern {patIdx})" : $"(pattern {patIdx} has no notes)";
        return $"Pattern {patIdx}: {shown} notes{(count > 300 ? " (capped at 300)" : "")}:\n" + sb.ToString().TrimEnd();
    }

    // ---- playlist tracks ----
    private Task<ulong> PlaylistRootAsync(CancellationToken ct) => CallAsync("11e32c0", Array.Empty<ulong>(), ct);

    private async Task RepaintPlaylistAsync(CancellationToken ct)
    {
        ulong a = await GPtrAsync("14aab88", ct);
        if (a != 0) await CallAsync("da40c0", new ulong[] { a }, ct);
    }

    /// <summary>Lists playlist tracks 1..50 (name, color, mute, collapse, selection, mode).</summary>
    public async Task<string> ListPlaylistTracksAsync(CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) return "(playlist not available)";
        string[] modes = { "normal", "audio", "?", "instrument" };
        var sb = new StringBuilder();
        for (int i = 1; i <= 50; i++)
        {
            ulong t = root + (ulong)i * 0x114;
            byte[] tr = await PeekAbsAsync(t, 0x114, ct);
            string name = await ReadDelphiStringAsync(BitConverter.ToUInt64(tr, 0x24), ct);
            uint c = BitConverter.ToUInt32(tr, 0x2c);
            uint rgb = (c & 0xFF) << 16 | (c & 0xFF00) | (c >> 16) & 0xFF;
            byte enabled = tr[0x3c], collapsed = tr[0xe4], selected = tr[0xe5];
            int mode = BitConverter.ToInt32(tr, 0xd8);
            string disp = string.IsNullOrEmpty(name) ? $"Track {i}" : name;
            sb.Append($"{i}: {disp} [#{rgb:X6}{(enabled == 0 ? " MUTED" : "")}{(collapsed != 0 ? " collapsed" : "")}{(selected != 0 ? " selected" : "")}{(mode >= 0 && mode < modes.Length ? " " + modes[mode] : "")}]\n");
        }
        return "Playlist tracks 1-50:\n" + sb.ToString().TrimEnd();
    }

    public async Task SetTrackNameAsync(int track, string name, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) throw new InvalidOperationException("Playlist not available.");
        uint color = BitConverter.ToUInt32(await PeekAbsAsync(root + (ulong)track * 0x114 + 0x2c, 4, ct), 0);
        ulong strPtr = await WriteDelphiStringAsync(name, ct);
        await CallAsync("11e7940", new ulong[] { root, (uint)track, strPtr, color }, ct);  // SetTrackNameAndColor (self-refreshes)
    }

    public async Task SetTrackColorAsync(int track, int rgb, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) throw new InvalidOperationException("Playlist not available.");
        ulong namePtr = await APtrAsync(root + (ulong)track * 0x114 + 0x24, ct);  // preserve existing name
        uint bgr = (uint)(((rgb >> 16) & 0xFF) | (((rgb >> 8) & 0xFF) << 8) | ((rgb & 0xFF) << 16));
        await CallAsync("11e7940", new ulong[] { root, (uint)track, namePtr, bgr }, ct);
    }

    public async Task SetTrackMuteAsync(int track, bool muted, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) throw new InvalidOperationException("Playlist not available.");
        await PokeAbsAsync(root + (ulong)track * 0x114 + 0x3c, new byte[] { (byte)(muted ? 0 : 1) }, ct);
        await RepaintPlaylistAsync(ct);
    }

    public async Task SetTrackCollapsedAsync(int track, bool collapsed, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) throw new InvalidOperationException("Playlist not available.");
        await PokeAbsAsync(root + (ulong)track * 0x114 + 0xe4, new byte[] { (byte)(collapsed ? 1 : 0) }, ct);
        await RepaintPlaylistAsync(ct);
    }

    public async Task SelectTrackAsync(int track, CancellationToken ct = default)
    {
        ulong root = await PlaylistRootAsync(ct);
        if (root == 0) throw new InvalidOperationException("Playlist not available.");
        await CallAsync("11e9c30", new ulong[] { root, (uint)track, 1 }, ct);  // FLpl_SetTrackSelection mode 1 = exclusive
    }

    // ---- playlist clips ----
    // Clip collection = *(FLpl_GetCurrentArrangement@0x11E32C0 + 0x14) — a DEREFERENCE of arr+0x14 (the engine's own
    // FLpl_SendPatternToPlaylist resolves it this way). Cobj: vtbl@0, data@8, stride@0x10, count@0x14.
    private async Task<ulong> ClipCollObjAsync(CancellationToken ct)
    {
        ulong arr = await PlaylistRootAsync(ct);          // FLpl_GetCurrentArrangement
        if (arr == 0) return 0;
        return await APtrAsync(arr + 0x14, ct);           // Cobj
    }

    private async Task<(ulong data, int stride, int count)> ClipCollectionAsync(CancellationToken ct)
    {
        ulong C = await ClipCollObjAsync(ct);
        if (C == 0) return (0, 0, 0);
        return (await APtrAsync(C + 8, ct), await AI32Async(C + 0x10, ct), await AI32Async(C + 0x14, ct));
    }

    /// <summary>Lists active playlist clips (slot index, track, start, length, source).</summary>
    public async Task<string> ListClipsAsync(CancellationToken ct = default)
    {
        var (data, stride, count) = await ClipCollectionAsync(ct);
        if (data == 0 || count <= 0 || stride <= 0) return "(no clips)";
        var sb = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < count && shown < 200; i++)
        {
            byte[] cb = await PeekAbsAsync(data + (ulong)i * (ulong)stride, 0x24, ct);
            byte flags = cb[0x13];
            if ((flags & 0x80) == 0) continue;
            int start = BitConverter.ToInt32(cb, 0);
            uint src = BitConverter.ToUInt32(cb, 4);
            int len = BitConverter.ToInt32(cb, 8);
            int trackNo = 500 - BitConverter.ToInt16(cb, 0xc);
            string srcDesc = src >= 0x50000000 ? $"pattern {(int)((src - 0x50000000) >> 16)}" : $"channel {(int)(src >> 16)}";
            sb.Append($"[{i}] track {trackNo} start={start} len={len} {srcDesc}{((flags & 0x20) != 0 ? " muted" : "")}\n");
            shown++;
        }
        return shown == 0 ? "(no active clips)" : $"{shown} clips:\n" + sb.ToString().TrimEnd();
    }

    /// <summary>Adds a pattern clip to the playlist timeline. <paramref name="pattern"/> is 1-based (the same
    /// index used by notes/patterns/name/length); 0 (the reserved slot) and out-of-range throw.
    /// lengthTick&lt;=0 = the pattern's own length.</summary>
    public async Task AddPatternClipAsync(int pattern, int track, int startTick, int lengthTick, CancellationToken ct = default)
    {
        ValidatePattern(pattern); // 1-based; rejects 0 + out-of-range (a 0-based index here mis-targets patterns)
        int len = lengthTick;
        if (len <= 0)
        {
            ulong patArr = await GPtrAsync("14aa0c8", ct);
            if (patArr != 0) len = await AI32Async(patArr + (ulong)pattern * 0xC0 + 0x50, ct);
            if (len <= 0) len = await GetPpqAsync(ct) * 4;
        }
        // FL encodes a pattern-clip sourceID from the 1-based pattern number (FLpl_SendPatternToPlaylist
        // @0xCB0780: 0x50005000 + (N<<16)) — the SAME index that drives the note recorder / name / length.
        // Feeding a 0-based index here stored N-1, so pattern 1 -> slot 0 ("pattern 0"), pattern 2 -> pattern 1, etc.
        await InsertClipRawAsync(startTick, 0x50005000u + ((uint)pattern << 16), len, track, -1, -1, ct);
        await RepaintPlaylistAsync(ct);
    }

    private async Task<ulong> ClipAddrAsync(int index, CancellationToken ct)
    {
        var (data, stride, count) = await ClipCollectionAsync(ct);
        if (data == 0 || stride <= 0) throw new InvalidOperationException("Playlist clip collection not available.");
        if (index < 0 || index >= count) throw new InvalidOperationException($"Clip index {index} out of range (0..{count - 1}).");
        return data + (ulong)index * (ulong)stride;
    }

    public async Task MoveClipAsync(int clipIndex, int startTick, int track, CancellationToken ct = default)
    {
        ulong clip = await ClipAddrAsync(clipIndex, ct);
        await PokeAbsAsync(clip + 0, BitConverter.GetBytes(startTick), ct);
        if (track >= 0) await PokeAbsAsync(clip + 0xc, BitConverter.GetBytes((short)(500 - track)), ct);
        await RepaintPlaylistAsync(ct);
    }

    public async Task ResizeClipAsync(int clipIndex, int lengthTick, CancellationToken ct = default)
    {
        ulong clip = await ClipAddrAsync(clipIndex, ct);
        await PokeAbsAsync(clip + 8, BitConverter.GetBytes(lengthTick), ct);
        await RepaintPlaylistAsync(ct);
    }

    public async Task DeleteClipAsync(int clipIndex, CancellationToken ct = default)
    {
        ulong C = await ClipCollObjAsync(ct);
        if (C == 0) throw new InvalidOperationException("Playlist clip collection not available.");
        ulong clip = await ClipAddrAsync(clipIndex, ct);
        byte fl = (await PeekAbsAsync(clip + 0x13, 1, ct))[0];
        await PokeAbsAsync(clip + 0x13, new byte[] { (byte)(fl & ~0x80) }, ct);  // clear ACTIVE
        await CallAsync("f6e180", new ulong[] { C }, ct);
        await RepaintPlaylistAsync(ct);
    }

    /// <summary>Mute/unmute a playlist clip (sets clip+0x13 bit 0x20 via FLpl_SetClipMuted).</summary>
    public async Task SetClipMutedAsync(int clipIndex, bool muted, CancellationToken ct = default)
    {
        ulong clip = await ClipAddrAsync(clipIndex, ct);
        if (clip == 0) throw new InvalidOperationException("Clip not found.");
        await CallAsync("f71a60", new ulong[] { clip, (ulong)(muted ? 1 : 0) }, ct);  // FLpl_SetClipMuted
        await RepaintPlaylistAsync(ct);
    }

    // Shared clip-insert core (same path the engine's FLpl_SendPatternToPlaylist uses): init defaults into a
    // 0x48 temp, set fields, insert into the collection. Guards the resolved vtbl fn ptrs against the FLEngine
    // module range (calling a non-code ptr AVs and crashes FL).
    private async Task InsertClipRawAsync(int startTick, uint sourceID, int len, int track, int srcStart, int srcEnd, CancellationToken ct)
    {
        ulong C = await ClipCollObjAsync(ct);
        if (C == 0) throw new InvalidOperationException("Playlist clip collection not available.");
        ulong vt = await APtrAsync(C, ct);
        ulong initFn = await APtrAsync(vt + 0x10, ct);
        ulong insertFn = await APtrAsync(vt + 8, ct);
        EnsureInModule("Clip-insert", initFn, insertFn);
        ulong tmp = await ScratchAsync(ct);
        await PokeAbsAsync(tmp, new byte[0x48], ct);
        await CallAbsAsync(initFn, new ulong[] { C, tmp }, ct);
        await PokeAbsAsync(tmp + 0x00, BitConverter.GetBytes(startTick), ct);
        await PokeAbsAsync(tmp + 0x04, BitConverter.GetBytes(sourceID), ct);
        await PokeAbsAsync(tmp + 0x08, BitConverter.GetBytes(len), ct);
        await PokeAbsAsync(tmp + 0x0c, BitConverter.GetBytes((short)(500 - track)), ct);
        byte fl = (await PeekAbsAsync(tmp + 0x13, 1, ct))[0];
        await PokeAbsAsync(tmp + 0x13, new byte[] { (byte)(fl | 0x80) }, ct);
        await PokeAbsAsync(tmp + 0x18, BitConverter.GetBytes(srcStart), ct);
        await PokeAbsAsync(tmp + 0x1c, BitConverter.GetBytes(srcEnd), ct);
        await CallAbsAsync(insertFn, new ulong[] { C, tmp }, ct);
        await CallAsync("f6e180", new ulong[] { C }, ct);  // RecountActiveClips
    }

    private static (ulong b, ulong e) FlEngineRange()
    {
        var m = Process.GetProcessesByName("FL64").FirstOrDefault()?.Modules.Cast<ProcessModule>()
            .FirstOrDefault(x => x.ModuleName.StartsWith("FLEngine", StringComparison.OrdinalIgnoreCase));
        ulong b = m != null ? (ulong)m.BaseAddress.ToInt64() : 0;
        return (b, m != null ? b + (ulong)m.ModuleMemorySize : 0);
    }

    // ============================ In-FL chat tab (native browser tab; re/14) ============================
    /// <summary>
    /// Stage A: add a native "FruityLink AI" tab to FL's MAIN data browser by cloning an existing tab
    /// (inherits a valid view config so it won't fault), then renaming it. The content host + vtbl-hook
    /// that make it show our chat come in later stages. Returns the new tab's id + pointers. (re/14 §4.)
    /// </summary>
    public async Task<string> CreateChatTabAsync(CancellationToken ct = default)
    {
        ulong browser = await GPtrAsync("157ffb8", ct);            // *DAT_0157ffb8 = main TVirtualDataBrowser
        if (browser == 0) throw new InvalidOperationException("FL main data browser not found.");
        ulong tabsObj = await APtrAsync(browser + 0x158, ct);
        if (tabsObj == 0) throw new InvalidOperationException("Browser tab container not found.");
        ulong arr = await APtrAsync(tabsObj + 0x10, ct);
        int n = arr == 0 ? 0 : await AI32Async(arr - 8, ct);
        if (arr == 0 || n <= 0) throw new InvalidOperationException("Browser has no tabs to clone from.");

        ulong srcTab = await APtrAsync(arr, ct);                   // arr[0]
        int srcId = await AI32Async(srcTab + 0xf4, ct);

        ulong cloneFn = GhidraToRuntime(0x9ac910);                 // FLbrz_AddTabCloneOfSource(browser, srcId)
        EnsureInModule("AddTabCloneOfSource", cloneFn);
        await CallAbsAsync(cloneFn, new ulong[] { browser, (uint)srcId }, ct);

        arr = await APtrAsync(tabsObj + 0x10, ct);                 // may have re-allocated
        int n2 = await AI32Async(arr - 8, ct);
        if (n2 <= n) throw new InvalidOperationException($"Clone did not add a tab (count {n}->{n2}).");
        ulong ourTab = await APtrAsync(arr + (ulong)(n2 - 1) * 8, ct);
        if (ourTab == 0) throw new InvalidOperationException("Clone produced a null tab.");
        int ourId = await AI32Async(ourTab + 0xf4, ct);

        // Rename caption (+0x20) + name/filter (+0x6c). Delphi_UStrAsg deep-copies the const → persists.
        ulong ustr = await WriteDelphiStringAsync("FruityLink AI", ct);
        ulong asg = GhidraToRuntime(0x4133f0);                     // Delphi_UStrAsg(dest, src)
        EnsureInModule("UStrAsg", asg);
        await CallAbsAsync(asg, new ulong[] { ourTab + 0x20, ustr }, ct);
        await CallAbsAsync(asg, new ulong[] { ourTab + 0x6c, ustr }, ct);

        // Verified repaint (NOT the unverified FUN_009b2120, which corrupted the browser): switch away then
        // back via FLbrz_SelectTabById so the tab bar redraws with our new caption. The clone carries a valid
        // config, so re-selecting it is the same safe path as clicking the source tab.
        ulong selectFn = GhidraToRuntime(0x9ac590);               // FLbrz_SelectTabById(browser, id, flag)
        EnsureInModule("SelectTabById", selectFn);
        await CallAbsAsync(selectFn, new ulong[] { browser, (uint)srcId, 0 }, ct);
        await CallAbsAsync(selectFn, new ulong[] { browser, (uint)ourId, 0 }, ct);

        // Read back to confirm the rename landed at the data level (vs. a display/offset issue).
        ulong capPtr = await APtrAsync(ourTab + 0x20, ct);
        ulong nmPtr = await APtrAsync(ourTab + 0x6c, ct);
        string cap = capPtr == 0 ? "(null)" : await ReadDelphiStringAsync(capPtr, ct);
        string nm = nmPtr == 0 ? "(null)" : await ReadDelphiStringAsync(nmPtr, ct);
        return $"Created chat tab id={ourId} (count {n}->{n2}); caption='{cap}' name='{nm}' (tab=0x{ourTab:x}).";
    }

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

    // Cached FLEngine range for the in-module guards (hot paths like ListPluginParams check per item).
    // Refresh-on-miss keeps it correct across an FL restart: a valid addr hits the cache; anything outside
    // forces one re-query (a real bad ptr stays rejected; a moved module is re-learned). x64 ulong rw is atomic.
    private static ulong _modBase, _modEnd;

    /// <summary>True if an address lies inside the live FLEngine module (real code/data, not a stale/garbage ptr).</summary>
    private static bool IsInModule(ulong addr)
    {
        if (_modBase != 0 && addr >= _modBase && addr < _modEnd) return true;
        (_modBase, _modEnd) = FlEngineRange();
        return _modBase != 0 && addr >= _modBase && addr < _modEnd;
    }

    /// <summary>Guard before callabs-ing resolved function pointers: a non-code ptr AVs and crashes FL.</summary>
    private static void EnsureInModule(string what, params ulong[] fns)
    {
        foreach (ulong fn in fns)
            if (!IsInModule(fn))
                throw new InvalidOperationException(
                    $"{what} resolution invalid (0x{fn:x} not in FLEngine 0x{_modBase:x}..0x{_modEnd:x}); aborting to avoid crashing FL.");
    }

    // Pattern indices feed FLpat_GetOrCreateNoteRecorder / the note-recorder static array directly; a wild
    // value indexes far out of bounds and AVs inside FL on the UI thread (fatal). Bound to a finite range.
    private const int MaxPatternIndex = 9999;
    private static void ValidatePattern(int idx)
    {
        if (idx < 1 || idx > MaxPatternIndex)
            throw new InvalidOperationException($"Pattern {idx} is out of range (1..{MaxPatternIndex}).");
    }

    private static ulong GhidraToRuntime(ulong ghidra)
    {
        (ulong b, ulong e) = FlEngineRange();
        return b == 0 ? 0 : b + (ghidra - 0x400000);
    }

    private async Task<ulong> FindClipAddrAsync(int startTick, short trackField, uint sourceID, ulong exclude, CancellationToken ct)
    {
        var (data, stride, count) = await ClipCollectionAsync(ct);
        for (int i = 0; i < count; i++)
        {
            ulong addr = data + (ulong)i * (ulong)stride;
            if (addr == exclude) continue;
            byte[] cb = await PeekAbsAsync(addr, 0x20, ct);
            if ((cb[0x13] & 0x80) == 0) continue;
            if (BitConverter.ToInt32(cb, 0) == startTick && BitConverter.ToInt16(cb, 0xc) == trackField && BitConverter.ToUInt32(cb, 4) == sourceID)
                return addr;
        }
        return 0;
    }

    /// <summary>Slice/chop a clip at an absolute tick into two clips. Audio source ranges are split so playback stays continuous.</summary>
    public async Task SliceClipAsync(int clipIndex, int tick, CancellationToken ct = default)
    {
        ulong clip = await ClipAddrAsync(clipIndex, ct);
        if (clip == 0) throw new InvalidOperationException("Clip not found.");
        byte[] cb = await PeekAbsAsync(clip, 0x20, ct);
        if ((cb[0x13] & 0x80) == 0) throw new InvalidOperationException($"Clip {clipIndex} is not an active clip.");
        int start = BitConverter.ToInt32(cb, 0);
        uint src = BitConverter.ToUInt32(cb, 4);
        int len = BitConverter.ToInt32(cb, 8);
        short trackField = BitConverter.ToInt16(cb, 0xc);
        int origEnd = start + len;
        if (tick <= start || tick >= origEnd)
            throw new InvalidOperationException($"Slice tick {tick} must be strictly inside the clip ({start}..{origEnd}).");
        int off = tick - start;
        bool audio = src < 0x50000000;
        // 1) shorten the original to the slice point
        await PokeAbsAsync((await ClipAddrAsync(clipIndex, ct)) + 8, BitConverter.GetBytes(off), ct);
        // 2) insert the second half (same source/track)
        await InsertClipRawAsync(tick, src, origEnd - tick, 500 - trackField, -1, -1, ct);
        // 3) for AUDIO, split the source window so the second half continues the sample
        if (audio)
        {
            ulong sr = GhidraToRuntime(0xF71A70);  // FLpl_SetClipSourceRange(clip, double start, double end)
            if (sr != 0)
            {
                ulong c1 = await ClipAddrAsync(clipIndex, ct);
                await CallFAbsAsync(sr, new ulong[] { c1, Bits(0), Bits(off) }, ct);
                ulong c2 = await FindClipAddrAsync(tick, trackField, src, c1, ct);
                if (c2 != 0) await CallFAbsAsync(sr, new ulong[] { c2, Bits(off), Bits(origEnd - start) }, ct);
            }
        }
        await RepaintPlaylistAsync(ct);
    }

    /// <summary>Duplicate a clip, placing the copy immediately after it on the same track.</summary>
    public async Task DuplicateClipAsync(int clipIndex, CancellationToken ct = default)
    {
        ulong clip = await ClipAddrAsync(clipIndex, ct);
        if (clip == 0) throw new InvalidOperationException("Clip not found.");
        byte[] cb = await PeekAbsAsync(clip, 0x20, ct);
        if ((cb[0x13] & 0x80) == 0) throw new InvalidOperationException($"Clip {clipIndex} is not an active clip.");
        int start = BitConverter.ToInt32(cb, 0);
        uint src = BitConverter.ToUInt32(cb, 4);
        int len = BitConverter.ToInt32(cb, 8);
        short trackField = BitConverter.ToInt16(cb, 0xc);
        int srcStart = BitConverter.ToInt32(cb, 0x18);
        int srcEnd = BitConverter.ToInt32(cb, 0x1c);
        await InsertClipRawAsync(start + len, src, len, 500 - trackField, srcStart, srcEnd, ct);
        await RepaintPlaylistAsync(ct);
    }

    // ---- song / transport state ----
    private async Task<ulong> SongArrangementAsync(CancellationToken ct)
    {
        ulong arr = await GPtrAsync("14aba80", ct);
        return arr != 0 ? arr : await GPtrAsync("14aab88", ct);
    }

    /// <summary>Reads playhead (bar/beat/tick), song-vs-pattern mode, play state, loop region and song length.</summary>
    /// <summary>FL's current status/hint bar text, cleaned of FL's "tooltip|status" split + '^' markup.</summary>
    public async Task<string> GetStatusAsync(CancellationToken ct = default)
        => CleanHint(await RawAsync("status", 4096, ct));

    // FL stores the raw hint as "tooltip|status" with '^' markup tokens (re/ui-gap-popup-hint-render). Take
    // the status side (after '|') and strip '^X' markup for a clean, human-readable status string.
    private static string CleanHint(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.StartsWith("err:", StringComparison.Ordinal)) return string.Empty;
        int bar = raw.IndexOf('|');
        string s = bar >= 0 ? raw.Substring(bar + 1) : raw;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '^') { i++; continue; }   // skip '^' + its following markup char
            sb.Append(s[i]);
        }
        return sb.ToString().Trim();
    }

    public async Task<string> GetSongStateAsync(CancellationToken ct = default)
    {
        int mode = BitConverter.ToInt32(await PeekAsync("14a8670", 4, ct), 0);      // direct int global
        int playing = BitConverter.ToInt32(await PeekAsync("14a81c0", 4, ct), 0);    // direct int global
        int ppq = await GetPpqAsync(ct);
        int ppqBar = ppq * 4;                                                        // assumes 4/4
        // Playhead + loop via the song-position object: sp = *(*(0x14AA4C8)+0x7e8)
        int tick = 0, loopStart = -1, loopEnd = -1;
        ulong a = await GPtrAsync("14aa4c8", ct);
        if (a != 0)
        {
            ulong sp = await APtrAsync(a + 0x7e8, ct);
            if (sp != 0)
            {
                tick = await AI32Async(sp + 0x3c0, ct);
                loopStart = await AI32Async(sp + 0x3b8, ct);
                loopEnd = await AI32Async(sp + 0x3bc, ct);
            }
        }
        int bar = ppqBar > 0 ? tick / ppqBar + 1 : 0;
        int beat = (ppq > 0 && ppqBar > 0) ? (tick % ppqBar) / ppq + 1 : 0;
        ulong arr = await SongArrangementAsync(ct);
        int songBars = arr != 0 ? await AI32Async(arr + 0xb04, ct) : -1;
        string loop = loopEnd > loopStart && loopStart >= 0 ? $"{loopStart}..{loopEnd}" : "none";
        return $"mode={(mode == 1 ? "song" : "pattern")} playing={(playing == 1 ? "yes" : "no")} pos=bar {bar} beat {beat} (tick {tick}) ppq={ppq} songLength={songBars} bars loop={loop}";
    }

    public async Task SetSongModeAsync(bool song, CancellationToken ct = default)
    {
        int mode = BitConverter.ToInt32(await PeekAsync("14a8670", 4, ct), 0);
        if ((mode == 1) != song) await CallAsync("ef7b20", new ulong[] { 15, 1, 2, 0xf }, ct);  // toggle song/pattern
    }

    /// <summary>Move the song playhead to an absolute tick (live-verified). The function takes the tick
    /// as a DOUBLE in XMM0 (so it uses the XMM call path); mode 0 = normal seek.</summary>
    public async Task SeekAsync(int tick, CancellationToken ct = default)
    {
        ulong rt = GhidraToRuntime(0x10e3470);  // FLtr_SeekToSongTick(double tick, byte mode)
        if (rt == 0) throw new InvalidOperationException("FLEngine module not found.");
        await CallFAbsAsync(rt, new ulong[] { Bits(tick < 0 ? 0 : tick), 0 }, ct);
    }

    public async Task<string> ListMarkersAsync(CancellationToken ct = default)
    {
        ulong arr = await SongArrangementAsync(ct);
        if (arr == 0) return "(no arrangement)";
        ulong mgr = await APtrAsync(arr + 0xd5c, ct);
        if (mgr == 0) return "(no markers)";
        ulong a = await APtrAsync(mgr, ct);
        if (a == 0) return "(no markers)";
        int count = await AI32Async(a - 8, ct);
        if (count <= 0) return "(no markers)";
        var sb = new StringBuilder();
        for (int i = 0; i < count && i < 200; i++)
        {
            ulong e = a + (ulong)i * 0x34;
            int tick = await AI32Async(e, ct);
            string nm = await ReadDelphiStringAsync(await APtrAsync(e + 8, ct), ct);
            sb.Append($"{(string.IsNullOrEmpty(nm) ? "(marker)" : nm)} @ tick {tick}\n");
        }
        return $"{count} markers:\n" + sb.ToString().TrimEnd();
    }

    public async Task AddMarkerAsync(int tick, string name, CancellationToken ct = default)
    {
        ulong arr = await SongArrangementAsync(ct);
        if (arr == 0) throw new InvalidOperationException("No arrangement.");
        ulong strPtr = await WriteDelphiStringAsync(name, ct);
        await CallAsync("d523c0", new ulong[] { arr, (uint)Math.Max(0, tick), strPtr, 0, 4, 4 }, ct);  // FLtr_AddTimelineMarkerCore
    }

    // ---- project lifecycle (verify globals live; affects the user's project) ----
    public async Task OpenProjectAsync(string path, CancellationToken ct = default)
    {
        ulong ctrl = await GPtrAsync("14a8750", ct);
        if (ctrl == 0) throw new InvalidOperationException("Main controller not found.");
        ulong strPtr = await WriteDelphiStringAsync(path, ct);
        await CallAsync("10d50c0", new ulong[] { ctrl, strPtr, 1 }, ct);  // FLproj_OpenProject
    }

    public async Task SaveProjectAsync(string path, CancellationToken ct = default)
    {
        ulong song = await GPtrAsync("1581200", ct);
        if (song == 0) throw new InvalidOperationException("Project object not found.");
        ulong wpath = string.IsNullOrWhiteSpace(path) ? await GPtrAsync("1581298", ct) : await WriteDelphiStringAsync(path, ct);
        if (wpath == 0) throw new InvalidOperationException("No save path (project not yet saved — pass an explicit path).");
        ulong outSlot = await ScratchAsync(ct) + 0x400;
        await PokeAbsAsync(outSlot, new byte[8], ct);
        await CallAsync("10d6190", new ulong[] { song, wpath, 1, outSlot }, ct);  // FLproj_SaveProjectToFlp
    }

    public async Task NewProjectAsync(CancellationToken ct = default)
    {
        ulong pm0 = await GPtrAsync("14abca8", ct);
        if (pm0 == 0) throw new InvalidOperationException("Project manager not found.");
        ulong projMgr = await APtrAsync(pm0 + 0x10, ct);
        if (projMgr == 0) throw new InvalidOperationException("Project manager not found.");
        await PokeAbsAsync(projMgr + 0x11, new byte[] { 1 }, ct);
        ulong vt = await APtrAsync(projMgr, ct);
        ulong fn = await APtrAsync(vt + 0x30, ct);
        EnsureInModule("New project", fn);
        await CallAbsAsync(fn, new ulong[] { projMgr, 1, 1, 0 }, ct);  // projMgr.New
    }

    /// <summary>Reads the current project's title, path, and saved/untitled state.</summary>
    public async Task<string> GetProjectInfoAsync(CancellationToken ct = default)
    {
        ulong pathPtr = await GPtrAsync("1581298", ct);
        ulong titlePtr = await GPtrAsync("15812a0", ct);
        string path = pathPtr != 0 ? await ReadDelphiStringAsync(pathPtr, ct) : "";
        string title = titlePtr != 0 ? await ReadDelphiStringAsync(titlePtr, ct) : "";
        bool untitled = string.IsNullOrEmpty(path) || Path.GetFileName(path).Equals("untitled.flp", StringComparison.OrdinalIgnoreCase);
        return $"Title: {(string.IsNullOrEmpty(title) ? "(untitled)" : title)}\nPath: {(string.IsNullOrEmpty(path) ? "(none)" : path)}\nSaved: {(untitled ? "no — untitled" : "yes")}";
    }

    private async Task SetProjectPathAsync(ulong song, string path, CancellationToken ct)
    {
        ulong s = await WriteDelphiStringAsync(path, ct);
        await CallAsync("10d2c90", new ulong[] { song, s, s, 1 }, ct);  // FLproj_SetProjectPath(song, path, name, addToRecent)
    }

    /// <summary>Save As: writes to a new path AND makes it the current project (updates title + recent files).</summary>
    public async Task SaveProjectAsAsync(string path, CancellationToken ct = default)
    {
        ulong song = await GPtrAsync("1581200", ct);
        if (song == 0) throw new InvalidOperationException("Project object not found.");
        ulong s = await WriteDelphiStringAsync(path, ct);
        ulong outSlot = await ScratchAsync(ct) + 0x400;
        await PokeAbsAsync(outSlot, new byte[8], ct);
        await CallAsync("10d6190", new ulong[] { song, s, 1, outSlot }, ct);  // FLproj_SaveProjectToFlp
        await SetProjectPathAsync(song, path, ct);
    }

    /// <summary>Save a copy to a path WITHOUT changing the current project path/title (verified: SaveProjectToFlp leaves the path).</summary>
    public Task SaveCopyAsync(string path, CancellationToken ct = default) => SaveProjectAsync(path, ct);

    /// <summary>Save an auto-incremented new version (project_2.flp, _3.flp, …) and make it current.</summary>
    public async Task SaveNewVersionAsync(CancellationToken ct = default)
    {
        ulong song = await GPtrAsync("1581200", ct);
        ulong cur = await GPtrAsync("1581298", ct);
        if (song == 0 || cur == 0) throw new InvalidOperationException("Project not yet saved — use save_project_as first.");
        ulong outStr = await ScratchAsync(ct) + 0x200;
        await PokeAbsAsync(outStr, new byte[8], ct);
        await CallAsync("7f7800", new ulong[] { outStr, cur }, ct);  // FLproj_AutoIncrementFileName(&out, srcPath)
        ulong np = await APtrAsync(outStr, ct);
        if (np == 0) throw new InvalidOperationException("Could not compute the next version filename.");
        ulong outSlot = await ScratchAsync(ct) + 0x400;
        await PokeAbsAsync(outSlot, new byte[8], ct);
        await CallAsync("10d6190", new ulong[] { song, np, 1, outSlot }, ct);
        await CallAsync("10d2c90", new ulong[] { song, np, np, 1 }, ct);  // SetProjectPath
    }

    /// <summary>Lists the recent-projects (MRU) list.</summary>
    public async Task<string> ListRecentProjectsAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < 49 && shown < 20; i++)
        {
            ulong ptr = await GPtrAsync((0x1581320 + (ulong)i * 8).ToString("x"), ct);
            if (ptr == 0) continue;
            string s = await ReadDelphiStringAsync(ptr, ct);
            if (!string.IsNullOrEmpty(s)) { sb.Append(Path.GetFileName(s)).Append("  —  ").Append(s).Append('\n'); shown++; }
        }
        return shown == 0 ? "(no recent projects)" : sb.ToString().TrimEnd();
    }

    // ---- arrangements ----
    public async Task<string> ListArrangementsAsync(CancellationToken ct = default)
    {
        int count = (int)await CallAsync("11fb1a0", Array.Empty<ulong>(), ct);          // FLpl_GetArrangementCount
        int current = BitConverter.ToInt32(await PeekAsync("149e8b4", 4, ct), 0);
        if (count <= 0) return "(no arrangements)";
        ulong outSlot = await ScratchAsync(ct) + 0x300;
        var sb = new StringBuilder();
        for (int i = 0; i < count && i < 64; i++)
        {
            await PokeAbsAsync(outSlot, new byte[8], ct);
            await CallAsync("11fb160", new ulong[] { outSlot, (uint)i }, ct);           // FLpl_GetArrangementName(&out, idx)
            string name = await ReadDelphiStringAsync(await APtrAsync(outSlot, ct), ct);
            sb.Append(i == current ? "* [" : "  [").Append(i).Append("] ").Append(string.IsNullOrEmpty(name) ? "(unnamed)" : name).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    public async Task<int> AddArrangementAsync(string? name, CancellationToken ct = default)
    {
        int newIdx = (int)await CallAsync("11fabc0", new ulong[] { 1, 0 }, ct);         // FLpl_AddArrangement(switchTo=1, copyModes=0)
        if (!string.IsNullOrWhiteSpace(name))
            await CallAsync("11fb0d0", new ulong[] { (uint)newIdx, await WriteDelphiStringAsync(name, ct) }, ct);
        return newIdx;
    }

    public async Task<int> CloneArrangementAsync(int srcIdx, string? name, CancellationToken ct = default)
    {
        if (srcIdx < 0) srcIdx = BitConverter.ToInt32(await PeekAsync("149e8b4", 4, ct), 0);  // default = current
        int newIdx = (int)await CallAsync("11fabc0", new ulong[] { 0, 0 }, ct);         // add (don't switch yet)
        await CallAsync("11fb420", new ulong[] { (uint)srcIdx, (uint)newIdx }, ct);     // FLpl_CopyArrangementInto(src, dst) — deep copy incl. clips
        if (!string.IsNullOrWhiteSpace(name))
            await CallAsync("11fb0d0", new ulong[] { (uint)newIdx, await WriteDelphiStringAsync(name, ct) }, ct);
        await CallAsync("11fc880", new ulong[] { (uint)newIdx }, ct);                   // FLpl_SetCurrentArrangement
        return newIdx;
    }

    public async Task RenameArrangementAsync(int idx, string name, CancellationToken ct = default)
        => await CallAsync("11fb0d0", new ulong[] { (uint)idx, await WriteDelphiStringAsync(name, ct) }, ct);

    public async Task DeleteArrangementAsync(int idx, CancellationToken ct = default)
    {
        // FLpl_DeleteArrangement triggers an autosave (FLpl_AutoSaveHook) that writes a full .flp on FL's
        // main thread and stalls the bridge call. Suppress it by setting the load-in-progress flag
        // (*(0x14A8748)) = 1 around the delete (verified: delete then runs in ~40ms, no hang).
        ulong fptr = await GPtrAsync("14a8748", ct);
        byte saved = fptr != 0 ? (await PeekAbsAsync(fptr, 1, ct))[0] : (byte)0;
        if (fptr != 0) await PokeAbsAsync(fptr, new byte[] { 1 }, ct);
        try { await CallAsync("11fb1c0", new ulong[] { (uint)idx, 1, 1 }, ct); }       // FLpl_DeleteArrangement(idx, adjustCurrent, addUndo)
        finally { if (fptr != 0) await PokeAbsAsync(fptr, new byte[] { saved }, ct); }
    }

    public async Task SelectArrangementAsync(int idx, CancellationToken ct = default)
        => await CallAsync("11fc880", new ulong[] { (uint)idx }, ct);

    // ---- automation clips ----
    // channel -> container *(ch+0x390) -> env *(cont+0x10) -> points dynarray *(env+0x28) (0x20-byte
    // elements: deltaTime f32@0, value f32@4, tension f32@8, curve u8@0xc; count @arr-8). Times are in
    // beats (default 2nd point at 4.0 = one 4/4 bar). Make one with native_add_channel("Automation Clip").
    private async Task<ulong> AutomationEnvAsync(int channel, CancellationToken ct)
    {
        ulong ch = await ChannelObjAsync(channel, ct);
        if (ch == 0) return 0;
        ulong cont = await APtrAsync(ch + 0x390, ct);
        return cont == 0 ? 0 : await APtrAsync(cont + 0x10, ct);
    }

    public async Task<string> ListAutomationPointsAsync(int channel, CancellationToken ct = default)
    {
        ulong env = await AutomationEnvAsync(channel, ct);
        if (env == 0) return $"Channel {channel} is not an automation clip.";
        ulong arr = await APtrAsync(env + 0x28, ct);
        if (arr == 0) return "(no points)";
        int n = BitConverter.ToInt32(await PeekAbsAsync(arr - 8, 4, ct), 0);
        if (n <= 0 || n > 4000) return "(no points)";
        var sb = new StringBuilder($"{n} points (time in beats):\n");
        double abs = 0;
        for (int i = 0; i < n; i++)
        {
            byte[] p = await PeekAbsAsync(arr + (ulong)i * 0x20, 0x10, ct);
            abs += BitConverter.ToSingle(p, 0);
            sb.Append($"  [{i}] t={abs:0.###} value={BitConverter.ToSingle(p, 4):0.###} tension={BitConverter.ToSingle(p, 8):0.###} curve={p[0xc]}\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Add an automation point (time in beats, value 0..1, tension -1..1) — inserts in time order
    /// and rebuilds the curve (integer-only SetLength + poke + recompute; no XMM needed).</summary>
    public async Task AddAutomationPointAsync(int channel, double timeBeats, double value, double tension, CancellationToken ct = default)
    {
        ulong env = await AutomationEnvAsync(channel, ct);
        if (env == 0) throw new InvalidOperationException($"Channel {channel} is not an automation clip.");
        ulong arr = await APtrAsync(env + 0x28, ct);
        int n = arr != 0 ? BitConverter.ToInt32(await PeekAbsAsync(arr - 8, 4, ct), 0) : 0;
        if (n < 0 || n > 4000) n = 0;
        var pts = new List<(double t, float v, float ten, byte cv)>();
        double acc = 0;
        for (int i = 0; i < n; i++)
        {
            byte[] p = await PeekAbsAsync(arr + (ulong)i * 0x20, 0x10, ct);
            acc += BitConverter.ToSingle(p, 0);
            pts.Add((acc, BitConverter.ToSingle(p, 4), BitConverter.ToSingle(p, 8), p[0xc]));
        }
        pts.Add((timeBeats, (float)Math.Clamp(value, 0, 1), (float)Math.Clamp(tension, -1, 1), 0));
        pts.Sort((x, y) => x.t.CompareTo(y.t));
        int N = pts.Count;
        await CallAsync("417fc0", new ulong[] { env + 0x28, GhidraToRuntime(0xB2C678), 1, (uint)N }, ct);  // SetLength
        ulong arr2 = await APtrAsync(env + 0x28, ct);
        double prev = 0;
        for (int i = 0; i < N; i++)
        {
            ulong p = arr2 + (ulong)i * 0x20;
            await PokeAbsAsync(p + 0, BitConverter.GetBytes((float)(pts[i].t - prev)), ct); prev = pts[i].t;
            await PokeAbsAsync(p + 4, BitConverter.GetBytes(pts[i].v), ct);
            await PokeAbsAsync(p + 8, BitConverter.GetBytes(pts[i].ten), ct);
            await PokeAbsAsync(p + 0xc, new byte[1] { pts[i].cv }, ct);
            await PokeAbsAsync(p + 0xd, new byte[0x13], ct);  // zero cached coefficients
        }
        ulong vt = await APtrAsync(env, ct);
        ulong rec = await APtrAsync(vt + 0x40, ct);
        if (IsInModule(rec)) await CallAbsAsync(rec, new ulong[] { env }, ct);  // recompute
        await RefreshRackAsync(ct);
    }

    /// <summary>Delete an automation point by index (FLac_DeletePoint handles delta-fixup + recompute).</summary>
    public async Task DeleteAutomationPointAsync(int channel, int index, CancellationToken ct = default)
    {
        ulong env = await AutomationEnvAsync(channel, ct);
        if (env == 0) throw new InvalidOperationException($"Channel {channel} is not an automation clip.");
        await CallAsync("b30ad0", new ulong[] { env, (uint)index }, ct);  // FLac_DeletePoint
        await RefreshRackAsync(ct);
    }

    // ---- render / export ----
    /// <summary>Opens FL's audio Export dialog (the user picks format/path + clicks Render). A fully
    /// headless render needs a captured config24 (see re/12). pm=*(*(0x14ABCA8)+0x10); pm-&gt;vtbl[0x48](pm, fmt).
    ///
    /// <para>NON-BLOCKING by contract. <c>FLproj_FileExportFormat</c> opens a MODAL dialog on FL's
    /// main thread and enters a nested message loop that does not return until the user dismisses it —
    /// so the marshaled main-thread call would otherwise block the bridge (and the agent) for as long
    /// as the dialog is open, while transport/playback is stopped. We therefore issue it with a short
    /// bounded timeout and treat the (expected) timeout as "the dialog is now open for the user"; the
    /// bridge is freed immediately. This op is user-initiated only (it is intentionally NOT exposed as
    /// an agent tool — an unprompted modal mid-task freezes playback; see NativeControlPlugin).</para></summary>
    public async Task OpenExportDialogAsync(int formatIndex = 0, CancellationToken ct = default)
    {
        ulong c = await GPtrAsync("14abca8", ct);
        if (c == 0) throw new InvalidOperationException("Project controller not found.");
        ulong pm = await APtrAsync(c + 0x10, ct);
        if (pm == 0) throw new InvalidOperationException("Project manager not found.");
        ulong vt = await APtrAsync(pm, ct);
        ulong fn = await APtrAsync(vt + 0x48, ct);
        EnsureInModule("Export dispatcher", fn);
        // Fire-and-return: the dispatcher blocks for the lifetime of the modal, so a short timeout is
        // the expected, healthy outcome — do NOT let it wedge the bridge/agent.
        try { await CallAbsAsync(fn, new ulong[] { pm, (uint)formatIndex }, ct, timeoutMs: 1500); }
        catch (TimeoutException) { /* expected: the export dialog is now open; the user drives it */ }
    }
}
