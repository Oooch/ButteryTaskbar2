using System.Runtime.InteropServices;
using System.Text.Json;

namespace ButteryTaskbar3;

internal sealed class Config
{
    public bool Enabled { get; set; } = true;
    public bool ScrollActivationEnabled { get; set; } = true;
    public bool ToggleShortcutEnabled { get; set; }
    public bool AutohideWhenDisabled { get; set; } = true;

    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ButteryTaskbar3");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }

    public static Config Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<Config>(json) ?? new Config();
            }
        }
        catch { }

        var config = new Config();
        // Check if Windows auto-hide is already enabled
        var abd = new NativeMethods.APPBARDATA { cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>() };
        var state = NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref abd);
        config.AutohideWhenDisabled = (state & NativeMethods.ABS_AUTOHIDE) != 0;
        return config;
    }
}
