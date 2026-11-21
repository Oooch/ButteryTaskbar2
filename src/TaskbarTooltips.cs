using static ButteryTaskbar3.NativeMethods;

namespace ButteryTaskbar3;

// Tooltips on the classic taskbar (Windows 10, or ExplorerPatcher on Windows 11) are separate
// top-level tooltip windows owned by the taskbar window.
internal static class TaskbarTooltips
{
    private const uint TTM_ACTIVATE = WM_USER + 1;
    private const uint TTM_POP = WM_USER + 28;

    private static readonly HashSet<string> TaskbarOwnerClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
    };

    // Hiding the taskbar mid-hover strands its tooltip on screen, since the taskbar never sees the mouse leave.
    public static void HideVisible()
    {
        foreach (var tooltip in Find())
        {
            if (IsWindowVisible(tooltip))
                PostMessage(tooltip, TTM_POP, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        var tooltips = Find();
        foreach (var tooltip in tooltips)
            PostMessage(tooltip, TTM_ACTIVATE, enabled ? 1 : 0, IntPtr.Zero);
        Log.Write($"Taskbar tooltips {(enabled ? "enabled" : "disabled")} ({tooltips.Count} windows)");
    }

    private static List<IntPtr> Find()
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (TaskbarManager.GetWindowClassName(hwnd) == "tooltips_class32")
            {
                var owner = GetWindow(hwnd, GW_OWNER);
                if (owner != IntPtr.Zero && TaskbarOwnerClasses.Contains(TaskbarManager.GetWindowClassName(owner)))
                    result.Add(hwnd);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
