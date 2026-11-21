using static ButteryTaskbar3.NativeMethods;

namespace ButteryTaskbar3;

// Hides the cursor in every app by replacing each system cursor with a blank one.
// SPI_SETCURSORS reloads the user's cursor scheme from the registry to bring it back.
internal sealed class CursorHider : IDisposable
{
    private static readonly uint[] SystemCursorIds =
    [
        32512, // normal arrow
        32513, // text I-beam
        32514, // busy
        32515, // precision cross
        32516, // up arrow
        32642, 32643, 32644, 32645, 32646, // resize
        32648, // unavailable
        32649, // link hand
        32650, // working in background
        32651, // help
        32671, // location pin
        32672, // person
    ];

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Func<Config> _getConfig;
    private readonly Action _requestShow;
    private long _lastMoveTick = Environment.TickCount64;
    private volatile bool _hidden;
    private int _showRequested;

    public CursorHider(Func<Config> getConfig, Action requestShow)
    {
        _getConfig = getConfig;
        _requestShow = requestShow;
        _timer.Tick += (_, _) => HideIfIdle();
    }

    public void Update()
    {
        var config = _getConfig();
        Interlocked.Exchange(ref _lastMoveTick, Environment.TickCount64);
        _timer.Enabled = config.HideIdleCursor;
        if (!config.HideIdleCursor) Show();
    }

    // Runs inside the low-level mouse hook on every move, so it must stay cheap; restoring the
    // cursor scheme reads from disk, so that is posted to the UI thread instead.
    public void OnMouseMoved()
    {
        Interlocked.Exchange(ref _lastMoveTick, Environment.TickCount64);
        if (_hidden && Interlocked.Exchange(ref _showRequested, 1) == 0)
            _requestShow();
    }

    private void HideIfIdle()
    {
        if (_hidden) return;
        long idleMs = Environment.TickCount64 - Interlocked.Read(ref _lastMoveTick);
        if (idleMs < _getConfig().HideCursorAfterSeconds * 1000L) return;

        var andMask = Enumerable.Repeat((byte)0xFF, 32 * 32 / 8).ToArray();
        var xorMask = new byte[32 * 32 / 8];
        var blank = CreateCursor(GetModuleHandle(null), 0, 0, 32, 32, andMask, xorMask);
        foreach (var id in SystemCursorIds)
            SetSystemCursor(CopyIcon(blank), id); // takes ownership of the copy
        DestroyCursor(blank);

        _hidden = true;
        Log.Write("Cursor hidden");
    }

    public void Show()
    {
        Interlocked.Exchange(ref _showRequested, 0);
        if (!_hidden) return;
        RestoreSystemCursors();
        _hidden = false;
        Log.Write("Cursor shown");
    }

    public static void RestoreSystemCursors() => SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);

    public void Dispose()
    {
        _timer.Dispose();
        Show();
    }
}
