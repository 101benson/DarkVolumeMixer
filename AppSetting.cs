using System;
using System.IO;
using System.Text.Json;

namespace DarkVolumeMixer
{
    public class AppSettingsData
    {
        // --- DISCORD LIMITER EINSTELLUNGEN ---
        public bool AutoVolumeDiscord { get; set; } = true;
        public float DiscordVolumeValue { get; set; } = 20f;

        public List<string> SessionOrder { get; set; } = new List<string>();
        public List<string> PinnedSessionIds { get; set; } = new List<string>();

        // --- FENSTER-ABMESSUNGEN & POSITION ---
        public double WindowWidth { get; set; } = 390;
        public double WindowHeight { get; set; } = 390;
        public double? WindowX { get; set; } = null;
        public double? WindowY { get; set; } = null;
        
        public bool ProportionalMaster { get; set; } = true;

        public bool AutoAdjustWidth { get; set; } = true; // <-- NEU: Automatische Breitenanpassung

        // --- VERHALTEN ---
        public bool IsAlwaysOnTop { get; set; } = false;
    }

    public static class AppSettings
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DarkVolumeMixer",
            "settings.json");

        public static AppSettingsData Current { get; private set; } = new AppSettingsData();

        static AppSettings()
        {
            Load();
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettingsData>(json);
                    if (loaded != null) Current = loaded;
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }
    }
}