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
public sealed partial class FlInjectBridge : FruityLink.Core.Abstractions.INativeFlControl
{
    public const string PipeName = "FruityLinkBridge";

    /// <summary>Ghidra address of FL_DispatchCommand; the bridge maps it to the live FLEngine base.</summary>
    private const string CmdBusAddr = "f53fe0";

    // Dispatch flags observed on the FL_DispatchCommand call sites.
    // (0x185 = the slider-drag flag was also observed on the call sites; unused here — see re/08.)
    public const uint FlagWheelOrScript = 0x3DD;

    // Generic param protocol on the command bus (live-verified): GET returns current value; SET writes it.
    public const uint FlagSet = 0x11; // bit0 = SET
    public const uint FlagGet = 0x02; // bit1 = GET (returns current value in RAX)

    // Global command ids (0x4000xxxx) — the complete master/global group, all live-verified.
    public const uint CmdMasterVolume = 0x40000000; // 0..12800 (~7624 = 0 dB-ish default)
    public const uint CmdShuffle      = 0x40000001; // 0..128 swing
    public const uint CmdMasterPitch  = 0x40000002; // cents, -1200..+1200
    public const uint CmdSetTempo     = 0x40000005; // value = BPM * 1000 (10000..0x7F710)
}
