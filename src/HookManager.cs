using System.Runtime.InteropServices;
using static ButteryTaskbar3.NativeMethods;

namespace ButteryTaskbar3;

internal sealed class HookManager : IDisposable
{
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private LowLevelProc? _keyboardProc;
    private LowLevelProc? _mouseProc;
    private readonly TaskbarManager _taskbar;
    private readonly Func<Config> _getConfig;
    private readonly Action _onToggle;

    public HookManager(TaskbarManager taskbar, Func<Config> getConfig, Action onToggle)
    {
        _taskbar = taskbar;
        _getConfig = getConfig;
        _onToggle = onToggle;
    }

    public void UpdateHooks(bool menuActive)
    {
        var config = _getConfig();
        bool wantKeyboard = (config.Enabled || config.ToggleShortcutEnabled) && !menuActive;
        bool wantMouse = config.Enabled && config.ScrollActivationEnabled && !menuActive;

        bool hasKeyboard = _keyboardHook != IntPtr.Zero;
        bool hasMouse = _mouseHook != IntPtr.Zero;

        if (hasKeyboard != wantKeyboard)
        {
            if (wantKeyboard)
            {
                _keyboardProc = KeyboardHookProc;
                _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc,
                    GetModuleHandle(null), 0);
            }
            else
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
                _keyboardProc = null;
            }
        }

        if (hasMouse != wantMouse)
        {
            if (wantMouse)
            {
                _mouseProc = MouseHookProc;
                _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc,
                    GetModuleHandle(null), 0);
            }
            else
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
                _mouseProc = null;
            }
        }
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            var config = _getConfig();

            if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                if (HandleKeyDown(info.vkCode, config))
                    return (IntPtr)1;
            }
            else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                if ((info.vkCode == VK_LWIN || info.vkCode == VK_RWIN) && config.Enabled)
                {
                    int otherKey = info.vkCode == VK_LWIN ? VK_RWIN : VK_LWIN;
                    _taskbar.IsWinKeyDown = (GetKeyState(otherKey) & 0x80) != 0;
                    _taskbar.ExtendVisibility(400);
                }
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private bool HandleKeyDown(uint vk, Config config)
    {
        switch (vk)
        {
            case VK_LWIN:
            case VK_RWIN:
                if (config.Enabled && !_taskbar.IsWinKeyDown)
                    _taskbar.IsWinKeyDown = true;
                break;

            case VK_F11:
                bool ctrl = (GetKeyState(VK_CONTROL) & 0x80) != 0;
                bool win = (GetKeyState(VK_LWIN) & 0x80) != 0 || (GetKeyState(VK_RWIN) & 0x80) != 0;
                if (ctrl && win && config.ToggleShortcutEnabled)
                {
                    _onToggle();
                    return true;
                }
                break;
        }
        return false;
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WM_MOUSEWHEEL)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int delta = (short)(info.mouseData >> 16);
            if (delta != 0 && HandleMouseScroll(info.pt.X, info.pt.Y))
                return (IntPtr)1;
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private bool HandleMouseScroll(int mouseX, int mouseY)
    {
        var rect = _taskbar.PrimaryMonitorFull;
        // Check if cursor is at the bottom edge of the primary monitor (full bounds, not work area)
        if (mouseY >= rect.Bottom - 2
            && mouseX >= rect.Left
            && mouseX < rect.Right
            && !_taskbar.ShouldShowDueToFocus)
        {
            _taskbar.ShouldShowDueToFocus = true;
            _taskbar.SendWinKeyPress = true;
            _taskbar.SignalUpdate();
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }
}
