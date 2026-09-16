using System.Runtime.InteropServices;
using System.Text;

namespace FruityLink.FlStudio.Inject;

/// <summary>Cheap, non-blocking diagnostics for a bridge timeout: is FL's UI thread hung, and which visible
/// top-level windows does this process show? Runs in-process (the bridge is loaded inside FL), never sends a
/// message that could block on the hung thread: <c>IsHungAppWindow</c>, <c>EnumWindows</c>, <c>GetClassName</c>
/// and <c>IsWindowVisible</c> read window-manager state, and titles are fetched with
/// <c>SendMessageTimeout(WM_GETTEXT, SMTO_ABORTIFHUNG)</c> so a hung owner returns nothing instead of stalling
/// the probe. Nothing here calls FL code or answers a dialog.</summary>
internal static class UiThreadProbe
{
    private const int MaxWindows = 6;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint SmtoBlock = 0x0001;
    private const uint WmGetText = 0x000D;
    private const uint WmGetTextLength = 0x000E;

    /// <summary>"visible FL windows: 'Sign in - Super VHS', 'Fruity Wrapper'" (hung main window first), or null
    /// when nothing beyond the main window is visible and the main window is not reported hung.</summary>
    public static string? Describe()
    {
        if (!OperatingSystem.IsWindows()) return null;
        uint pid = (uint)Environment.ProcessId;
        var titles = new List<string>();
        bool hung = false;
        EnumWindows((window, _) =>
        {
            if (titles.Count >= MaxWindows) return false;
            if (!IsWindowVisible(window) || GetWindowThreadProcessId(window, out uint owner) == 0 || owner != pid) return true;
            string title = TitleOf(window);
            string cls = ClassOf(window);
            if (IsHungAppWindow(window)) hung = true;
            if (title.Length == 0 && cls.Length == 0) return true;
            if (IsMainWindowClass(cls)) return true;   // FL's own frame: not the blocker
            titles.Add(title.Length > 0 ? title : $"<{cls}>");
            return true;
        }, 0);
        return Format(hung, titles);
    }

    /// <summary>Message fragment for a probe result; null when neither a hang nor an extra window was seen.</summary>
    internal static string? Format(bool hung, IReadOnlyList<string> titles)
    {
        if (!hung && titles.Count == 0) return null;
        var text = new StringBuilder();
        if (hung) text.Append("FL's UI thread is not processing messages");
        if (titles.Count > 0)
        {
            if (text.Length > 0) text.Append("; ");
            text.Append("visible FL windows: ").Append(string.Join(", ", titles.Select(t => $"'{t}'")));
        }
        return text.ToString();
    }

    private static bool IsMainWindowClass(string cls)
        => cls.StartsWith("TFruityLoopsMainForm", StringComparison.Ordinal) || cls.StartsWith("TFruityMainForm", StringComparison.Ordinal);

    private static string TitleOf(nint window)
    {
        if (SendMessageTimeout(window, WmGetTextLength, 0, 0, SmtoAbortIfHung | SmtoBlock, 50, out nint length) == 0 || length <= 0)
            return "";
        var buffer = new StringBuilder((int)Math.Min(length, 200) + 1);
        if (SendMessageTimeout(window, WmGetText, (nint)buffer.Capacity, buffer, SmtoAbortIfHung | SmtoBlock, 50, out _) == 0) return "";
        return buffer.ToString().Trim();
    }

    private static string ClassOf(nint window)
    {
        var buffer = new StringBuilder(128);
        return GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, StringBuilder lParam, uint flags, uint timeoutMs, out nint result);
}
