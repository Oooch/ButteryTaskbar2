using System.Runtime.InteropServices;
using static ButteryTaskbar3.NativeMethods;

namespace ButteryTaskbar3;

// Low-level hook callbacks must return quickly or Windows silently removes the hook,
// so anything slow (saving config, talking to Explorer) is posted back to the UI thread.
internal sealed class HookManager : IDisposable
{
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;
    private readonly TaskbarManager _taskbar;
    private readonly Func<Config> _getConfig;
    private readonly Action _requestToggle;

    public HookManager(TaskbarManager taskbar, Func<Config> getConfig, Action requestToggle)
    {
        _taskbar = taskbar;
        _getConfig = getConfig;
        _requestToggle = requestToggle;
        _keyboardProc = KeyboardHookProc;
        _mouseProc = MouseHookProc;
    }

    public void UpdateHooks()
    {
        var config = _getConfig();
        SetHook(ref _keyboardHook, WH_KEYBOARD_LL, _keyboardProc, config.Enabled || config.ToggleShortcutEnabled);
        SetHook(ref _mouseHook, WH_MOUSE_LL, _mouseProc, config.Enabled && config.ScrollActivationEnabled);
    }

    private static void SetHook(ref IntPtr hook, int type, LowLevelProc proc, bool wanted)
    {
        if (wanted && hook == IntPtr.Zero)
        {
            hook = SetWindowsHookEx(type, proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
                Log.Write($"SetWindowsHookEx({type}) failed: {Marshal.GetLastWin32Error()}");
        }
        else if (!wanted && hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }

    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            var config = _getConfig();
            bool isWin = info.vkCode is VK_LWIN or VK_RWIN;

            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                if (isWin && config.Enabled && !_taskbar.WinKeyDown)
                {
                    Log.Write("Win down");
                    _taskbar.WinKeyDown = true;
                }
                else if (info.vkCode == VK_F11 && config.ToggleShortcutEnabled
                         && IsKeyDown(VK_CONTROL) && (IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN)))
                {
                    _requestToggle();
                    return 1;
                }
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP && isWin && _taskbar.WinKeyDown)
            {
                // Not gated on Enabled, so toggling off while Win is held can't leave the flag stuck on.
                int otherWin = info.vkCode == VK_LWIN ? VK_RWIN : VK_LWIN;
                Log.Write("Win up");
                _taskbar.WinKeyDown = IsKeyDown(otherWin);
                _taskbar.KeepVisibleFor(400);
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WM_MOUSEWHEEL)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var bounds = Screen.PrimaryScreen!.Bounds;
            bool atBottomEdge = info.pt.Y >= bounds.Bottom - 2
                && info.pt.X >= bounds.Left && info.pt.X < bounds.Right;

            if (atBottomEdge && !_taskbar.ShowForFocus)
            {
                Log.Write("Scroll at bottom edge: opening Start");
                _taskbar.ShowForFocus = true;
                _taskbar.RequestOpenStart();
                return 1;
            }
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHook = IntPtr.Zero;
        _mouseHook = IntPtr.Zero;
    }
}
