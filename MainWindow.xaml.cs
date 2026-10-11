using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DarkVolumeMixer
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        // ------------------------------------------------------------------ Zustand

        public ObservableCollection<AudioSessionModel> Sessions { get; } = new();
        public ObservableCollection<MMDevice> Devices { get; } = new();

        private readonly DispatcherTimer _uiRenderTimer;
        private readonly DispatcherTimer _autoScanTimer;
        private readonly DispatcherTimer _debounceScanTimer;
        private readonly DispatcherTimer _masterResyncTimer;

        private readonly CoalescingWriter<float> _masterWriter;
        private readonly CoalescingWriter<bool> _masterMuteWriter;

        private CancellationTokenSource? _bgPeakCts;
        private MMDevice? _selectedDevice;

        // Thread-sicherer Schnappschuss für den Hintergrund-Worker (ObservableCollection ist es nicht)
        private volatile AudioSessionModel[] _sessionSnapshot = Array.Empty<AudioSessionModel>();

        private ImageSource? _systemIcon;
        private ImageSource? _razerIcon;
        private ImageSource? _defaultAppIcon;
        private bool _iconsLoaded;
        private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

        private volatile float _masterVolume;
        private volatile bool _isMasterMuted;
        private float _masterPeak;
        private volatile float _rawMasterPeak;
        private bool _isAlwaysOnTop;
        private bool _isListLayout;
        private int _masterWriteSeq;        // zählt Nutzer-Eingaben am Master (veraltete Lesewerte erkennen)
        private float _masterTarget;        // zuletzt vom Nutzer gewünschter Master-Wert
        private int _masterResyncRetries;
        private double _cardModeWidth;
        private int _scanLock;
        private long _lastMasterWriteTick;

        private volatile bool _uiActive = true;
        private volatile bool _forceExternalPoll;
        private bool _isShuttingDown;

        // Basis für die proportionale Master-Regelung (siehe ScaleSessionsProportionally)
        private Dictionary<AudioSessionModel, float>? _propBase;
        private float _propBaseMaster;

        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private System.Windows.Point _dragStartPoint;
        private AudioSessionModel? _draggedData;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon, IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, System.Text.StringBuilder lpExeName, ref int pdwSize);

        // ------------------------------------------------------------------ Start

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            Sessions.CollectionChanged += (_, _) => _sessionSnapshot = Sessions.ToArray();

            _isAlwaysOnTop = AppSettings.Current.IsAlwaysOnTop;
            Topmost = _isAlwaysOnTop;
            ApplySavedWindowBounds();
            _isListLayout = AppSettings.Current.ListLayout;
            ApplyLayoutMode(initial: true);

            _masterWriter = new CoalescingWriter<float>(WriteMasterVolume);
            _masterMuteWriter = new CoalescingWriter<bool>(WriteMasterMute);

            // Alle Timer VOR LoadDevices anlegen (LoadDevices stößt bereits einen Scan an)
            _debounceScanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _debounceScanTimer.Tick += (s, e) => { _debounceScanTimer.Stop(); StartScan(); };

            _autoScanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _autoScanTimer.Tick += (s, e) => StartScan();

            _uiRenderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
            _uiRenderTimer.Tick += UiRenderTimer_Tick;

            _masterResyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _masterResyncTimer.Tick += MasterResyncTimer_Tick;

            LoadDevices();
            SetupTrayIcon();

            StateChanged += (_, _) => UpdateUiActive();
            IsVisibleChanged += (_, _) => UpdateUiActive();

            _autoScanTimer.Start();
            _uiRenderTimer.Start();

            StartScan();
            StartBackgroundAudioWorker();
        }

        private void ApplySavedWindowBounds()
        {
            var s = AppSettings.Current;
            if (s.WindowHeight > 0) Height = s.WindowHeight;
            if (s.WindowWidth > 0) Width = s.WindowWidth;

            if (s.WindowX.HasValue && s.WindowY.HasValue)
            {
                double x = s.WindowX.Value, y = s.WindowY.Value;
                double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
                double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;

                // Nur übernehmen, wenn das Fenster tatsächlich auf einem vorhandenen Bildschirm landet
                // (früher konnte es nach Minimieren/Monitorwechsel außerhalb des Desktops starten).
                bool visible = x + 100 > vl && x < vl + vw - 100 && y + 40 > vt && y < vt + vh - 40;
                if (visible)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = x;
                    Top = y;
                }
            }
        }

        private void UpdateUiActive()
        {
            bool active = IsVisible && WindowState != WindowState.Minimized;
            if (active && !_uiActive) _forceExternalPoll = true;
            _uiActive = active;
        }

        private void PostToUi(Action action) => Dispatcher.BeginInvoke(DispatcherPriority.Background, action);

        // ------------------------------------------------------------------ Hintergrund-Worker (Pegel)

        // Reine Messung im Hintergrund: nur Speicher, keine Dispatcher-Aufrufe.
        // Ist das Fenster minimiert/versteckt, ruht der Worker fast komplett (schont FPS im Spiel).
        private void StartBackgroundAudioWorker()
        {
            _bgPeakCts?.Cancel();
            _bgPeakCts = new CancellationTokenSource();
            var token = _bgPeakCts.Token;

            Task.Run(async () =>
            {
                int tick = 0;

                while (!token.IsCancellationRequested)
                {
                    bool active = _uiActive;

                    if (active)
                    {
                        try
                        {
                            PollMasterPeak();

                            var sessions = _sessionSnapshot;
                            foreach (var s in sessions) s.PollPeak();

                            tick++;
                            if (tick % 15 == 0 || _forceExternalPoll)   // ca. alle 450 ms
                            {
                                _forceExternalPoll = false;
                                foreach (var s in sessions) s.PollExternalState();
                            }
                        }
                        catch { }
                    }

                    try { await Task.Delay(active ? 30 : 500, token); }
                    catch (OperationCanceledException) { break; }
                }
            }, token);
        }

        private void PollMasterPeak()
        {
            var device = _selectedDevice;
            if (device == null) return;

            float raw = 0f;
            try { raw = device.AudioMeterInformation.MasterPeakValue; } catch { }

            if (_isMasterMuted) raw = 0f;

            // Balken reicht nie höher als der Master-Regler
            _rawMasterPeak = MeterMath.ToPercent(raw) * _masterVolume / 100f;
        }

        // UI holt sich die Werte ab (30 FPS)
        private void UiRenderTimer_Tick(object? sender, EventArgs e)
        {
            if (!_uiActive) return;

            float target = _rawMasterPeak;
            float next = target >= _masterPeak
                ? target
                : Math.Max(0f, _masterPeak - ((_masterPeak - target) * 0.45f) - 1.5f);

            if (Math.Abs(next - _masterPeak) > 1.0f || (next == 0f && _masterPeak != 0f))
            {
                _masterPeak = next;
                OnPropertyChanged(nameof(MasterPeak));
            }

            foreach (var session in Sessions) session.RenderTickPeak();
        }

        // ------------------------------------------------------------------ Master-Lautstärke

        private void MarkMasterWrite() => Interlocked.Exchange(ref _lastMasterWriteTick, Environment.TickCount64);

        private void WriteMasterVolume(float scalar)
        {
            var device = _selectedDevice;
            if (device != null)
            {
                try { device.AudioEndpointVolume.MasterVolumeLevelScalar = scalar; } catch { }
            }
            MarkMasterWrite();
        }

        private void WriteMasterMute(bool mute)
        {
            var device = _selectedDevice;
            if (device != null)
            {
                try { device.AudioEndpointVolume.Mute = mute; } catch { }
            }
            MarkMasterWrite();
        }

        private void ScheduleMasterResync()
        {
            _masterResyncTimer.Stop();
            _masterResyncTimer.Start();
        }

        // Nach dem Loslassen einmal nachschauen, was Windows WIRKLICH eingestellt hat
        private void MasterResyncTimer_Tick(object? sender, EventArgs e)
        {
            _masterResyncTimer.Stop();

            var device = _selectedDevice;
            if (device == null) return;

            int seq = Volatile.Read(ref _masterWriteSeq);

            Task.Run(() =>
            {
                float vol;
                bool mute;
                try
                {
                    var endpoint = device.AudioEndpointVolume;
                    vol = endpoint.MasterVolumeLevelScalar * 100f;
                    mute = endpoint.Mute;
                }
                catch { return; }

                PostToUi(() =>
                {
                    // Nutzer war inzwischen wieder am Regler → dieser Lesewert ist veraltet
                    if (seq != Volatile.Read(ref _masterWriteSeq)) return;
                    if (!ReferenceEquals(device, _selectedDevice)) return;

                    // Windows/Treiber hat den gewünschten Wert (noch) nicht übernommen, z. B. weil er den
                    // Pegel nur langsam nachzieht: bis zu zweimal erneut setzen, statt den Regler sofort
                    // auf den Zwischenwert zurückzuziehen.
                    if (Math.Abs(vol - _masterTarget) > 1.5f && _masterResyncRetries < 2 && !_isMasterMuted)
                    {
                        _masterResyncRetries++;
                        MarkMasterWrite();
                        _masterWriter.Post(_masterTarget / 100f);
                        ScheduleMasterResync();
                        return;
                    }

                    AdoptMasterState(device, vol, mute, 1.5f);
                });
            });
        }

        private void AdoptMasterState(MMDevice device, float vol, bool mute, float tolerance)
        {
            if (!ReferenceEquals(device, _selectedDevice)) return;
            if (_masterWriter.IsBusy || _masterMuteWriter.IsBusy) return;   // Nutzer ist gerade dran

            if (_isMasterMuted != mute)
            {
                _isMasterMuted = mute;
                OnPropertyChanged(nameof(IsMasterMuted));
                OnPropertyChanged(nameof(IsMasterEnabled));
                ApplyMasterMuteToSessions(mute);
            }

            if (Math.Abs(_masterVolume - vol) > tolerance)
            {
                _masterVolume = vol;
                _masterTarget = vol;
                InvalidateProportionalBaseline();
                OnPropertyChanged(nameof(MasterVolume));
            }
        }

        private void AudioEndpointVolume_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            float extVol = data.MasterVolume * 100f;
            bool extMute = data.Muted;
            var device = _selectedDevice;
            if (device == null) return;

            PostToUi(() =>
            {
                // Echos unserer eigenen Schreibzugriffe ignorieren – sie kommen verspätet und mit altem Wert zurück
                // und ließen den Master früher von selbst "wandern".
                if (Environment.TickCount64 - Interlocked.Read(ref _lastMasterWriteTick) < 2000) return;
                AdoptMasterState(device, extVol, extMute, 0.8f);
            });
        }

        public float MasterVolume
        {
            get => _masterVolume;
            set
            {
                float clamped = Math.Clamp(value, 0f, 100f);
                if (Math.Abs(_masterVolume - clamped) <= 0.05f) return;

                float oldMaster = _masterVolume;
                _masterVolume = clamped;
                _masterTarget = clamped;
                _masterResyncRetries = 0;
                Interlocked.Increment(ref _masterWriteSeq);
                OnPropertyChanged(nameof(MasterVolume));

                if (clamped > 0 && IsMasterMuted) IsMasterMuted = false;

                if (AppSettings.Current.ProportionalMaster) ScaleSessionsProportionally(oldMaster, clamped);

                MarkMasterWrite();
                _masterWriter.Post(clamped / 100f);
                ScheduleMasterResync();
            }
        }

        // Proportionale Regelung ohne Drift: Beim ersten Master-Schritt wird einmal festgehalten,
        // wie die Apps zum Master stehen. Danach gilt immer  App = Basis × (Master / Basis-Master).
        // Früher wurde jedes Mal multipliziert – das summierte Rundungs-/Klemm-Fehler auf,
        // und wer den Master auf 0 zog, hat die App-Pegel dauerhaft zerstört.
        private void ScaleSessionsProportionally(float oldMaster, float newMaster)
        {
            if (_propBase == null)
            {
                if (oldMaster < 1f) return;
                _propBaseMaster = oldMaster;
                _propBase = Sessions.Where(s => s.IsEnabled).ToDictionary(s => s, s => s.Volume);
            }

            float ratio = newMaster / _propBaseMaster;
            foreach (var kv in _propBase)
            {
                kv.Key.SetVolumeProgrammatic(Math.Clamp(kv.Value * ratio, 0f, 100f));
            }
        }

        private void InvalidateProportionalBaseline() => _propBase = null;

        public bool IsMasterEnabled => !IsMasterMuted;

        public bool IsMasterMuted
        {
            get => _isMasterMuted;
            set
            {
                if (_isMasterMuted == value) return;

                _isMasterMuted = value;
                Interlocked.Increment(ref _masterWriteSeq);
                MarkMasterWrite();
                _masterMuteWriter.Post(value);

                OnPropertyChanged(nameof(IsMasterMuted));
                OnPropertyChanged(nameof(IsMasterEnabled));
                ApplyMasterMuteToSessions(value);
                ScheduleMasterResync();
            }
        }

        private HashSet<AudioSessionModel>? _mutedBeforeMaster;

        // Master stumm -> alle Kacheln stumm (rot). Master wieder an -> nur die Kacheln bleiben stumm,
        // die schon VOR dem Master-Mute einzeln stumm waren und es noch sind.
        private void ApplyMasterMuteToSessions(bool masterMuted)
        {
            if (masterMuted)
            {
                _mutedBeforeMaster = Sessions.Where(x => x.IsMuted).ToHashSet();
                foreach (var session in Sessions.ToList()) session.IsMuted = true;
            }
            else
            {
                var keep = _mutedBeforeMaster;
                _mutedBeforeMaster = null;
                foreach (var session in Sessions.ToList())
                    session.IsMuted = keep != null && keep.Contains(session) && session.IsMuted;
            }
        }

        public float MasterPeak => _masterPeak;

        // ------------------------------------------------------------------ Geräte

        public MMDevice? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (ReferenceEquals(_selectedDevice, value)) return;

                DetachDeviceEvents(_selectedDevice);
                _selectedDevice = value;
                InvalidateProportionalBaseline();

                if (_selectedDevice != null)
                {
                    try
                    {
                        _masterVolume = _selectedDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
                        _masterTarget = _masterVolume;
                        _isMasterMuted = _selectedDevice.AudioEndpointVolume.Mute;
                        _selectedDevice.AudioEndpointVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;
                    }
                    catch { }

                    try
                    {
                        var manager = _selectedDevice.AudioSessionManager;
                        if (manager != null)
                        {
                            manager.RefreshSessions();
                            manager.OnSessionCreated += AudioSessionManager_OnSessionCreated;
                        }
                    }
                    catch { }
                }

                foreach (var s in Sessions) s.Dispose();
                Sessions.Clear();

                OnPropertyChanged(nameof(SelectedDevice));
                OnPropertyChanged(nameof(MasterVolume));
                OnPropertyChanged(nameof(IsMasterMuted));
                OnPropertyChanged(nameof(IsMasterEnabled));

                TriggerAudioSessionScan();
            }
        }

        private void DetachDeviceEvents(MMDevice? device)
        {
            if (device == null) return;
            try { device.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification; } catch { }
            try
            {
                var manager = device.AudioSessionManager;
                if (manager != null) manager.OnSessionCreated -= AudioSessionManager_OnSessionCreated;
            }
            catch { }
        }

        private void LoadDevices()
        {
            try
            {
                Devices.Clear();
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) Devices.Add(device);

                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                SelectedDevice = Devices.FirstOrDefault(d => d.ID == defaultDevice.ID) ?? Devices.FirstOrDefault();
            }
            catch { }
        }

        // ------------------------------------------------------------------ Session-Erkennung

        private void AudioSessionManager_OnSessionCreated(object? sender, IAudioSessionControl newSession) =>
            Dispatcher.InvokeAsync(TriggerAudioSessionScan);

        private void TriggerAudioSessionScan()
        {
            _debounceScanTimer.Stop();
            _debounceScanTimer.Start();
        }

        private void StartScan() => Task.Run(ExecuteLoadAudioSessionsAsync);

        private sealed record SessionInfo(AudioSessionControl Session, int Pid, string? ProcessName, DateTime StartTime, string InstanceId);
        private sealed record DetectedCard(List<AudioSessionControl> Sessions, ImageSource? Icon);

        private async Task ExecuteLoadAudioSessionsAsync()
        {
            if (Interlocked.Exchange(ref _scanLock, 1) == 1) return;

            try
            {
                var device = _selectedDevice;
                if (device == null) return;

                EnsureIconsLoaded();

                var detected = ScanSessions(device);
                if (detected == null) return;

                await Dispatcher.InvokeAsync(() => ApplyScanResult(device, detected));
            }
            catch { }
            finally { Interlocked.Exchange(ref _scanLock, 0); }
        }

        // Läuft im Hintergrund: jede Session wird nur EINMAL untersucht (früher mehrfach pro Scan).
        private Dictionary<string, DetectedCard>? ScanSessions(MMDevice device)
        {
            var manager = device.AudioSessionManager;
            if (manager == null) return null;

            try { manager.RefreshSessions(); } catch { }
            var sessions = manager.Sessions;
            if (sessions == null) return null;

            var infos = new List<SessionInfo>();
            int count = sessions.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var session = sessions[i];
                    if (session == null) continue;
                    var info = Inspect(session);
                    if (info != null) infos.Add(info);
                }
                catch { }
            }

            var named = new List<(SessionInfo Info, string Name)>();
            var discord = new List<SessionInfo>();

            foreach (var info in infos)
            {
                string name = GetAppName(info);
                if (string.IsNullOrEmpty(name)) continue;

                if (name.Equals("Discord", StringComparison.OrdinalIgnoreCase)) discord.Add(info);
                else named.Add((info, name));
            }

            // Discord deterministisch benennen: ältester Prozess zuerst. Früher hing die Zuordnung von der
            // Aufzählungsreihenfolge ab – "Discord" und "Discord (Voice)" konnten zwischen Scans die Plätze tauschen.
            int n = 0;
            foreach (var info in discord.OrderBy(d => d.StartTime).ThenBy(d => d.Pid).ThenBy(d => d.InstanceId, StringComparer.Ordinal))
            {
                n++;
                string name = n == 1 ? "Discord" : n == 2 ? "Discord (Voice)" : $"Discord ({n})";
                named.Add((info, name));
            }

            var result = new Dictionary<string, DetectedCard>(StringComparer.OrdinalIgnoreCase);
            foreach (var (info, name) in named)
            {
                if (!result.TryGetValue(name, out var card))
                {
                    card = new DetectedCard(new List<AudioSessionControl>(), GetAppIcon(info, name));
                    result[name] = card;
                }
                card.Sessions.Add(info.Session);
            }
            return result;
        }

        private static SessionInfo? Inspect(AudioSessionControl session)
        {
            try
            {
                if (session.State == AudioSessionState.AudioSessionStateExpired) return null;

                int pid = 0;
                try { pid = (int)session.GetProcessID; } catch { }

                string? processName = null;
                DateTime startTime = DateTime.MaxValue;

                if (pid > 0)
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        if (proc.HasExited) return null;
                        processName = proc.ProcessName;
                        try { startTime = proc.StartTime; } catch { }
                    }
                    catch (ArgumentException) { return null; }   // Prozess existiert nicht mehr
                    catch { }                                    // kein Zugriff: wir nehmen an, er lebt
                }

                string instanceId = string.Empty;
                try { instanceId = session.GetSessionInstanceIdentifier ?? string.Empty; } catch { }

                return new SessionInfo(session, pid, processName, startTime, instanceId);
            }
            catch { return null; }
        }

        private static string GetAppName(SessionInfo info)
        {
            string? procName = info.ProcessName;

            if (!string.IsNullOrEmpty(procName))
            {
                if (procName.Contains("razeraxon", StringComparison.OrdinalIgnoreCase) ||
                    procName.Contains("axon", StringComparison.OrdinalIgnoreCase))
                {
                    return "Razer Axon";
                }
                if (procName.Contains("discord", StringComparison.OrdinalIgnoreCase)) return "Discord";
                if (procName.Contains("webhelper", StringComparison.OrdinalIgnoreCase) ||
                    procName.Equals("gameoverlayui", StringComparison.OrdinalIgnoreCase) ||
                    procName.Equals("steamservice", StringComparison.OrdinalIgnoreCase) ||
                    procName.StartsWith("steam", StringComparison.OrdinalIgnoreCase)) return "Steam";
                if (procName.Equals("tslgame", StringComparison.OrdinalIgnoreCase) ||
                    procName.Equals("coherentui_host", StringComparison.OrdinalIgnoreCase) ||
                    procName.Equals("pubg", StringComparison.OrdinalIgnoreCase)) return "PUBG: Battlegrounds";

                string[] suffixesToStrip = { "worker", "helper", "host", "service", "broker", "query", "renderer", "overlay", "limiter", "_be", "_dx11", "-x64" };
                string lowerName = procName.ToLowerInvariant();
                foreach (var suffix in suffixesToStrip)
                {
                    if (lowerName.EndsWith(suffix) && procName.Length > suffix.Length)
                    {
                        procName = procName.Substring(0, procName.Length - suffix.Length);
                        lowerName = procName.ToLowerInvariant();
                    }
                }
                return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(procName.ToLower());
            }

            try
            {
                string fallbackName = info.Session.DisplayName;
                if (!string.IsNullOrWhiteSpace(fallbackName) && !fallbackName.StartsWith("@"))
                {
                    if (fallbackName.Contains('(')) fallbackName = fallbackName.Split('(')[0].Trim();
                    if (fallbackName.Contains("discord", StringComparison.OrdinalIgnoreCase)) return "Discord";
                    return fallbackName;
                }
            }
            catch { }

            return "Systemsounds";
        }

        // UI-Thread: Ergebnis des Scans in die Kachelliste einarbeiten
        private void ApplyScanResult(MMDevice device, Dictionary<string, DetectedCard> detected)
        {
            if (!ReferenceEquals(device, _selectedDevice)) return;   // Gerät wurde inzwischen gewechselt

            bool structureChanged = false;
            bool orderChanged = false;

            var cardMap = new Dictionary<string, AudioSessionModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in Sessions) cardMap[s.Name] = s;

            foreach (var (appName, data) in detected)
            {
                if (cardMap.TryGetValue(appName, out var existing))
                {
                    existing.SyncSessions(data.Sessions);
                    continue;
                }

                if (appName.Equals("Discord", StringComparison.OrdinalIgnoreCase) &&
                    AppSettings.Current.AutoVolumeDiscord && data.Sessions.Count > 0)
                {
                    try
                    {
                        var sav = data.Sessions[0].SimpleAudioVolume;
                        if (sav != null) sav.Volume = AppSettings.Current.DiscordVolumeValue / 100f;
                    }
                    catch { }
                }

                var newCard = new AudioSessionModel(data.Sessions, appName, data.Icon)
                {
                    RequestMasterUnmute = () => { if (IsMasterMuted) IsMasterMuted = false; }
                };
                newCard.VolumeEdited += InvalidateProportionalBaseline;
                if (IsMasterMuted) newCard.IsMuted = true;

                var pinned = AppSettings.Current.PinnedSessionIds;
                if (pinned != null && pinned.Any(id =>
                        id.Equals(appName, StringComparison.OrdinalIgnoreCase) ||
                        (id.Contains("PUBG", StringComparison.OrdinalIgnoreCase) && appName.Contains("PUBG", StringComparison.OrdinalIgnoreCase))))
                {
                    newCard.IsPinned = true;
                }

                Sessions.Insert(GetInsertIndex(newCard), newCard);
                cardMap[appName] = newCard;
                structureChanged = true;

                var order = AppSettings.Current.SessionOrder ??= new List<string>();
                if (!order.Contains(newCard.Name, StringComparer.OrdinalIgnoreCase))
                {
                    order.Add(newCard.Name);
                    orderChanged = true;
                }
            }

            var deadCards = Sessions.Where(s => !detected.ContainsKey(s.Name)).ToList();
            foreach (var dead in deadCards)
            {
                dead.Dispose();
                Sessions.Remove(dead);
                structureChanged = true;
            }

            if (structureChanged)
            {
                InvalidateProportionalBaseline();
                AdjustWindowWidthToContent();
            }
            if (orderChanged) AppSettings.Save();
        }

        private static int IndexOfName(List<string>? list, string name)
        {
            if (list == null) return -1;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // Gepinnte Kacheln stehen vorn, innerhalb der Gruppe gilt die gespeicherte Reihenfolge.
        private int GetInsertIndex(AudioSessionModel card)
        {
            var order = AppSettings.Current.SessionOrder;
            int rank = IndexOfName(order, card.Name);

            int firstUnpinned = 0;
            while (firstUnpinned < Sessions.Count && Sessions[firstUnpinned].IsPinned) firstUnpinned++;

            int lo = card.IsPinned ? 0 : firstUnpinned;
            int hi = card.IsPinned ? firstUnpinned : Sessions.Count;

            if (rank < 0) return hi;

            for (int i = lo; i < hi; i++)
            {
                int other = IndexOfName(order, Sessions[i].Name);
                if (other < 0 || other > rank) return i;
            }
            return hi;
        }

        // ------------------------------------------------------------------ Icons

        // Läuft im Scan-Thread, bevor der erste Scan Icons braucht (früher parallel im Konstruktor gestartet –
        // dann konnten Kacheln dauerhaft ohne Icon entstehen). Reihenfolge wichtig: Standard-Icon zuerst.
        private void EnsureIconsLoaded()
        {
            if (_iconsLoaded) return;
            _defaultAppIcon = ExtractDefaultApplicationIcon();
            _systemIcon = ExtractSystemSoundsIcon();
            _razerIcon = ExtractRazerSpecificIcon();
            _iconsLoaded = true;
        }

        private ImageSource? GetAppIcon(SessionInfo info, string appName)
        {
            if (appName.Equals("System sounds", StringComparison.OrdinalIgnoreCase) ||
                appName.Equals("Systemsounds", StringComparison.OrdinalIgnoreCase)) return _systemIcon;

            if (_iconCache.TryGetValue(appName, out var cached)) return cached;

            ImageSource? icon = null;
            try
            {
                if (info.Pid > 0)
                {
                    string? path = GetProcessPathSafe(info.Pid);
                    if (!string.IsNullOrEmpty(path)) icon = ExtractIconFromFile(path);
                }
            }
            catch { }

            if (icon == null && appName.Equals("Razer Axon", StringComparison.OrdinalIgnoreCase)) icon = _razerIcon;
            icon ??= _defaultAppIcon;

            _iconCache[appName] = icon;
            return icon;
        }

        private string? GetProcessPathSafe(int processId)
        {
            try
            {
                IntPtr hProcess = OpenProcess(0x1000, false, processId);
                if (hProcess != IntPtr.Zero)
                {
                    try
                    {
                        var sb = new System.Text.StringBuilder(1024);
                        int capacity = sb.Capacity;
                        if (QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
                        {
                            string path = sb.ToString();
                            if (File.Exists(path)) return path;
                        }
                    }
                    finally { CloseHandle(hProcess); }
                }
            }
            catch { }
            return null;
        }

        private ImageSource? ExtractIconFromFile(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
                if (sysIcon != null && sysIcon.Handle != IntPtr.Zero)
                {
                    var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(sysIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmapSource.Freeze();
                    return bitmapSource;
                }
            }
            catch { }
            return null;
        }

        private ImageSource? ExtractRazerSpecificIcon()
        {
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                string[] rzPaths =
                {
                    Path.Combine(pf, "Razer", "Synapse3", "WPFUI", "Framework", "Razer Synapse 3.exe"),
                    Path.Combine(pf86, "Razer", "Synapse3", "WPFUI", "Framework", "Razer Synapse 3.exe"),
                    Path.Combine(pf, "Razer", "Razer Central", "Razer Central.exe"),
                    Path.Combine(pf86, "Razer", "Razer Central", "Razer Central.exe")
                };

                foreach (var path in rzPaths)
                {
                    if (!File.Exists(path)) continue;
                    var img = ExtractIconFromFile(path);
                    if (img != null) return img;
                }
            }
            catch { }
            return _defaultAppIcon;
        }

        private ImageSource? ExtractDefaultApplicationIcon()
        {
            string shell32Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll");
            return GetIconByResourceId(shell32Path, -16712) ?? GetIconByResourceId(shell32Path, -47);
        }

        private ImageSource? ExtractSystemSoundsIcon()
        {
            try
            {
                string sndVol = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "SndVol.exe");
                var icon = ExtractIconFromFile(sndVol);
                if (icon != null) return icon;
            }
            catch { }
            return _defaultAppIcon;
        }

        private ImageSource? GetIconByResourceId(string file, int resourceId)
        {
            if (!File.Exists(file)) return null;
            try
            {
                IntPtr[] phicon = new IntPtr[1];
                uint[] piconid = new uint[1];
                if (PrivateExtractIcons(file, Math.Abs(resourceId), 32, 32, phicon, piconid, 1, 0) > 0 && phicon[0] != IntPtr.Zero)
                {
                    try
                    {
                        var bitmapSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(phicon[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        bitmapSource.Freeze();
                        return bitmapSource;
                    }
                    finally { DestroyIcon(phicon[0]); }
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------------ Fensterbreite

        // Nur noch aufrufen, wenn sich die Kachelliste wirklich geändert hat (früher alle 3 s → Layout-Neuberechnung ohne Grund)
        private void AdjustWindowWidthToContent()
        {
            if (AppScrollViewer == null) return;

            // Listenansicht: feste Breite, vom Nutzer veränderbar – nie automatisch anpassen
            if (_isListLayout)
            {
                SizeToContent = SizeToContent.Manual;
                return;
            }

            if (!AppSettings.Current.AutoAdjustWidth)
            {
                SizeToContent = SizeToContent.Manual;
                AppScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                return;
            }

            try
            {
                AppScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                SizeToContent = SizeToContent.Width;
                UpdateLayout();
            }
            catch (Exception) { }
        }

        // ------------------------------------------------------------------ Discord-Limiter / Always-on-Top

        public bool IsDiscordLimiterActive
        {
            get => AppSettings.Current.AutoVolumeDiscord;
            set
            {
                if (AppSettings.Current.AutoVolumeDiscord == value) return;
                AppSettings.Current.AutoVolumeDiscord = value;
                AppSettings.Save();
                OnPropertyChanged(nameof(IsDiscordLimiterActive));
                TriggerAudioSessionScan();
            }
        }

        public bool IsListLayout
        {
            get => _isListLayout;
            set
            {
                if (_isListLayout == value) return;
                _isListLayout = value;
                AppSettings.Current.ListLayout = value;
                AppSettings.Save();
                OnPropertyChanged(nameof(IsListLayout));
                ApplyLayoutMode(initial: false);
            }
        }

        // Stellt Scrollbars, Mindestgrößen und Fensterbreite passend zur Ansicht ein.
        // (Die Umschaltung der Darstellung selbst passiert im XAML per DataTrigger auf IsListLayout.)
        private void ApplyLayoutMode(bool initial)
        {
            bool list = _isListLayout;

            if (AppScrollViewer != null)
            {
                AppScrollViewer.VerticalScrollBarVisibility = list ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
                AppScrollViewer.HorizontalScrollBarVisibility = list ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            }

            MinWidth = list ? 300 : 380;
            MinHeight = list ? 260 : 380;

            if (list)
            {
                SizeToContent = SizeToContent.Manual;
                if (!initial)
                {
                    _cardModeWidth = ActualWidth;
                    Width = 340;
                }
            }
            else if (!initial)
            {
                if (!AppSettings.Current.AutoAdjustWidth && _cardModeWidth > 0) Width = _cardModeWidth;
                AdjustWindowWidthToContent();
            }
        }

        public bool IsAlwaysOnTop
        {
            get => _isAlwaysOnTop;
            set
            {
                if (_isAlwaysOnTop == value) return;
                _isAlwaysOnTop = value;
                Topmost = value;
                AppSettings.Current.IsAlwaysOnTop = value;
                AppSettings.Save();
                OnPropertyChanged(nameof(IsAlwaysOnTop));
            }
        }

        // ------------------------------------------------------------------ Tray / Fenster

        private void SetupTrayIcon()
        {
            try
            {
                System.Drawing.Icon? trayIcon = null;
                try
                {
                    var iconUri = new Uri("pack://application:,,,/app.ico", UriKind.Absolute);
                    var streamInfo = System.Windows.Application.GetResourceStream(iconUri);
                    if (streamInfo != null)
                    {
                        using var stream = streamInfo.Stream;
                        trayIcon = new System.Drawing.Icon(stream);
                    }
                }
                catch { }

                trayIcon ??= File.Exists("app.ico") ? new System.Drawing.Icon("app.ico") : System.Drawing.SystemIcons.Application;

                _notifyIcon = new System.Windows.Forms.NotifyIcon { Icon = trayIcon, Text = "Dark Volume Mixer", Visible = true };
                var contextMenu = new System.Windows.Forms.ContextMenuStrip();
                contextMenu.Items.Add("Öffnen", null, (s, e) => Dispatcher.Invoke(RestoreWindow));
                contextMenu.Items.Add("Einstellungen...", null, (s, e) => Dispatcher.Invoke(OpenSettingsWindow));
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add("Beenden", null, (s, e) => Dispatcher.Invoke(ShutdownApp));

                _notifyIcon.ContextMenuStrip = contextMenu;
                _notifyIcon.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Dispatcher.Invoke(RestoreWindow); };
            }
            catch { }
        }

        private void RestoreWindow()
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = _isAlwaysOnTop;
            Focus();
        }

        private void OpenSettingsWindow()
        {
            var settingsWin = new SettingsWindow { Owner = this };
            if (settingsWin.ShowDialog() == true)
            {
                _isAlwaysOnTop = AppSettings.Current.IsAlwaysOnTop;
                Topmost = _isAlwaysOnTop;
                OnPropertyChanged(nameof(IsAlwaysOnTop));
                OnPropertyChanged(nameof(IsDiscordLimiterActive));
                InvalidateProportionalBaseline();
                IsListLayout = AppSettings.Current.ListLayout;   // wendet die Ansicht an, falls geändert
                AdjustWindowWidthToContent();
                TriggerAudioSessionScan();
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();
        // In den Tray: Fenster verstecken -> verschwindet auch aus der Taskleiste.
        // Zurück per Klick auf das Tray-Symbol ("Öffnen"). Offen = Taskleisten-Eintrag sichtbar.
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => Hide();
        private void CloseButton_Click(object sender, RoutedEventArgs e) => ShutdownApp();
        private void Window_Closing(object? sender, CancelEventArgs e) => ShutdownApp();
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateUiActive();
            AdjustWindowWidthToContent();
        }

        // Fenster ziehen: nur dort, wo nichts anderes passiert (nicht auf Reglern, Buttons, Kacheln).
        // Früher löste jeder Klick – auch auf eine Kachel – DragMove aus und kam dem Kachel-Umsortieren in die Quere.
        private void Window_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (e.OriginalSource is DependencyObject source && IsInteractiveArea(source)) return;

            try { DragMove(); } catch (InvalidOperationException) { }
        }

        private static bool IsInteractiveArea(DependencyObject source)
        {
            DependencyObject? current = source;
            while (current != null)
            {
                if (current is Slider or System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.ComboBox or ComboBoxItem or System.Windows.Controls.Primitives.ButtonBase) return true;
                if (current is FrameworkElement { DataContext: AudioSessionModel }) return true;

                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
            }
            return false;
        }

        // ------------------------------------------------------------------ Mausrad

        private void MasterSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!IsMasterEnabled) return;
            MasterVolume += (e.Delta > 0) ? 2 : -2;
            e.Handled = true;
        }

        private void AppSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is Border { DataContext: AudioSessionModel session })
            {
                if (session.IsEnabled)
                    session.Volume = Math.Clamp(session.Volume + (e.Delta > 0 ? 2f : -2f), 0f, 100f);
                e.Handled = true;
            }
        }

        // ------------------------------------------------------------------ Kacheln: Pin, Drag & Drop

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject? parentObject = VisualTreeHelper.GetParent(child);
            while (parentObject != null)
            {
                if (parentObject is T parent) return parent;
                parentObject = VisualTreeHelper.GetParent(parentObject);
            }
            return null;
        }

        private void AppCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject depObj)
            {
                if (FindVisualParent<Slider>(depObj) != null ||
                    FindVisualParent<Thumb>(depObj) != null ||
                    FindVisualParent<RepeatButton>(depObj) != null ||
                    FindVisualParent<ToggleButton>(depObj) != null) return;
            }

            if (sender is Border { DataContext: AudioSessionModel session } && session.IsEnabled)
            {
                _dragStartPoint = e.GetPosition(null);
                _draggedData = session;
            }
        }

        private void AppCard_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _draggedData = null;

        private void AppCard_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _draggedData == null) return;

            Vector diff = _dragStartPoint - e.GetPosition(null);
            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (sender is Border border)
                {
                    var data = _draggedData;
                    _draggedData = null;
                    DragDrop.DoDragDrop(border, data, System.Windows.DragDropEffects.Move);
                }
            }
        }

        private void AppCard_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(AudioSessionModel))) return;

            if (e.Data.GetData(typeof(AudioSessionModel)) is AudioSessionModel dropped &&
                sender is Border { DataContext: AudioSessionModel target })
            {
                int oldIndex = Sessions.IndexOf(dropped);
                int newIndex = Sessions.IndexOf(target);

                if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
                {
                    Sessions.Move(oldIndex, newIndex);
                    AppSettings.Current.SessionOrder = Sessions.Select(s => s.Name).Distinct().ToList();
                    AppSettings.Save();
                }
            }
        }

        private void AppIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: AudioSessionModel session })
            {
                session.IsPinned = !session.IsPinned;
                ReorderSessionsAfterPin(session);
            }
        }

        public void ReorderSessionsAfterPin(AudioSessionModel changedSession)
        {
            try
            {
                if (!Sessions.Contains(changedSession)) return;

                Sessions.Remove(changedSession);
                var pinned = AppSettings.Current.PinnedSessionIds ??= new List<string>();

                if (changedSession.IsPinned)
                {
                    int insertIndex = 0;
                    while (insertIndex < Sessions.Count && Sessions[insertIndex].IsPinned) insertIndex++;
                    Sessions.Insert(insertIndex, changedSession);

                    if (!pinned.Contains(changedSession.Name, StringComparer.OrdinalIgnoreCase))
                        pinned.Add(changedSession.Name);
                }
                else
                {
                    Sessions.Add(changedSession);
                    pinned.RemoveAll(id => id.Equals(changedSession.Name, StringComparison.OrdinalIgnoreCase));
                }

                AppSettings.Current.SessionOrder = Sessions.Select(s => s.Name).Distinct().ToList();
                AppSettings.Save();
            }
            catch { }
        }

        // ------------------------------------------------------------------ Beenden

        private void ShutdownApp()
        {
            if (_isShuttingDown) return;   // Application.Shutdown schließt das Fenster erneut → sonst Doppelaufruf
            _isShuttingDown = true;

            _bgPeakCts?.Cancel();
            _uiRenderTimer.Stop();
            _autoScanTimer.Stop();
            _debounceScanTimer.Stop();
            _masterResyncTimer.Stop();

            DetachDeviceEvents(_selectedDevice);

            // Minimiert liefern Left/Top/Width/Height Unsinn (-32000) → dann RestoreBounds nehmen.
            Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (!bounds.IsEmpty)
            {
                AppSettings.Current.WindowX = bounds.Left;
                AppSettings.Current.WindowY = bounds.Top;
                AppSettings.Current.WindowWidth = bounds.Width;
                AppSettings.Current.WindowHeight = bounds.Height;
            }
            AppSettings.Current.IsAlwaysOnTop = _isAlwaysOnTop;
            AppSettings.SaveNow();

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }

            foreach (var session in Sessions) session.Dispose();

            System.Windows.Application.Current.Shutdown();
        }

        // ------------------------------------------------------------------ INPC

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
