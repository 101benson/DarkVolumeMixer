using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

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
        public bool AutoAdjustWidth { get; set; } = true;

        // Kompakte Listenansicht (waagerechte Fader) statt senkrechter Kacheln
        public bool ListLayout { get; set; } = false;

        // --- VERHALTEN ---
        public bool IsAlwaysOnTop { get; set; } = false;
    }

    public static class AppSettings
    {
        private static readonly string DirPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DarkVolumeMixer");

        private static readonly string FilePath = Path.Combine(DirPath, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static readonly object StateLock = new();
        private static readonly object WriteLock = new();
        private static System.Threading.Timer? _saveTimer;
        private static string? _pendingJson;

        public static AppSettingsData Current { get; private set; } = new AppSettingsData();

        static AppSettings()
        {
            Load();
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;

                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettingsData>(json);
                if (loaded == null) return;

                loaded.SessionOrder ??= new List<string>();
                loaded.PinnedSessionIds ??= new List<string>();
                Current = loaded;
            }
            catch (JsonException)
            {
                // Kaputte Datei nicht stillschweigend überschreiben, sondern als .bak aufheben.
                try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { }
            }
            catch { }
        }

        /// <summary>
        /// Merkt den aktuellen Stand und schreibt ihn leicht verzögert (gebündelt) auf die Platte.
        /// Serialisiert wird sofort im aufrufenden Thread, geschrieben im Hintergrund –
        /// der UI-Thread wartet nie auf die Festplatte.
        /// </summary>
        public static void Save()
        {
            string json;
            try { json = JsonSerializer.Serialize(Current, JsonOptions); }
            catch { return; }

            lock (StateLock)
            {
                _pendingJson = json;
                _saveTimer ??= new System.Threading.Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
                _saveTimer.Change(500, Timeout.Infinite);
            }
        }

        /// <summary>Sofort und synchron speichern (z. B. beim Beenden).</summary>
        public static void SaveNow()
        {
            try
            {
                string json = JsonSerializer.Serialize(Current, JsonOptions);
                lock (StateLock) { _pendingJson = json; }
            }
            catch { }

            Flush();
        }

        private static void Flush()
        {
            string? json;
            lock (StateLock)
            {
                json = _pendingJson;
                _pendingJson = null;
            }
            if (json == null) return;

            lock (WriteLock)
            {
                try
                {
                    Directory.CreateDirectory(DirPath);
                    string tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, FilePath, overwrite: true);   // atomar: nie eine halb geschriebene settings.json
                }
                catch { }
            }
        }
    }
}
