using System.Diagnostics;
using System.Runtime.InteropServices;
using static ButteryTaskbar3.NativeMethods;

namespace ButteryTaskbar3;

internal sealed class TaskbarManager : IDisposable
{
    private readonly List<IntPtr> _taskbarHwnds = [];
    private readonly object _lock = new();
    private Thread? _thread;
    private readonly ManualResetEventSlim _wake = new(false);
    private volatile bool _stopping;
    private volatile bool _shouldShow = true;
    private volatile bool _winKeyDown;
    private volatile bool _sendWinKeyPress;
    private long _stayVisibleUntilTicks;

    // Known "core" windows (Start menu, etc.) tracked across activations
    private readonly HashSet<IntPtr> _coreWindows = [];

    // Windows 11 barrier window: sits at the bottom edge to block auto-hide mouse trigger
    private IntPtr _barrierHwnd;
    private WndProc? _barrierWndProc; // prevent GC

    private static readonly long TicksPerMs;

    static TaskbarManager()
    {
        QueryPerformanceFrequency(out var freq);
        TicksPerMs = freq / 1000;
    }

    public bool IsWinKeyDown
    {
        get => _winKeyDown;
        set
        {
            _winKeyDown = value;
            SignalUpdate();
        }
    }

    public bool SendWinKeyPress
    {
        set => _sendWinKeyPress = value;
    }

    public bool ShouldShowDueToFocus
    {
        get => _shouldShow;
        set
        {
            _shouldShow = value;
            SignalUpdate();
        }
    }

    private static long NowMs()
    {
        QueryPerformanceCounter(out var ticks);
        return ticks / TicksPerMs;
    }

    public void ExtendVisibility(int ms)
    {
        Interlocked.Exchange(ref _stayVisibleUntilTicks, NowMs() + ms);
        SignalUpdate();
    }

    public void Start()
    {
        _thread = new Thread(TaskbarLoop) { IsBackground = true, Name = "TaskbarManager" };
        _thread.Start();
    }

    public void SignalUpdate() => _wake.Set();

    public void RefreshTaskbarState()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero)
        {
            ShouldShowDueToFocus = false;
            return;
        }

        var className = GetWindowClassName(fg);
        var processName = GetProcessName(fg);

        if (IsSystemOverlayWindow(className, processName))
        {
            ShouldShowDueToFocus = true;
            return;
        }

        // Track CoreWindow handles (used by Start menu, etc.)
        if (className == "Windows.UI.Core.CoreWindow")
        {
            _coreWindows.Add(fg);
        }

        // Check if any known core window is the foreground
        bool show = false;
        foreach (var hwnd in _coreWindows)
        {
            if (hwnd == fg)
            {
                show = true;
                break;
            }
        }
        ShouldShowDueToFocus = show;
    }

    public void SetAutoHideState(bool enabled, bool autoHideWhenDisabled)
    {
        RefreshTaskbars();
        if (_taskbarHwnds.Count == 0) return;

        var abd = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = _taskbarHwnds[0],
            lParam = (enabled || autoHideWhenDisabled) ? (IntPtr)ABS_AUTOHIDE : IntPtr.Zero,
        };
        SHAppBarMessage(ABM_SETSTATE, ref abd);
    }

    private void TaskbarLoop()
    {
        while (!_stopping)
        {
            _wake.Wait();
            _wake.Reset();
            if (_stopping) break;

            RefreshTaskbars();
            RefreshPrimaryMonitor();

            if (_sendWinKeyPress)
            {
                _sendWinKeyPress = false;
                SimulateWinKeyPress();
            }

            int attempts = 0;
            while (attempts < 60 && !_stopping)
            {
                bool shouldShow = _shouldShow || _winKeyDown || NowMs() < Interlocked.Read(ref _stayVisibleUntilTicks);

                bool allCorrect = true;
                foreach (var hwnd in _taskbarHwnds)
                {
                    SetTaskbarWindowVisibility(hwnd, shouldShow);
                    if (IsWindowVisible(hwnd) != shouldShow)
                        allCorrect = false;
                }

                // Manage barrier window
                UpdateBarrier(!shouldShow);

                if (NowMs() < Interlocked.Read(ref _stayVisibleUntilTicks))
                {
                    attempts = 0;
                    Thread.Sleep(100);
                }
                else if (!allCorrect)
                {
                    Thread.Sleep(10);
                    attempts++;
                }
                else if (shouldShow)
                {
                    break;
                }
                else
                {
                    Thread.Sleep(50);
                    attempts += 8;
                }
            }
        }
    }

    private void RefreshTaskbars()
    {
        lock (_lock)
        {
            _taskbarHwnds.Clear();
            var primary = FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero)
                _taskbarHwnds.Add(primary);

            IntPtr hwnd = IntPtr.Zero;
            while (true)
            {
                hwnd = FindWindowEx(IntPtr.Zero, hwnd, "Shell_SecondaryTrayWnd", null);
                if (hwnd == IntPtr.Zero) break;
                _taskbarHwnds.Add(hwnd);
            }
        }
    }

    private RECT _primaryMonitorWork;

    public RECT PrimaryMonitorWork => _primaryMonitorWork;
    public RECT PrimaryMonitorFull => _primaryMonitorFull;

    private RECT _primaryMonitorFull;

    private void RefreshPrimaryMonitor()
    {
        var monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        _primaryMonitorWork = info.rcWork;
        _primaryMonitorFull = info.rcMonitor;
    }

    private static void SetTaskbarWindowVisibility(IntPtr hwnd, bool visible)
    {
        ShowWindow(hwnd, visible ? SW_SHOWNOACTIVATE : SW_HIDE);

        // On Windows 11, ShowWindow may not be enough. Reinforce with SetWindowPos.
        if (!visible && IsWindowVisible(hwnd))
        {
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        }
        else if (visible && !IsWindowVisible(hwnd))
        {
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    private void UpdateBarrier(bool shouldBlock)
    {
        if (shouldBlock)
        {
            EnsureBarrierWindow();
            if (_barrierHwnd != IntPtr.Zero)
            {
                // Position at bottom edge of primary monitor, 1px tall
                var r = _primaryMonitorWork;
                // The work area excludes the taskbar when visible. Use the full monitor bottom.
                var monitor = MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(monitor, ref info);
                int bottom = info.rcMonitor.Bottom;
                int left = info.rcMonitor.Left;
                int width = info.rcMonitor.Right - info.rcMonitor.Left;

                SetWindowPos(_barrierHwnd, HWND_TOPMOST, left, bottom - 1, width, 1,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
        }
        else if (_barrierHwnd != IntPtr.Zero)
        {
            ShowWindow(_barrierHwnd, SW_HIDE);
        }
    }

    private void EnsureBarrierWindow()
    {
        if (_barrierHwnd != IntPtr.Zero) return;

        _barrierWndProc = (hWnd, msg, wParam, lParam) => DefWindowProc(hWnd, msg, wParam, lParam);

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _barrierWndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = "ButteryTaskbar3_Barrier",
            hCursor = LoadCursor(IntPtr.Zero, 32512), // IDC_ARROW
        };
        RegisterClassEx(ref wc);

        _barrierHwnd = CreateWindowEx(
            (uint)(WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE),
            "ButteryTaskbar3_Barrier",
            "",
            0x80000000, // WS_POPUP
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (_barrierHwnd != IntPtr.Zero)
        {
            // Make fully transparent
            SetLayeredWindowAttributes(_barrierHwnd, 0, 0, 0x02); // LWA_ALPHA
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    private static void SimulateWinKeyPress()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = (ushort)VK_LWIN;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = (ushort)VK_LWIN;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    public static string GetWindowClassName(IntPtr hwnd)
    {
        var buf = new char[256];
        int len = GetClassName(hwnd, buf, buf.Length);
        return new string(buf, 0, len);
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    // Window classes and processes that indicate the taskbar should stay visible
    private static readonly HashSet<string> SystemOverlayClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "TopLevelWindowForOverflowXamlIsland",   // Tray overflow (Win11)
        "XamlExplorerHostIslandWindow",          // Task View
        "Windows.UI.Core.CoreWindow",            // Start menu, Action Center, Notifications
        "Windows.UI.Input.InputSite.WindowClass", // Touch keyboard area
        "Shell_InputSwitchTopLevelWindow",       // Input method switcher
    };

    private static readonly HashSet<string> SystemOverlayProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "StartMenuExperienceHost",    // Win11 Start menu
        "SearchHost",                 // Win10/11 Search
        "SearchApp",                  // Win11 23H2+ Search
        "ShellExperienceHost",        // Action Center, Widgets
        "SystemSettings",             // Quick Settings
        "TextInputHost",              // Emoji picker, touch keyboard
        "WidgetService",              // Widgets panel
        "Widgets",                    // Widgets panel
    };

    private static bool IsSystemOverlayWindow(string className, string processName)
    {
        if (SystemOverlayClasses.Contains(className)) return true;
        if (SystemOverlayProcesses.Contains(processName)) return true;
        return false;
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread?.Join(2000);

        if (_barrierHwnd != IntPtr.Zero)
        {
            DestroyWindow(_barrierHwnd);
            _barrierHwnd = IntPtr.Zero;
        }

        _wake.Dispose();
    }
}
