using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace FruityLink.Core.Diagnostics;

/// <summary>Reads FL's custom message text and choice identity on verified engine builds.
/// Opens the process for query/read access only; never writes memory, calls FL code, or answers a dialog.</summary>
[SupportedOSPlatform("windows")]
public static class FlDialogInspector
{
    /// <summary>Return a verified message snapshot, or null for an unavailable, changed or unsupported dialog.</summary>
    /// <param name="processId">Expected process owner.</param><param name="window">Message HWND.</param>
    public static FlDialogInspection? TryInspect(int processId, nint window)
    {
        try
        {
            if (!BelongsTo(window, processId)) return null;
            using var process = Process.GetProcessById(processId);
            using var engine = process.Modules.Cast<ProcessModule>().FirstOrDefault(module =>
                string.Equals(module.ModuleName, "FLEngine_x64.dll", StringComparison.OrdinalIgnoreCase));
            if (engine is null) return null;
            var layout = Layout(FileVersionInfo.GetVersionInfo(engine.FileName).FileVersion);
            if (layout is null) return null;
            using var memory = new ProcessMemory(processId);
            var form = ObjectFor(window);
            var buttons = ChildControls(window, processId, "TQuickFocusBtn", 2);
            var body = ChildControls(window, processId, "TQuickMemo", 1)[0];
            var result = FlDialogDecoder.Read(memory, layout, (ulong)engine.BaseAddress, window, form, body, buttons);
            return BelongsTo(window, processId) ? result : null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or IOException or InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    internal static FlDialogLayout? Layout(string? version) => version?.Replace(", ", ".", StringComparison.Ordinal) switch
    {
        "26.1.3.5570" => new(0x7C8, 0x634, 0x510, 0x1E4, 0x1EC, 0x563960),
        "25.2.5.5319" => new(0x7A0, 0x624, 0x510, 0x1E4, 0x1EC, 0x689E60),
        _ => null
    };

    private static IReadOnlyList<(nint Window, ulong Object)> ChildControls(nint parent, int processId, string windowClass, int expected)
    {
        var windows = new List<nint>();
        EnumChildWindows(parent, (child, _) =>
        {
            if (windows.Count >= 64) return false;
            var name = new System.Text.StringBuilder(128);
            GetClassName(child, name, name.Capacity);
            if (name.ToString() == windowClass) windows.Add(child);
            return true;
        }, 0);
        if (windows.Count != expected) throw new InvalidDataException("Unexpected FL message child count.");
        return windows.Select(window => BelongsTo(window, processId) && IsChild(parent, window)
            ? (window, ObjectFor(window)) : throw new InvalidDataException("FL button owner changed.")).ToArray();
    }

    private static ulong ObjectFor(nint window)
    {
        var values = new List<ulong>();
        EnumPropsEx(window, (_, name, value, _) =>
        {
            if ((ulong)name <= ushort.MaxValue) return 1;
            var property = Marshal.PtrToStringUni(name);
            if (property is { Length: 26 } && property.StartsWith("ControlOfs", StringComparison.Ordinal) &&
                property.AsSpan(10).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0)
                values.Add((ulong)value);
            return values.Count > 1 ? 0 : 1;
        }, 0);
        return values.Count == 1 ? values[0] : throw new InvalidDataException("FL control property is unavailable or ambiguous.");
    }

    private static bool BelongsTo(nint window, int processId) =>
        IsWindow(window) && IsWindowVisible(window) && IsWindowEnabled(window) &&
        GetWindowThreadProcessId(window, out var pid) != 0 && pid == processId;

    private sealed class ProcessMemory : IFlDialogMemory, IDisposable
    {
        private readonly SafeProcessHandle process;
        internal ProcessMemory(int pid)
        {
            process = OpenProcess(0x1010, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ
            if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public byte[] Read(ulong address, int length)
        {
            if (length is < 1 or > 16386 || address < 0x10000 || address > 0x7FFFFFFFFFFF - (ulong)length)
                throw new InvalidDataException("FL dialog read is outside the memory inspection bounds.");
            var data = new byte[length];
            if (!ReadProcessMemory(process, (nint)address, data, (nuint)length, out var read) || read != (nuint)length)
                throw new InvalidDataException("FL dialog memory became unavailable.");
            return data;
        }
        public void Dispose() => process.Dispose();
    }

    private delegate bool EnumWindow(nint window, nint parameter);
    private delegate int EnumProperty(nint window, nint name, nint value, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindow callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int EnumPropsEx(nint window, EnumProperty callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder text, int maximum);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address,
        [Out] byte[] buffer, nuint length, out nuint read);
}
