using System.Diagnostics;

namespace ButteryTaskbar3;

internal static class Log
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ButteryTaskbar3", "debug.log");

    private static readonly object Gate = new();

    [Conditional("DEBUG")]
    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
    }
}
