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

        // Saved straight away so the user's original auto-hide setting is captured before this app
        // ever changes it; re-reading it on later launches would pick up state left by a crash.
        var config = new Config { AutohideWhenDisabled = TaskbarManager.IsAutoHideOn() };
        config.Save();
        return config;
    }
}
