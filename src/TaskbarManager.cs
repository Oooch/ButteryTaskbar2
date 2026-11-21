using System.Diagnostics;
using System.Runtime.InteropServices;
using static ButteryTaskbar3.NativeMethods;

namespace ButteryTaskbar3;

// All show/hide calls on the taskbar windows happen on this class's worker thread.
internal sealed class TaskbarManager
{
    private readonly ManualResetEventSlim _wake = new(false);
    private Thread? _thread;
    private volatile bool _stopping;
    private volatile bool _showForFocus = true;
    private volatile bool _winKeyDown;
    private volatile bool _openStartRequested;
    private long _keepVisibleUntil;

    public bool ShowForFocus
    {
        get => _showForFocus;
        set { _showForFocus = value; _wake.Set(); }
    }

    public bool WinKeyDown
    {
        get => _winKeyDown;
        set { _winKeyDown = value; _wake.Set(); }
    }

    // Covers the gap between releasing the Win key and the Start menu becoming the foreground window.
    public void KeepVisibleFor(int milliseconds)
    {
        Interlocked.Exchange(ref _keepVisibleUntil, Environment.TickCount64 + milliseconds);
        _wake.Set();
    }

    public void RequestOpenStart()
    {
        _openStartRequested = true;
        _wake.Set();
    }

    public void Wake() => _wake.Set();

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "TaskbarManager" };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        _wake.Set();
        _thread?.Join(2000);
    }

    private void Loop()
    {
        while (true)
        {
            _wake.Wait();
            _wake.Reset();
            if (_stopping) return;

            if (_openStartRequested)
            {
                _openStartRequested = false;
                SimulateWinKeyTap();
            }

            var taskbars = FindTaskbars();
            bool tooltipsHidden = false;
            int attempts = 0;
            while (attempts < 60 && !_stopping)
            {
                bool inGracePeriod = Environment.TickCount64 < Interlocked.Read(ref _keepVisibleUntil);
                bool show = _showForFocus || _winKeyDown || inGracePeriod;

                if (!show && !tooltipsHidden)
                {
                    TaskbarTooltips.HideVisible();
                    tooltipsHidden = true;
                }

                bool allCorrect = true;
                foreach (var hwnd in taskbars)
                {
                    ShowWindow(hwnd, show ? SW_SHOWNOACTIVATE : SW_HIDE);
                    if (IsWindowVisible(hwnd) != show) allCorrect = false;
                }

                if (inGracePeriod)
                {
                    attempts = 0;
                    Thread.Sleep(100);
                }
                else if (!allCorrect)
                {
                    attempts++;
                    Thread.Sleep(10);
                }
                else if (show)
                {
                    break;
                }
                else
                {
                    // Explorer sometimes re-shows the taskbar right after we hide it, so keep re-hiding briefly.
                    attempts += 8;
                    Thread.Sleep(50);
                }
            }
        }
    }

    private static List<IntPtr> FindTaskbars()
    {
        var result = new List<IntPtr>();
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) result.Add(primary);

        var hwnd = IntPtr.Zero;
        while ((hwnd = FindWindowEx(IntPtr.Zero, hwnd, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            result.Add(hwnd);
        return result;
    }

    public static void ShowAllTaskbars()
    {
        foreach (var hwnd in FindTaskbars())
            ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    }

    // Windows' own auto-hide setting. Keeping it on gives maximised windows the full screen height.
    public static void SetAutoHide(bool autoHide)
    {
        var abd = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = FindWindow("Shell_TrayWnd", null),
            lParam = autoHide ? ABS_AUTOHIDE : IntPtr.Zero,
        };
        SHAppBarMessage(ABM_SETSTATE, ref abd);
    }

    public static bool IsAutoHideOn()
    {
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return (SHAppBarMessage(ABM_GETSTATE, ref abd) & ABS_AUTOHIDE) != 0;
    }

    private static void SimulateWinKeyTap()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = VK_LWIN;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = VK_LWIN;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        uint sent = SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        Log.Write($"SendInput Win tap: sent {sent}/2 (sizeof INPUT = {Marshal.SizeOf<INPUT>()})");
    }

    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",            // Windows 10 tray overflow
        "TopLevelWindowForOverflowXamlIsland", // Windows 11 tray overflow
        // Not XamlExplorerHostIslandWindow (Task View): Alt+Tab uses the same window, which flashed the
        // taskbar on every switch and could report focus after the target app, leaving the taskbar stuck up.
    };

    // CoreWindow is also used by ordinary apps, so it only counts when one of these shell processes owns it.
    private static readonly HashSet<string> ShellCoreWindowProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "SearchHost",              // Start menu and search on Windows 11
        "StartMenuExperienceHost", // Start menu on other builds
        "SearchApp",               // Search on Windows 10
        "ShellExperienceHost",     // Notification centre and quick settings
    };

    public static bool IsShellUi(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var className = GetWindowClassName(hwnd);
        if (ShellWindowClasses.Contains(className)) return true;
        return className == "Windows.UI.Core.CoreWindow"
            && ShellCoreWindowProcesses.Contains(GetProcessName(hwnd));
    }

    public static string GetWindowClassName(IntPtr hwnd)
    {
        var buf = new char[256];
        int len = GetClassName(hwnd, buf, buf.Length);
        return new string(buf, 0, len);
    }

    public static string GetProcessName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }
}
