using System.Runtime.InteropServices;
using static ButteryTaskbar3.NativeMethods;

namespace ButteryTaskbar3;

internal static class Program
{
    private const string AppName = "Buttery Taskbar 3";
    private const string AppVersion = "3.0.0";
    private const string MutexName = "ButteryTaskbar3_SingleInstance";

    private static Config _config = null!;
    private static TaskbarManager _taskbar = null!;
    private static HookManager _hooks = null!;
    private static NotifyIcon _trayIcon = null!;
    private static IntPtr _shellHookHwnd;
    private static uint _shellHookMsg;
    private static WndProc? _wndProc; // prevent GC collection
    private static bool _originalAutoHideState;

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show($"{AppName} is already running.", AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        _config = Config.Load();

        // Remember the original auto-hide state so we can restore it on exit
        var abd = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        _originalAutoHideState = (SHAppBarMessage(ABM_GETSTATE, ref abd) & ABS_AUTOHIDE) != 0;

        _taskbar = new TaskbarManager();
        _hooks = new HookManager(_taskbar, () => _config, ToggleEnabled);

        SetupTrayIcon();
        SetupShellHookWindow();

        _taskbar.SetAutoHideState(_config.Enabled, _config.AutohideWhenDisabled);
        _hooks.UpdateHooks(false);
        _taskbar.Start();

        // Initial state
        if (_config.Enabled)
        {
            _taskbar.RefreshTaskbarState();
            _taskbar.SignalUpdate();
        }

        Application.ApplicationExit += (_, _) => Shutdown();
        Application.Run();
    }

    private static void Shutdown()
    {
        _hooks.Dispose();

        // Forcefully restore taskbar before anything else
        RestoreTaskbarDirect();

        _taskbar.ShouldShowDueToFocus = true;
        _taskbar.SignalUpdate();
        Thread.Sleep(300);
        _taskbar.Dispose();

        // Restore again after TaskbarManager is done, in case it reverted
        RestoreTaskbarDirect();

        if (_shellHookHwnd != IntPtr.Zero)
        {
            DeregisterShellHookWindow(_shellHookHwnd);
            DestroyWindow(_shellHookHwnd);
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }

    private static void RestoreTaskbarDirect()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero)
        {
            ShowWindow(taskbar, SW_SHOWNOACTIVATE);
            SetWindowPos(taskbar, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        // Also show secondary taskbars
        IntPtr secondary = IntPtr.Zero;
        while (true)
        {
            secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null);
            if (secondary == IntPtr.Zero) break;
            ShowWindow(secondary, SW_SHOWNOACTIVATE);
            SetWindowPos(secondary, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        // Restore auto-hide to whatever it was before the app started
        var abd = new APPBARDATA
        {
            cbSize = Marshal.SizeOf<APPBARDATA>(),
            hWnd = taskbar,
            lParam = _originalAutoHideState ? (IntPtr)ABS_AUTOHIDE : IntPtr.Zero,
        };
        SHAppBarMessage(ABM_SETSTATE, ref abd);
    }

    private static Icon CreateTrayIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // Butter yellow rounded rectangle
        using var butterBrush = new SolidBrush(Color.FromArgb(255, 199, 44));
        using var butterPath = new System.Drawing.Drawing2D.GraphicsPath();
        var butterRect = new Rectangle(2, 6, 28, 18);
        int radius = 5;
        butterPath.AddArc(butterRect.X, butterRect.Y, radius * 2, radius * 2, 180, 90);
        butterPath.AddArc(butterRect.Right - radius * 2, butterRect.Y, radius * 2, radius * 2, 270, 90);
        butterPath.AddArc(butterRect.Right - radius * 2, butterRect.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        butterPath.AddArc(butterRect.X, butterRect.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        butterPath.CloseFigure();
        g.FillPath(butterBrush, butterPath);

        // Shine highlight
        using var shineBrush = new SolidBrush(Color.FromArgb(80, 255, 255, 255));
        g.FillRectangle(shineBrush, 5, 8, 12, 6);

        // Taskbar line at the bottom
        using var barBrush = new SolidBrush(Color.FromArgb(200, 60, 60, 60));
        g.FillRectangle(barBrush, 1, 27, 30, 4);

        return Icon.FromHandle(bmp.GetHicon());
    }

    private static void SetupTrayIcon()
    {
        var contextMenu = new ContextMenuStrip();
        UpdateContextMenu(contextMenu);

        _trayIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = AppName,
            Visible = true,
            ContextMenuStrip = contextMenu,
        };

        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                UpdateContextMenu(_trayIcon.ContextMenuStrip!);
                var mi = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                mi?.Invoke(_trayIcon, null);
            }
        };

        contextMenu.Opening += (_, _) => UpdateContextMenu(contextMenu);
    }

    private static void UpdateContextMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();

        var titleItem = new ToolStripLabel($"{AppName} {AppVersion}")
        {
            Font = new Font(menu.Font, FontStyle.Bold),
        };
        menu.Items.Add(titleItem);
        menu.Items.Add(new ToolStripSeparator());

        var enabledItem = new ToolStripMenuItem(_config.Enabled ? "Enabled" : "Enable")
        {
            Checked = _config.Enabled,
        };
        enabledItem.Click += (_, _) => ToggleEnabled();
        menu.Items.Add(enabledItem);

        var shortcutItem = new ToolStripMenuItem("Ctrl+Win+F11 to toggle")
        {
            Checked = _config.ToggleShortcutEnabled,
        };
        shortcutItem.Click += (_, _) =>
        {
            _config.ToggleShortcutEnabled = !_config.ToggleShortcutEnabled;
            _hooks.UpdateHooks(false);
            _config.Save();
        };
        menu.Items.Add(shortcutItem);

        var scrollItem = new ToolStripMenuItem("Scroll to reveal taskbar")
        {
            Checked = _config.ScrollActivationEnabled,
        };
        scrollItem.Click += (_, _) =>
        {
            _config.ScrollActivationEnabled = !_config.ScrollActivationEnabled;
            _hooks.UpdateHooks(false);
            _config.Save();
        };
        menu.Items.Add(scrollItem);

        var autoHideItem = new ToolStripMenuItem("Auto-hide when disabled")
        {
            Checked = _config.AutohideWhenDisabled,
        };
        autoHideItem.Click += (_, _) =>
        {
            _config.AutohideWhenDisabled = !_config.AutohideWhenDisabled;
            _taskbar.SetAutoHideState(_config.Enabled, _config.AutohideWhenDisabled);
            _taskbar.SignalUpdate();
            _config.Save();
        };
        menu.Items.Add(autoHideItem);

        menu.Items.Add(new ToolStripSeparator());

        var quitItem = new ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => Application.Exit();
        menu.Items.Add(quitItem);
    }

    private static void ToggleEnabled()
    {
        _config.Enabled = !_config.Enabled;

        if (_config.Enabled)
        {
            _taskbar.RefreshTaskbarState();
        }
        else
        {
            _taskbar.ShouldShowDueToFocus = true;
        }

        _taskbar.SetAutoHideState(_config.Enabled, _config.AutohideWhenDisabled);
        _taskbar.SignalUpdate();
        _hooks.UpdateHooks(false);
        _config.Save();
    }

    private static void SetupShellHookWindow()
    {
        _shellHookMsg = RegisterWindowMessage("SHELLHOOK");

        _wndProc = ShellHookWndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = "ButteryTaskbar3_ShellHook",
            hCursor = LoadCursor(IntPtr.Zero, 32512),
        };
        RegisterClassEx(ref wc);

        _shellHookHwnd = CreateWindowEx(
            0, "ButteryTaskbar3_ShellHook", AppName, 0,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero,
            GetModuleHandle(null), IntPtr.Zero);

        if (_shellHookHwnd != IntPtr.Zero)
        {
            RegisterShellHookWindow(_shellHookHwnd);
        }
    }

    private static IntPtr ShellHookWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _shellHookMsg && _config.Enabled)
        {
            int code = wParam.ToInt32();

            if (code == HSHELL_WINDOWACTIVATED)
            {
                _taskbar.RefreshTaskbarState();
                _taskbar.SignalUpdate();
            }
            else if (code == HSHELL_RUDEAPPACTIVATED)
            {
                var activatedWindow = lParam;

                if (activatedWindow == IntPtr.Zero)
                {
                    activatedWindow = GetForegroundWindow();
                    if (activatedWindow != IntPtr.Zero)
                    {
                        var className = TaskbarManager.GetWindowClassName(activatedWindow);
                        if (className is "Shell_TrayWnd"
                            or "Shell_SecondaryTrayWnd"
                            or "TopLevelWindowForOverflowXamlIsland"
                            or "XamlExplorerHostIslandWindow")
                        {
                            _taskbar.ShouldShowDueToFocus = true;
                        }
                        else
                        {
                            _taskbar.ShouldShowDueToFocus = false;
                        }
                    }
                }
                else
                {
                    _taskbar.RefreshTaskbarState();
                }

                _taskbar.SignalUpdate();
            }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
