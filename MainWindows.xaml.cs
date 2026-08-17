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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace DarkVolumeMixer
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<AudioSessionModel> Sessions { get; set; } = new ObservableCollection<AudioSessionModel>();
        public ObservableCollection<MMDevice> Devices { get; set; } = new ObservableCollection<MMDevice>();

        private DispatcherTimer _uiRenderTimer;
        private DispatcherTimer _autoScanTimer;
        private DispatcherTimer _debounceScanTimer;
        
        private CancellationTokenSource? _bgPeakCts;
        private MMDevice? _selectedDevice;
        private ImageSource? _systemIcon;
        private ImageSource? _razerIcon;
        private ImageSource? _defaultAppIcon;

        private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

        private float _masterVolume;
        private bool _isMasterMuted;
        private float _masterPeak;
        private float _rawMasterPeak;
        private bool _isAlwaysOnTop = false;
        private int _scanLock = 0;

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

        public MainWindow()
        {
            InitializeComponent();

            DataContext = this;
            SessionList.ItemsSource = Sessions;

            _isAlwaysOnTop = AppSettings.Current.IsAlwaysOnTop;
            Topmost = _isAlwaysOnTop;

            if (AppSettings.Current.WindowHeight > 0) Height = AppSettings.Current.WindowHeight;
            if (AppSettings.Current.WindowWidth > 0) Width = AppSettings.Current.WindowWidth;

            if (AppSettings.Current.WindowX.HasValue && AppSettings.Current.WindowY.HasValue)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = AppSettings.Current.WindowX.Value;
                Top = AppSettings.Current.WindowY.Value;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (defaultDevice != null)
                {
                    _masterVolume = defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
                    _isMasterMuted = defaultDevice.AudioEndpointVolume.Mute;
                }
            }
            catch { }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                _systemIcon = ExtractSystemSoundsIcon();
                _defaultAppIcon = ExtractDefaultApplicationIcon();
                _razerIcon = ExtractRazerSpecificIcon();
            });

            LoadDevices();
            SetupTrayIcon();

            _debounceScanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _debounceScanTimer.Tick += (s, e) => { _debounceScanTimer.Stop(); ThreadPool.QueueUserWorkItem(_ => ExecuteLoadAudioSessions()); };

            _autoScanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _autoScanTimer.Tick += (s, e) => ThreadPool.QueueUserWorkItem(_ => ExecuteLoadAudioSessions());
            _autoScanTimer.Start();

            // UI Render Timer: Zieht sich die Werte frame-synchron ab (KEIN Überlauf möglich!)
            _uiRenderTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) }; // 30 FPS
            _uiRenderTimer.Tick += UiRenderTimer_Tick;
            _uiRenderTimer.Start();

            ThreadPool.QueueUserWorkItem(_ => ExecuteLoadAudioSessions());
            StartBackgroundAudioWorker();
        }

        // Reine Hintergrund-Berechnung (NUR Speicher, 0 Dispatcher-Aufrufe)
        private void StartBackgroundAudioWorker()
        {
            _bgPeakCts?.Cancel();
            _bgPeakCts = new CancellationTokenSource();
            var token = _bgPeakCts.Token;

            ThreadPool.QueueUserWorkItem(async _ =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (_selectedDevice != null)
                        {
                            float raw = 0f;
                            try { raw = _selectedDevice.AudioMeterInformation.MasterPeakValue * 100f; } catch { }
                            _rawMasterPeak = _isMasterMuted ? 0f : raw * (_masterVolume / 100f);
                        }

                        for (int i = 0; i < Sessions.Count; i++)
                        {
                            if (i < Sessions.Count)
                            {
                                Sessions[i]?.UpdateRawPeak();
                            }
                        }
                    }
                    catch { }

                    try { await Task.Delay(30, token); } catch { break; }
                }
            });
        }

        // UI holt sich die Werte ab
        private void UiRenderTimer_Tick(object? sender, EventArgs e)
        {
            if (!IsVisible || WindowState == WindowState.Minimized) return;

            // Master Peak Glätten
            float target = _rawMasterPeak;
            if (target >= _masterPeak)
                _masterPeak = target;
            else
                _masterPeak = Math.Max(0f, _masterPeak - ((_masterPeak - target) * 0.45f) - 1.5f);

            OnPropertyChanged(nameof(MasterPeak));

            // App Peaks Glätten
            for (int i = 0; i < Sessions.Count; i++)
            {
                Sessions[i]?.RenderTickPeak();
            }
        }

        private void AudioEndpointVolume_OnVolumeNotification(AudioVolumeNotificationData data)
        {
            Dispatcher.InvokeAsync(() =>
            {
                bool extMute = data.Muted;
                float extVol = data.MasterVolume * 100f;

                if (_isMasterMuted != extMute)
                {
                    _isMasterMuted = extMute;
                    OnPropertyChanged(nameof(IsMasterMuted));
                    OnPropertyChanged(nameof(IsMasterEnabled));
                    foreach (var s in Sessions) s.IsMuted = extMute;
                }

                if (Math.Abs(_masterVolume - extVol) > 1.0f)
                {
                    _masterVolume = extVol;
                    OnPropertyChanged(nameof(MasterVolume));
                }
            });
        }

        private void AudioSessionManager_OnSessionCreated(object? sender, IAudioSessionControl newSession) =>
            Dispatcher.InvokeAsync(TriggerAudioSessionScan);

        private void TriggerAudioSessionScan()
        {
            _debounceScanTimer.Stop();
            _debounceScanTimer.Start();
        }

        public void LoadAudioSessions() => TriggerAudioSessionScan();

        private static bool IsSessionAlive(AudioSessionControl session)
        {
            try
            {
                if (session.State == AudioSessionState.AudioSessionStateExpired) return false;
                int pid = 0;
                try { pid = (int)session.GetProcessID; } catch { }
                if (pid <= 0) return true;

                try
                {
                    using var proc = Process.GetProcessById(pid);
                    return !proc.HasExited;
                }
                catch (ArgumentException) { return false; }
                catch { return true; }
            }
            catch { return false; }
        }

        private string GetGenericAppName(AudioSessionControl session)
        {
            try
            {
                int processId = 0;
                try { processId = (int)session.GetProcessID; } catch { }

                if (processId > 0)
                {
                    using var proc = Process.GetProcessById(processId);
                    string procName = proc.ProcessName;

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
                    string lowerName = procName.ToLower();
                    foreach (var suffix in suffixesToStrip)
                    {
                        if (lowerName.EndsWith(suffix))
                        {
                            procName = procName.Substring(0, procName.Length - suffix.Length);
                            lowerName = procName.ToLower();
                        }
                    }
                    return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(procName.ToLower());
                }
            }
            catch { }

            string fallbackName = session.DisplayName;
            if (!string.IsNullOrWhiteSpace(fallbackName) && !fallbackName.StartsWith("@"))
            {
                if (fallbackName.Contains("(", StringComparison.Ordinal)) fallbackName = fallbackName.Split('(')[0].Trim();
                if (fallbackName.Contains("discord", StringComparison.OrdinalIgnoreCase)) return "Discord";
                return fallbackName;
            }
            return "Systemsounds";
        }

public void ExecuteLoadAudioSessions()
{
    if (Interlocked.Exchange(ref _scanLock, 1) == 1) return;

    try
    {
        if (_selectedDevice == null) return;
        var sessionManager = _selectedDevice.AudioSessionManager;
        if (sessionManager == null) return;

        try { sessionManager.RefreshSessions(); } catch { }
        var sessions = sessionManager.Sessions;
        if (sessions == null) return;

        int count = sessions.Count;
        var activeSessions = new List<AudioSessionControl>();

        for (int i = 0; i < count; i++)
        {
            try
            {
                var session = sessions[i];
                if (session != null && IsSessionAlive(session)) activeSessions.Add(session);
            }
            catch { }
        }

        // Sessions pro App gruppieren – verhindert Zombie-Duplikate!
        var detectedCards = new Dictionary<string, (List<AudioSessionControl> sessionList, ImageSource? icon)>(StringComparer.OrdinalIgnoreCase);
        int discordZaehler = 0;

        foreach (var session in activeSessions)
        {
            string appName = GetGenericAppName(session);
            if (string.IsNullOrEmpty(appName)) continue;

            if (appName.Equals("Discord", StringComparison.OrdinalIgnoreCase))
            {
                discordZaehler++;
                if (discordZaehler == 1) appName = "Discord";
                else if (discordZaehler == 2) appName = "Discord (Voice)";
                else if (discordZaehler > 2) appName = $"Discord ({discordZaehler})";
            }

            if (!detectedCards.TryGetValue(appName, out var cardData))
            {
                ImageSource? icon = GetAppIcon(session);
                cardData = (new List<AudioSessionControl>(), icon);
                detectedCards[appName] = cardData;
            }
            cardData.sessionList.Add(session);
        }

        Dispatcher.Invoke(() =>
        {
            var cardMap = Sessions.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in detectedCards)
            {
                string appName = kvp.Key;
                var (sessionList, icon) = kvp.Value;

                if (cardMap.TryGetValue(appName, out var existingCard))
                {
                    existingCard.SyncSessions(sessionList);
                }
                else
                {
                    if (appName.Equals("Discord", StringComparison.OrdinalIgnoreCase) && sessionList.Count > 0 && sessionList[0].SimpleAudioVolume != null && AppSettings.Current.AutoVolumeDiscord)
                    {
                        sessionList[0].SimpleAudioVolume.Volume = AppSettings.Current.DiscordVolumeValue / 100f; 
                    }

                    var newCard = new AudioSessionModel(sessionList, appName, icon)
                    {
                        RequestMasterUnmute = () => { if (IsMasterMuted) IsMasterMuted = false; }
                    };

                    if (IsMasterMuted) newCard.IsMuted = true;

                    if (AppSettings.Current.PinnedSessionIds != null)
                    {
                        bool shouldBePinned = AppSettings.Current.PinnedSessionIds.Any(id => 
                            id.Equals(appName, StringComparison.OrdinalIgnoreCase) ||
                            (id.Contains("PUBG", StringComparison.OrdinalIgnoreCase) && appName.Contains("PUBG", StringComparison.OrdinalIgnoreCase)));
                        if (shouldBePinned) newCard.IsPinned = true;
                    }

                    if (newCard.IsPinned)
                    {
                        int insertIndex = 0;
                        int savedRank = AppSettings.Current.SessionOrder?.IndexOf(newCard.Name) ?? -1;

                        while (insertIndex < Sessions.Count && Sessions[insertIndex].IsPinned)
                        {
                            int otherRank = AppSettings.Current.SessionOrder?.IndexOf(Sessions[insertIndex].Name) ?? -1;
                            if (savedRank >= 0 && otherRank >= 0 && savedRank < otherRank)
                            {
                                break;
                            }
                            insertIndex++;
                        }
                        Sessions.Insert(insertIndex, newCard);
                    }
                    else
                    {
                        int firstUnpinnedIndex = 0;
                        while (firstUnpinnedIndex < Sessions.Count && Sessions[firstUnpinnedIndex].IsPinned)
                        {
                            firstUnpinnedIndex++;
                        }

                        int targetIndex = -1;
                        int savedRank = AppSettings.Current.SessionOrder?.IndexOf(newCard.Name) ?? -1;

                        if (savedRank >= 0)
                        {
                            for (int i = firstUnpinnedIndex; i < Sessions.Count; i++)
                            {
                                int otherRank = AppSettings.Current.SessionOrder?.IndexOf(Sessions[i].Name) ?? -1;
                                if (otherRank < 0 || otherRank > savedRank)
                                {
                                    targetIndex = i;
                                    break;
                                }
                            }
                        }

                        if (targetIndex >= 0 && targetIndex <= Sessions.Count)
                        {
                            Sessions.Insert(targetIndex, newCard);
                        }
                        else
                        {
                            Sessions.Add(newCard);
                        }
                    }

                    if (AppSettings.Current.SessionOrder == null) AppSettings.Current.SessionOrder = new List<string>();
                    if (!AppSettings.Current.SessionOrder.Contains(newCard.Name, StringComparer.OrdinalIgnoreCase))
                        AppSettings.Current.SessionOrder.Add(newCard.Name);

                    AppSettings.Save();
                    cardMap[appName] = newCard;
                }
            }

            var deadCards = Sessions.Where(s => !detectedCards.ContainsKey(s.Name)).ToList();
            if (deadCards.Count > 0)
            {
                foreach (var dead in deadCards)
                {
                    dead.Dispose();
                    Sessions.Remove(dead);
                }
                AppSettings.Save();
            }

            AdjustWindowWidthToContent();
        });
    }
    catch { }
    finally { Interlocked.Exchange(ref _scanLock, 0); }
}

        private ImageSource? GetAppIcon(AudioSessionControl session)
        {
            string appName = GetGenericAppName(session);
            if (appName.Equals("System sounds", StringComparison.OrdinalIgnoreCase) || appName.Equals("Systemsounds", StringComparison.OrdinalIgnoreCase)) return _systemIcon;
            if (_iconCache.TryGetValue(appName, out var cached)) return cached;

            try
            {
                int processId = (int)session.GetProcessID;
                if (processId > 0)
                {
                    string? path = GetProcessPathSafe(processId);
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        var icon = ExtractIconFromFile(path);
                        _iconCache[appName] = icon;
                        return icon;
                    }
                }
            }
            catch { }
            
            _iconCache[appName] = _defaultAppIcon;
            return _defaultAppIcon;
        }

        private string? GetProcessPathSafe(int processId)
        {
            try
            {
                IntPtr hProcess = OpenProcess(0x1000, false, processId);
                if (hProcess != IntPtr.Zero)
                {
                    var sb = new System.Text.StringBuilder(1024);
                    int capacity = sb.Capacity;
                    if (QueryFullProcessImageName(hProcess, 0, sb, ref capacity))
                    {
                        CloseHandle(hProcess);
                        string path = sb.ToString();
                        if (File.Exists(path)) return path;
                    }
                    CloseHandle(hProcess);
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
                    var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(sysIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
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
                string[] rzPaths = {
                    Path.Combine(pf, "Razer", "Synapse3", "WPFUI", "Framework", "Razer Synapse 3.exe"),
                    Path.Combine(pf86, "Razer", "Synapse3", "WPFUI", "Framework", "Razer Synapse 3.exe"),
                    Path.Combine(pf, "Razer", "Razer Central", "Razer Central.exe"),
                    Path.Combine(pf86, "Razer", "Razer Central", "Razer Central.exe")
                };

                foreach (var path in rzPaths)
                {
                    if (File.Exists(path))
                    {
                        var img = ExtractIconFromFile(path);
                        if (img != null) return img;
                    }
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
                string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string sndVol = Path.Combine(systemDir, "SndVol.exe");
                if (File.Exists(sndVol))
                {
                    using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(sndVol);
                    if (sysIcon != null && sysIcon.Handle != IntPtr.Zero)
                    {
                        var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(sysIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        bitmapSource.Freeze();
                        return bitmapSource;
                    }
                }
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
                    var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(phicon[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmapSource.Freeze();
                    DestroyIcon(phicon[0]);
                    return bitmapSource;
                }
            }
            catch { }
            return null;
        }

private void AdjustWindowWidthToContent()
{
    if (AppScrollViewer == null) return;

    if (!AppSettings.Current.AutoAdjustWidth)
    {
        SizeToContent = SizeToContent.Manual;
        AppScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        return;
    }

    try
    {
        AppScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        // Lässt WPF die exakte Breite des gesamten Inhalts automatisch bestimmen
        this.SizeToContent = SizeToContent.Width;
        this.UpdateLayout();
    }
    catch (Exception) { }
}

        public bool IsDiscordLimiterActive
        {
            get => AppSettings.Current.AutoVolumeDiscord;
            set
            {
                if (AppSettings.Current.AutoVolumeDiscord != value)
                {
                    AppSettings.Current.AutoVolumeDiscord = value;
                    AppSettings.Save();
                    OnPropertyChanged(nameof(IsDiscordLimiterActive));
                    TriggerAudioSessionScan();
                }
            }
        }

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

                if (trayIcon == null) trayIcon = File.Exists("app.ico") ? new System.Drawing.Icon("app.ico") : System.Drawing.SystemIcons.Application;

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
                AdjustWindowWidthToContent();
                OnPropertyChanged(nameof(IsDiscordLimiterActive));
                TriggerAudioSessionScan();
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();
        private void DiscordLimiterButton_Click(object sender, RoutedEventArgs e) => IsDiscordLimiterActive = !IsDiscordLimiterActive;
        private void PinApp_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: AudioSessionModel session }) session.IsPinned = !session.IsPinned; }
        
        private void AppMuteButton_Unchecked(object sender, RoutedEventArgs e) 
        { 
            if (sender is FrameworkElement { DataContext: AudioSessionModel session }) 
            {
                session.IsMuted = false;
                if (IsMasterMuted) IsMasterMuted = false;
            } 
        }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void CloseButton_Click(object sender, RoutedEventArgs e) => ShutdownApp();
        private void Window_Closing(object sender, CancelEventArgs e) => ShutdownApp();

        private void AlwaysOnTopButton_Click(object sender, RoutedEventArgs e)
        {
            _isAlwaysOnTop = !_isAlwaysOnTop;
            Topmost = _isAlwaysOnTop;
        }

        public bool IsAlwaysOnTop
        {
            get => _isAlwaysOnTop;
            set
            {
                if (_isAlwaysOnTop != value)
                {
                    _isAlwaysOnTop = value;
                    Topmost = value;
                    AppSettings.Current.IsAlwaysOnTop = value;
                    AppSettings.Save();
                    OnPropertyChanged(nameof(IsAlwaysOnTop));
                }
            }
        }

        private void MasterSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!IsMasterEnabled) return;
            MasterVolume += (e.Delta > 0) ? 2 : -2;
            e.Handled = true;
        }

        private void AppSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is Border border && border.DataContext is AudioSessionModel session)
            {
                float change = e.Delta > 0 ? 2f : -2f;
                session.Volume = Math.Clamp(session.Volume + change, 0f, 100f);
                e.Handled = true;
            }
        }

        private void Slider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }
        private void Slider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) { }

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindVisualParent<T>(parentObject);
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

            if (sender is Border border && border.DataContext is AudioSessionModel session && session.IsEnabled)
            {
                _dragStartPoint = e.GetPosition(null);
                _draggedData = session;
            }
        }

        private void AppCard_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _draggedData != null)
            {
                Vector diff = _dragStartPoint - e.GetPosition(null);
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    if (sender is Border border)
                    {
                        DragDrop.DoDragDrop(border, _draggedData, System.Windows.DragDropEffects.Move);
                        _draggedData = null;
                    }
                }
            }
        }

        private void AppCard_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(AudioSessionModel)))
            {
                var droppedData = e.Data.GetData(typeof(AudioSessionModel)) as AudioSessionModel;
                if (droppedData != null && sender is Border border && border.DataContext is AudioSessionModel targetData)
                {
                    int oldIndex = Sessions.IndexOf(droppedData);
                    int newIndex = Sessions.IndexOf(targetData);

                    if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
                    {
                        Sessions.Move(oldIndex, newIndex);
                        AppSettings.Current.SessionOrder = Sessions.Select(s => s.Name).Distinct().ToList();
                        AppSettings.Save();
                    }
                }
            }
        }

        public bool IsMasterEnabled => !IsMasterMuted;

        public bool IsMasterMuted
        {
            get => _isMasterMuted;
            set
            {
                if (_isMasterMuted != value)
                {
                    _isMasterMuted = value;
                    if (_selectedDevice != null)
                    {
                        try { _selectedDevice.AudioEndpointVolume.Mute = value; } catch { }
                    }

                    OnPropertyChanged(nameof(IsMasterMuted));
                    OnPropertyChanged(nameof(IsMasterEnabled));

                    foreach (var session in Sessions) session.IsMuted = value;
                }
            }
        }

        public MMDevice? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (_selectedDevice != value)
                {
                    if (_selectedDevice != null)
                    {
                        try { _selectedDevice.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification; } catch { }
                        if (_selectedDevice.AudioSessionManager != null)
                        {
                            try { _selectedDevice.AudioSessionManager.OnSessionCreated -= AudioSessionManager_OnSessionCreated; } catch { }
                        }
                    }

                    _selectedDevice = value;

                    if (_selectedDevice != null)
                    {
                        try
                        {
                            _masterVolume = _selectedDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100f;
                            _isMasterMuted = _selectedDevice.AudioEndpointVolume.Mute;
                            _selectedDevice.AudioEndpointVolume.OnVolumeNotification += AudioEndpointVolume_OnVolumeNotification;
                        }
                        catch { }

                        if (_selectedDevice.AudioSessionManager != null)
                        {
                            try 
                            { 
                                _selectedDevice.AudioSessionManager.RefreshSessions();
                                _selectedDevice.AudioSessionManager.OnSessionCreated += AudioSessionManager_OnSessionCreated; 
                            } 
                            catch { }
                        }
                    }

                    OnPropertyChanged(nameof(SelectedDevice));
                    OnPropertyChanged(nameof(MasterVolume));
                    OnPropertyChanged(nameof(IsMasterMuted));
                    OnPropertyChanged(nameof(IsMasterEnabled));
                    Sessions.Clear();
                    TriggerAudioSessionScan();
                }
            }
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

        public float MasterVolume
        {
            get => _masterVolume;
            set
            {
                float clamped = Math.Clamp(value, 0f, 100f);
                if (Math.Abs(_masterVolume - clamped) > 0.05f)
                {
                    float oldMaster = _masterVolume;
                    _masterVolume = clamped;
                    OnPropertyChanged(nameof(MasterVolume));

                    if (_masterVolume > 0 && IsMasterMuted) IsMasterMuted = false;

                    if (AppSettings.Current.ProportionalMaster && oldMaster > 0)
                    {
                        float ratio = clamped / oldMaster;
                        foreach (var session in Sessions)
                        {
                            if (session.IsEnabled) session.Volume = Math.Clamp(session.Volume * ratio, 0f, 100f);
                        }
                    }

                    float target = clamped / 100f;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        if (_selectedDevice != null)
                        {
                            try { _selectedDevice.AudioEndpointVolume.MasterVolumeLevelScalar = target; } catch { }
                        }
                    });
                }
            }
        }

        public float MasterPeak { get => _masterPeak; set { _masterPeak = value; OnPropertyChanged(nameof(MasterPeak)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void ReorderSessionsAfterPin(AudioSessionModel changedSession)
        {
            try
            {
                if (Sessions.Contains(changedSession))
                {
                    Sessions.Remove(changedSession);

                    if (changedSession.IsPinned)
                    {
                        int insertIndex = 0;
                        while (insertIndex < Sessions.Count && Sessions[insertIndex].IsPinned) insertIndex++;
                        Sessions.Insert(insertIndex, changedSession);

                        if (!AppSettings.Current.PinnedSessionIds.Contains(changedSession.Name, StringComparer.OrdinalIgnoreCase))
                            AppSettings.Current.PinnedSessionIds.Add(changedSession.Name);
                    }
                    else
                    {
                        Sessions.Add(changedSession);
                        AppSettings.Current.PinnedSessionIds.RemoveAll(id => id.Equals(changedSession.Name, StringComparison.OrdinalIgnoreCase));
                    }

                    AppSettings.Current.SessionOrder = Sessions.Select(s => s.Name).Distinct().ToList();
                    AppSettings.Save();
                }
            }
            catch { }
        }

        private void ShutdownApp()
        {
            _bgPeakCts?.Cancel();
            _uiRenderTimer?.Stop();

            if (_selectedDevice != null)
            {
                try { _selectedDevice.AudioEndpointVolume.OnVolumeNotification -= AudioEndpointVolume_OnVolumeNotification; } catch { }
                if (_selectedDevice.AudioSessionManager != null)
                {
                    try { _selectedDevice.AudioSessionManager.OnSessionCreated -= AudioSessionManager_OnSessionCreated; } catch { }
                }
            }

            AppSettings.Current.WindowHeight = Height;
            AppSettings.Current.WindowWidth = Width;
            AppSettings.Current.WindowX = Left;
            AppSettings.Current.WindowY = Top;
            AppSettings.Current.IsAlwaysOnTop = _isAlwaysOnTop;
            AppSettings.Save();

            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            System.Windows.Application.Current.Shutdown();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            AdjustWindowWidthToContent();
        }

        private void AppIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is AudioSessionModel session)
            {
                session.IsPinned = !session.IsPinned;
                ReorderSessionsAfterPin(session);
            }
        }     
    }
}