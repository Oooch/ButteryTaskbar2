using static ButteryTaskbar3.NativeMethods;

namespace ButteryTaskbar3;

internal static class Program
{
    private const string AppName = "Buttery Taskbar 3";
    private static readonly string AppVersion = Application.ProductVersion.Split('+')[0];
    private const string MutexName = "ButteryTaskbar3_SingleInstance";
    private const string MessageWindowTitle = "ButteryTaskbar3.MessageWindow";
    private const uint WM_TOGGLE_ENABLED = WM_APP + 1;
    private const uint WM_SHOW_CURSOR = WM_APP + 2;

    private static Config _config = null!;
    private static TaskbarManager _taskbar = null!;
    private static HookManager _hooks = null!;
    private static MessageWindow _messageWindow = null!;
    private static CursorHider _cursorHider = null!;
    private static NotifyIcon _trayIcon = null!;
    private static WinEventProc? _foregroundProc;
    private static IntPtr _foregroundHook;
    private static bool _shutDown;

    private static ToolStripMenuItem _enabledItem = null!;
    private static ToolStripMenuItem _shortcutItem = null!;
    private static ToolStripMenuItem _scrollItem = null!;
    private static ToolStripMenuItem _autoHideItem = null!;
    private static ToolStripMenuItem _startAtLoginItem = null!;
    private static ToolStripMenuItem _tooltipsItem = null!;
    private static ToolStripMenuItem _cursorItem = null!;
    private static bool _startAtLogin;

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            var running = FindWindow(null, MessageWindowTitle);
            if (running != IntPtr.Zero) PostMessage(running, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        bool createdNew = false;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(true, MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // An elevated copy (started by the log-in task) owns the mutex.
        }
        using var ownedMutex = mutex;
        if (!createdNew)
        {
            MessageBox.Show($"{AppName} is already running.", AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // A crash must never leave the taskbar hidden.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Write($"UI thread exception: {e.Exception}");
            Application.Exit();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write($"Unhandled exception: {e.ExceptionObject}");
            RestoreTaskbar();
            if (_config?.HideIdleCursor == true) CursorHider.RestoreSystemCursors();
        };

        _config = Config.Load();
        _taskbar = new TaskbarManager();
        _hooks = new HookManager(_taskbar, () => _config,
            () => PostMessage(_messageWindow.Handle, WM_TOGGLE_ENABLED, IntPtr.Zero, IntPtr.Zero),
            () => _cursorHider.OnMouseMoved());
        _messageWindow = new MessageWindow();
        _cursorHider = new CursorHider(() => _config,
            () => PostMessage(_messageWindow.Handle, WM_SHOW_CURSOR, IntPtr.Zero, IntPtr.Zero));

        // If a previous run was force-killed while the cursor was hidden, it is still blank.
        if (_config.HideIdleCursor) CursorHider.RestoreSystemCursors();

        SetupTrayIcon();

        // EVENT_SYSTEM_FOREGROUND fires for the Windows 11 Start menu; the old RegisterShellHookWindow approach did not catch it reliably.
        _foregroundProc = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        Log.Write($"Started {AppVersion}, enabled={_config.Enabled}");
        ApplySettings();
        _taskbar.Start();
        RefreshStartAtLogin();

        try
        {
            Application.Run();
        }
        finally
        {
            Shutdown();
        }
    }

    private static void ApplySettings()
    {
        TaskbarManager.SetAutoHide(_config.Enabled || _config.AutohideWhenDisabled);
        if (_config.HideTaskbarTooltips) TaskbarTooltips.SetEnabled(false);
        _hooks.UpdateHooks();
        _cursorHider.Update();
        _taskbar.ShowForFocus = !_config.Enabled || TaskbarManager.IsShellUi(GetForegroundWindow());
    }

    private static void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (!_config.Enabled) return;
        bool show = TaskbarManager.IsShellUi(hwnd);
        Log.Write($"Foreground: {TaskbarManager.GetWindowClassName(hwnd)} ({TaskbarManager.GetProcessName(hwnd)}) -> show={show}");
        _taskbar.ShowForFocus = show;
    }

    // schtasks runs off the UI thread: blocking it would stall the low-level hooks and lag all input.
    private static async void RefreshStartAtLogin()
    {
        _startAtLogin = await Task.Run(StartupTask.IsEnabled);
    }

    private static async void ToggleStartAtLogin()
    {
        _startAtLoginItem.Enabled = false;
        bool enable = !_startAtLogin;
        await Task.Run(() => enable ? StartupTask.Enable() : StartupTask.Disable());
        _startAtLogin = await Task.Run(StartupTask.IsEnabled);
        _startAtLoginItem.Enabled = true;
    }

    private static void ToggleEnabled()
    {
        _config.Enabled = !_config.Enabled;
        _config.Save();
        ApplySettings();
    }

    private static void Shutdown()
    {
        if (_shutDown) return;
        _shutDown = true;
        Log.Write("Shutting down");

        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        _hooks.Dispose();
        _cursorHider.Dispose();
        _taskbar.Stop();
        RestoreTaskbar();
        if (_config.HideTaskbarTooltips) TaskbarTooltips.SetEnabled(true);

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _messageWindow.DestroyHandle();
    }

    private static void RestoreTaskbar()
    {
        TaskbarManager.ShowAllTaskbars();
        TaskbarManager.SetAutoHide(_config?.AutohideWhenDisabled ?? false);
    }

    private static Icon CreateTrayIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var butterBrush = new SolidBrush(Color.FromArgb(255, 199, 44));
        using var butterPath = new System.Drawing.Drawing2D.GraphicsPath();
        var rect = new Rectangle(2, 6, 28, 18);
        const int d = 10;
        butterPath.AddArc(rect.X, rect.Y, d, d, 180, 90);
        butterPath.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        butterPath.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        butterPath.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        butterPath.CloseFigure();
        g.FillPath(butterBrush, butterPath);

        using var shineBrush = new SolidBrush(Color.FromArgb(80, 255, 255, 255));
        g.FillRectangle(shineBrush, 5, 8, 12, 6);

        using var barBrush = new SolidBrush(Color.FromArgb(200, 60, 60, 60));
        g.FillRectangle(barBrush, 1, 27, 30, 4);

        return Icon.FromHandle(bmp.GetHicon());
    }

    private static void SetupTrayIcon()
    {
        var menu = new ContextMenuStrip();

        var title = new ToolStripLabel($"{AppName} {AppVersion}");
        title.Font = new Font(title.Font, FontStyle.Bold);
        menu.Items.Add(title);
        menu.Items.Add(new ToolStripSeparator());

        _enabledItem = new ToolStripMenuItem("Enabled", null, (_, _) => ToggleEnabled());
        _shortcutItem = new ToolStripMenuItem("Ctrl+Win+F11 to toggle", null, (_, _) =>
        {
            _config.ToggleShortcutEnabled = !_config.ToggleShortcutEnabled;
            _config.Save();
            _hooks.UpdateHooks();
        });
        _scrollItem = new ToolStripMenuItem("Scroll to reveal taskbar", null, (_, _) =>
        {
            _config.ScrollActivationEnabled = !_config.ScrollActivationEnabled;
            _config.Save();
            _hooks.UpdateHooks();
        });
        _autoHideItem = new ToolStripMenuItem("Auto-hide when disabled", null, (_, _) =>
        {
            _config.AutohideWhenDisabled = !_config.AutohideWhenDisabled;
            _config.Save();
            ApplySettings();
        });
        _tooltipsItem = new ToolStripMenuItem("Hide taskbar tooltips", null, (_, _) =>
        {
            _config.HideTaskbarTooltips = !_config.HideTaskbarTooltips;
            _config.Save();
            TaskbarTooltips.SetEnabled(!_config.HideTaskbarTooltips);
        });
        _cursorItem = new ToolStripMenuItem("Hide mouse cursor when idle", null, (_, _) =>
        {
            _config.HideIdleCursor = !_config.HideIdleCursor;
            _config.Save();
            _hooks.UpdateHooks();
            _cursorHider.Update();
        });
        _startAtLoginItem = new ToolStripMenuItem("Start at log-in", null, (_, _) => ToggleStartAtLogin());
        menu.Items.AddRange([_enabledItem, _shortcutItem, _scrollItem, _autoHideItem, _tooltipsItem, _cursorItem,
            _startAtLoginItem, new ToolStripSeparator()]);
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Application.Exit()));

        menu.Opening += (_, _) =>
        {
            _enabledItem.Checked = _config.Enabled;
            _shortcutItem.Checked = _config.ToggleShortcutEnabled;
            _scrollItem.Checked = _config.ScrollActivationEnabled;
            _autoHideItem.Checked = _config.AutohideWhenDisabled;
            _tooltipsItem.Checked = _config.HideTaskbarTooltips;
            _cursorItem.Checked = _config.HideIdleCursor;
            _startAtLoginItem.Checked = _startAtLogin;
        };

        _trayIcon = new NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Text = AppName,
            Visible = true,
            ContextMenuStrip = menu,
        };

        // NotifyIcon only opens its menu on right-click; reuse its internal method for left-click too.
        var showContextMenu = typeof(NotifyIcon).GetMethod("ShowContextMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) showContextMenu?.Invoke(_trayIcon, null);
        };
    }

    // Hidden top-level window: receives WM_CLOSE from `--quit`, TaskbarCreated when Explorer
    // restarts (only broadcast to top-level windows), and toggle requests posted from the keyboard hook.
    private sealed class MessageWindow : NativeWindow
    {
        private static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

        public MessageWindow()
        {
            CreateHandle(new CreateParams { Caption = MessageWindowTitle });
            // When running elevated, Windows drops messages from normal-privilege processes
            // (Explorer, `--quit`) unless they are explicitly allowed.
            ChangeWindowMessageFilterEx(Handle, WM_CLOSE, MSGFLT_ALLOW, IntPtr.Zero);
            ChangeWindowMessageFilterEx(Handle, TaskbarCreated, MSGFLT_ALLOW, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLOSE)
            {
                Log.Write("Quit requested");
                Application.Exit();
                return;
            }
            if (m.Msg == WM_TOGGLE_ENABLED)
            {
                ToggleEnabled();
                return;
            }
            if (m.Msg == WM_SHOW_CURSOR)
            {
                _cursorHider.Show();
                return;
            }
            if (m.Msg == TaskbarCreated)
            {
                Log.Write("Explorer restarted");
                // NotifyIcon's own window misses this message when elevated, so re-add the icon here.
                _trayIcon.Visible = false;
                _trayIcon.Visible = true;
                ApplySettings();
                _taskbar.Wake();
            }
            base.WndProc(ref m);
        }
    }
}
