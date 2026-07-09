using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using FruityLink.Core.Abstractions;

namespace FruityLink.FlStudio.Inject;

// Raw memory / call primitives: call/peek/poke (Ghidra + absolute), scratch buffer, vtable calls, pointer-walk helpers, Delphi strings, FLEngine module range guards.
// Partial of the FlInjectBridge god-class split; see FlInjectBridge.cs for the class doc.
public sealed partial class FlInjectBridge
{
    // ---- bridge response-envelope parsing (shared by every call/poke/peek variant) ----------------

    /// <summary>Throws when the bridge reports the op SEH-faulted inside FL (<c>ok:0</c>).
    /// <paramref name="error"/> is the exact per-call-site exception message.</summary>
    private static void EnsureOk(JsonElement root, string error)
    {
        if (root.TryGetProperty("ok", out var ok) && ok.GetInt32() == 0)
            throw new InvalidOperationException(error);
    }

    /// <summary>Reads a hex register value ("0x…") off the bridge's JSON reply (e.g. <c>ret</c>, <c>xmm0</c>).</summary>
    private static ulong ParseHexRet(JsonElement root, string prop)
        => Convert.ToUInt64((root.GetProperty(prop).GetString() ?? "0x0").Replace("0x", string.Empty), 16);

    /// <summary>Decodes a hex-string bridge reply into bytes, throwing on the bridge's "err…" envelope.
    /// <paramref name="context"/> names the command for the "<c>{context} failed: …</c>" message.</summary>
    private static byte[] DecodeHexResponse(string hex, string context)
    {
        if (hex.StartsWith("err", StringComparison.Ordinal)) throw new InvalidOperationException($"{context} failed: {hex}");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

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
        EnsureOk(root, $"bridge call faulted (ok:0): {msg}");
        return ParseHexRet(root, "ret");
    }

    /// <summary>Read <paramref name="len"/> bytes at a Ghidra address (mapped to the live FLEngine base).</summary>
    internal async Task<byte[]> PeekAsync(string ghidraHexAddr, int len, CancellationToken ct = default)
        => DecodeHexResponse((await RawAsync($"peek {ghidraHexAddr} {len}", 4000, ct)).Trim(), "peek");

    /// <summary>Write raw bytes at a Ghidra address (direct struct edit; use only where no FL command exists).</summary>
    internal async Task PokeAsync(string ghidraHexAddr, byte[] data, CancellationToken ct = default)
    {
        string hex = Convert.ToHexString(data).ToLowerInvariant();
        string resp = await RawAsync($"poke {ghidraHexAddr} {hex}", 4000, ct);
        using var doc = JsonDocument.Parse(resp);
        EnsureOk(doc.RootElement, $"poke failed at {ghidraHexAddr}: {resp}");
    }

    /// <summary>Write raw bytes at an absolute live address (e.g. a resolved heap-object field).</summary>
    internal async Task PokeAbsAsync(ulong absAddr, byte[] data, CancellationToken ct = default)
    {
        string hex = Convert.ToHexString(data).ToLowerInvariant();
        string resp = await RawAsync($"pokeabs {absAddr:x} {hex}", 4000, ct);
        using var doc = JsonDocument.Parse(resp);
        EnsureOk(doc.RootElement, $"pokeabs failed at 0x{absAddr:x}: {resp}");
    }

    // ---- low-level helpers for struct/out-param calls (scratch + absolute peek/call) ----
    private async Task<ulong> ScratchAsync(CancellationToken ct = default)
    {
        string resp = (await RawAsync("scratch", 2000, ct)).Trim();
        if (resp.StartsWith("err", StringComparison.Ordinal)) throw new InvalidOperationException($"scratch failed: {resp}");
        return Convert.ToUInt64(resp.Replace("0x", string.Empty), 16);
    }

    /// <summary>Scratch-buffer slot at <paramref name="offset"/>, zeroed as a valid (empty) 8-byte
    /// out-param — the prep every Delphi hidden-out-param call needs (a garbage slot faults the assign).</summary>
    private async Task<ulong> ZeroedScratchSlotAsync(ulong offset, CancellationToken ct)
    {
        ulong slot = await ScratchAsync(ct) + offset;
        await PokeAbsAsync(slot, new byte[8], ct);
        return slot;
    }

    private async Task<byte[]> PeekAbsAsync(ulong addr, int len, CancellationToken ct = default)
        => DecodeHexResponse((await RawAsync($"peekabs {addr:x} {len}", 4000, ct)).Trim(), "peekabs");

    private async Task<ulong> CallAbsAsync(ulong addr, ulong[] args, CancellationToken ct = default, int timeoutMs = 5000)
    {
        string msg = "callabs " + addr.ToString("x") + string.Concat(args.Select(a => " " + a.ToString("x")));
        string resp = await RawAsync(msg, timeoutMs, ct);
        using var doc = JsonDocument.Parse(resp);
        var root = doc.RootElement;
        EnsureOk(root, $"callabs faulted (ok:0): {msg}");
        return ParseHexRet(root, "ret");
    }

    /// <summary>Guarded virtual call: resolve <c>fn = *(*obj + slotOffset)</c>, guard the pointer against
    /// the FLEngine module range (a non-code ptr AVs and crashes FL), then <c>callabs</c> it.
    /// <paramref name="what"/> labels the guard's exception. NOT for hosted-plugin (VST) instance methods —
    /// those legitimately live outside FLEngine (see <see cref="ReadPluginTextAsync"/>).</summary>
    private async Task<ulong> CallVtblAsync(ulong obj, ulong slotOffset, string what, ulong[] args, CancellationToken ct, int timeoutMs = 5000)
    {
        ulong vt = await APtrAsync(obj, ct);
        ulong fn = await APtrAsync(vt + slotOffset, ct);
        EnsureInModule(what, fn);
        return await CallAbsAsync(fn, args, ct, timeoutMs);
    }

    /// <summary>XMM-capable call (loads args into GP + XMM0-3 via the bridge thunk). Returns RAX and the
    /// raw 8-byte XMM0 (use <see cref="XmmFloat"/>; a 64-bit double comes back the same way via
    /// <c>BitConverter.Int64BitsToDouble</c>). For float-arg/float-return engine fns.</summary>
    private async Task<(ulong rax, ulong xmm0)> CallFAbsAsync(ulong addr, ulong[] argBits, CancellationToken ct = default)
    {
        string msg = "callfabs " + addr.ToString("x") + string.Concat(argBits.Select(a => " " + a.ToString("x")));
        string resp = await RawAsync(msg, 5000, ct);
        using var doc = JsonDocument.Parse(resp);
        var root = doc.RootElement;
        EnsureOk(root, $"callfabs faulted (ok:0): {msg}");
        return (ParseHexRet(root, "ret"), ParseHexRet(root, "xmm0"));
    }

    /// <summary>Encode a double as its 64-bit pattern for passing to <see cref="CallFAbsAsync"/>.</summary>
    private static ulong Bits(double d) => unchecked((ulong)BitConverter.DoubleToInt64Bits(d));
    /// <summary>Interpret the low 32 bits of an XMM0 result as a 32-bit float (Win64 float returns).</summary>
    private static float XmmFloat(ulong xmm0) => BitConverter.Int32BitsToSingle(unchecked((int)(uint)xmm0));

    /// <summary>Builds a Delphi UnicodeString (refcnt -1 constant) in the scratch buffer; returns the chars ptr.
    /// TRANSIENT: safe only for args CONSUMED during the call. For a name FL STORES (renames), use
    /// <see cref="MakeOwnedDelphiStringAsync"/> — FL's UStrAsg setters SHARE (don't copy) a refcnt-(-1) const,
    /// so this pointer would dangle into scratch once reused.</summary>
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

    /// <summary>
    /// Returns a Delphi UnicodeString const (chars ptr) to hand to FL's name setters for a rename. FL assigns
    /// names via <c>Delphi_UStrAsg</c> (0x4133F0), which DEEP-COPIES a refcount-(-1) const into an FL-OWNED
    /// heap string — so the name PERSISTS (survives scratch reuse + save/reload) with no dangling pointer and
    /// no need for FL's own allocator. Verified by the bridge's own <c>makeUStr</c> (whose comment states
    /// exactly this) driving the menus/tab/hint, and by the shipped playlist rename (<c>SetTrackNameAsync</c>)
    /// passing this same const to its setter. The earlier version ALSO called FL 0x54C5E0 — misidentified as
    /// "makeUStr"; it is NOT, and it FAULTED (confirmed live: native_set_pattern_name → "call 54c5e0" ok:0).
    /// The const alone is the fix; the setter does the owning copy. Kept as a helper so every rename shares it.
    /// </summary>
    private Task<ulong> MakeOwnedDelphiStringAsync(string name, CancellationToken ct)
        => WriteDelphiStringAsync(name ?? string.Empty, ct);

    /// <summary>Delphi_UStrAsg(&amp;destField, srcConst) @0x4133F0 — deep-copies a refcount-(-1) const into an
    /// FL-owned heap string at the <paramref name="destFieldAddr"/> string-pointer field. This is FL's own
    /// name-assignment primitive (the bridge uses it for the browser tab name); use it to set a NAME field
    /// directly when there's no higher-level setter (e.g. a mixer track's name pointer), so the string persists
    /// instead of dangling like a raw poke of a scratch pointer would.</summary>
    private Task<ulong> UStrAsgAsync(ulong destFieldAddr, ulong srcConstChars, CancellationToken ct)
        => CallAsync("4133f0", new ulong[] { destFieldAddr, srcConstChars }, ct);

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

    /// <summary>Read an int that lives behind FL's .data→.bss indirection table: the <paramref name="ghidraSlot"/>
    /// global holds a POINTER to the real int, so the correct read is <c>*(*(slot))</c>. Several transport flags
    /// (song/pattern mode 0x14a8670, play state 0x14a81c0) are stored this way; reading them as a DIRECT int
    /// returns the pointer value (never 0/1) and silently breaks mode/play detection. Returns 0 on a bad chain.</summary>
    private async Task<int> DerefI32Async(string ghidraSlot, CancellationToken ct)
    {
        ulong p = await GPtrAsync(ghidraSlot, ct);
        if (p == 0) return 0;
        try { return await AI32Async(p, ct); } catch { return 0; }
    }

    private static (ulong b, ulong e) FlEngineRange()
    {
        var m = Process.GetProcessesByName("FL64").FirstOrDefault()?.Modules.Cast<ProcessModule>()
            .FirstOrDefault(x => x.ModuleName.StartsWith("FLEngine", StringComparison.OrdinalIgnoreCase));
        ulong b = m != null ? (ulong)m.BaseAddress.ToInt64() : 0;
        return (b, m != null ? b + (ulong)m.ModuleMemorySize : 0);
    }

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

    private static ulong GhidraToRuntime(ulong ghidra)
    {
        (ulong b, ulong e) = FlEngineRange();
        return b == 0 ? 0 : b + (ghidra - 0x400000);
    }
}
