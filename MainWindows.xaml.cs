using System.Windows.Controls;
using System.Windows;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using System.Windows.Controls.Primitives;

namespace DarkVolumeMixer
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        public ObservableCollection<AudioSessionModel> Sessions { get; set; } = new ObservableCollection<AudioSessionModel>();
        public ObservableCollection<MMDevice> Devices { get; set; } = new ObservableCollection<MMDevice>();

        private DispatcherTimer _autoScanTimer;
        private DispatcherTimer _debounceScanTimer;
        private DispatcherTimer _vuMeterTimer;
        
        private MMDevice? _selectedDevice;
        private ImageSource? _systemIcon;
        private ImageSource? _razerIcon;
        private ImageSource? _defaultAppIcon;

        private float _masterVolume;
        private bool _isMasterMutedCache;
        private float _masterPeak;
        private bool _isAlwaysOnTop = false;

        private bool _hasRestoredInitialOrder = false;

        private System.Windows.Forms.NotifyIcon? _notifyIcon;
        private System.Windows.Point _dragStartPoint;
        private AudioSessionModel? _draggedData;

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern int ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, int nIcons);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint PrivateExtractIcons(
            string lpszFile, int nIconIndex, int cxIcon, int cyIcon,
            IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetModuleFileNameEx(IntPtr hProcess, IntPtr hModule, System.Text.StringBuilder lpFilename, int nSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, System.Text.StringBuilder lpExeName, ref int pdwSize);

        public MainWindow()
        {
            InitializeComponent();

            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Sessions);
            if (view != null)
            {
                view.SortDescriptions.Clear();
            }

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
                var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _masterVolume = defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100;

                var args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length - 1; i++)
                {
                    string arg = args[i].ToLower();
                    if (arg == "-x" || arg == "--x") { if (double.TryParse(args[i + 1], out double val)) Left = val; }
                    if (arg == "-y" || arg == "--y") { if (double.TryParse(args[i + 1], out double val)) Top = val; }
                    if (arg == "-h" || arg == "--height") { if (double.TryParse(args[i + 1], out double val)) Height = val; }
                    if (arg == "-w" || arg == "--width") { if (double.TryParse(args[i + 1], out double val)) Width = val; }
                }
            }
            catch { }

            Task.Run(() =>
            {
                _systemIcon = ExtractSystemSoundsIcon();
                _defaultAppIcon = ExtractDefaultApplicationIcon();
                _razerIcon = ExtractRazerSpecificIcon();
            });

            LoadDevices();
            SetupTrayIcon();

            _debounceScanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _debounceScanTimer.Tick += (s, e) => { _debounceScanTimer.Stop(); ExecuteLoadAudioSessions(); };

            _vuMeterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _vuMeterTimer.Tick += UpdateVUMeters;
            _vuMeterTimer.Start();

            _autoScanTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _autoScanTimer.Tick += (s, e) => 
            {
                ExecuteLoadAudioSessions();
            };
            _autoScanTimer.Start();
        
            Dispatcher.InvokeAsync(() => 
            {
                TriggerAudioSessionScan();
            }, DispatcherPriority.Loaded);
        }

        private void Window_Loaded(object sender, RoutedEventArgs e) => AdjustWindowWidthToContent();

        private void AppIcon_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is AudioSessionModel session)
            {
                session.IsPinned = !session.IsPinned;
            }
        }

private void AdjustWindowWidthToContent()
{
    try
    {
        double masterWidth = 111;  // Breite der Master-Kachel
        double cardWidth = 111;    // Breite einer normalen Kachel
        double windowPadding = 20; // Äußere Ränder des Fensters (links + rechts)

        // Gesamte Breite basierend auf der aktuellen Anzahl der Kacheln berechnen
        double calculatedWidth = windowPadding + masterWidth + (Sessions.Count * cardWidth);

        // WICHTIG: Einen kleinen Sicherheits-Puffer von 20 bis 25 Pixeln addieren, 
        // damit beim Schließen von Kanälen niemals Rundungsfehler den Scrollbalken auslösen!
        calculatedWidth += 22;

        // Minimale Fensterbreite absichern
        if (calculatedWidth < 350) calculatedWidth = 350;

        // Breite auf das Fenster übertragen
        this.Width = calculatedWidth;

        // Erzwingen, dass WPF das Layout sofort neu validiert, 
        // damit der inaktive Scrollbalken im Cache gar nicht erst hängen bleibt.
        this.UpdateLayout();
    }
    catch (Exception) { }
}

        private void TriggerAudioSessionScan()
        {
            _debounceScanTimer.Stop();
            _debounceScanTimer.Start();
        }

        public void LoadAudioSessions() => TriggerAudioSessionScan();

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

                    if (procName.Contains("discord", StringComparison.OrdinalIgnoreCase))
                    {
                        return "Discord";
                    }

                    if (procName.Contains("webhelper", StringComparison.OrdinalIgnoreCase) || 
                        procName.Equals("gameoverlayui", StringComparison.OrdinalIgnoreCase) ||
                        procName.Equals("steamservice", StringComparison.OrdinalIgnoreCase) ||
                        procName.StartsWith("steam", StringComparison.OrdinalIgnoreCase)) 
                        return "Steam";

                    if (procName.Equals("tslgame", StringComparison.OrdinalIgnoreCase) || 
                        procName.Equals("coherentui_host", StringComparison.OrdinalIgnoreCase) ||
                        procName.Equals("pubg", StringComparison.OrdinalIgnoreCase)) 
                        return "PUBG: Battlegrounds";

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
    try
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice currentDevice;

        if (_selectedDevice != null)
        {
            try
            {
                currentDevice = enumerator.GetDevice(_selectedDevice.ID);
            }
            catch
            {
                currentDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
        }
        else
        {
            currentDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        if (currentDevice == null) return;

        var sessionManager = currentDevice.AudioSessionManager;
        if (sessionManager == null) return;

        var sessions = sessionManager.Sessions;
        if (sessions == null) return;

        int count = sessions.Count;
        var activeSessions = new List<AudioSessionControl>();

        for (int i = 0; i < count; i++)
        {
            var session = sessions[i];
            if (session != null)
            {
                activeSessions.Add(session);
            }
        }

        var cardMap = Sessions.ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);
        var currentCardNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            currentCardNames.Add(appName);

            ImageSource? icon = null;
            if (appName.Equals("Systemsounds", StringComparison.OrdinalIgnoreCase) || 
                appName.Equals("System sounds", StringComparison.OrdinalIgnoreCase))
            {
                icon = _systemIcon;
            }
            else
            {
                icon = GetAppIcon(session);
            }

            if (cardMap.TryGetValue(appName, out var existingCard))
            {
                existingCard.AddSession(session);
            }
            else
            {
                if (appName.Equals("Discord", StringComparison.OrdinalIgnoreCase) && session.SimpleAudioVolume != null && AppSettings.Current.AutoVolumeDiscord)
                {
                    session.SimpleAudioVolume.Volume = AppSettings.Current.DiscordVolumeValue / 100f; 
                }

                var newCard = new AudioSessionModel(session, appName, icon)
                {
                    RequestMasterUnmute = () =>
                    {
                        if (IsMasterMuted) IsMasterMuted = false;
                    }
                };

                // Prüfen, ob der Name in den gespeicherten Favoriten enthalten ist
                if (AppSettings.Current.PinnedSessionIds != null)
                {
                    bool shouldBePinned = AppSettings.Current.PinnedSessionIds.Any(id => 
                        id.Equals(appName, StringComparison.OrdinalIgnoreCase) ||
                        (id.Contains("PUBG", StringComparison.OrdinalIgnoreCase) && appName.Contains("PUBG", StringComparison.OrdinalIgnoreCase)));
                    
                    if (shouldBePinned)
                    {
                        newCard.IsPinned = true;
                    }
                }

                Dispatcher.Invoke(() =>
                {
                    // 1. Ziel-Index basierend auf der gespeicherten namentlichen SessionOrder suchen
                    int targetIndex = -1;
                    if (AppSettings.Current.SessionOrder != null)
                    {
                        int savedRank = AppSettings.Current.SessionOrder.IndexOf(newCard.Name);
                        if (savedRank >= 0)
                        {
                            for (int i = 0; i < Sessions.Count; i++)
                            {
                                int otherRank = AppSettings.Current.SessionOrder.IndexOf(Sessions[i].Name);
                                if (otherRank < 0 || otherRank > savedRank)
                                {
                                    targetIndex = i;
                                    break;
                                }
                            }
                        }
                    }

                    // 2. An der richtigen Stelle einfügen
                    if (targetIndex >= 0 && targetIndex < Sessions.Count)
                    {
                        Sessions.Insert(targetIndex, newCard);
                    }
                    else if (newCard.IsPinned)
                    {
                        // Falls Favorit, aber noch nicht in SessionOrder: hinter bestehende Favoriten setzen
                        int insertIndex = 0;
                        while (insertIndex < Sessions.Count && Sessions[insertIndex].IsPinned)
                        {
                            insertIndex++;
                        }
                        Sessions.Insert(insertIndex, newCard);
                    }
                    else
                    {
                        Sessions.Add(newCard);
                    }

                    // 3. Neue App zur SessionOrder hinzufügen, OHNE inaktive Apps zu löschen
                    if (AppSettings.Current.SessionOrder == null)
                    {
                        AppSettings.Current.SessionOrder = new List<string>();
                    }
                    if (!AppSettings.Current.SessionOrder.Contains(newCard.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        AppSettings.Current.SessionOrder.Add(newCard.Name);
                    }

                    AppSettings.Save();
                });

                cardMap[appName] = newCard;
            }
        }

        var deadCards = Sessions.Where(s => !currentCardNames.Contains(s.Name)).ToList();
        foreach (var dead in deadCards)
        {
            Dispatcher.Invoke(() => Sessions.Remove(dead));
        }

        if (deadCards.Count > 0)
        {
            AppSettings.Save();
        }

        AdjustWindowWidthToContent();
    }
    catch (Exception)
    {
    }
}

        private ImageSource? GetAppIcon(AudioSessionControl session)
        {
            string appName = GetGenericAppName(session);
            if (appName.Equals("System sounds", StringComparison.OrdinalIgnoreCase) || 
                appName.Equals("Systemsounds", StringComparison.OrdinalIgnoreCase)) 
                return _systemIcon;

            try
            {
                int processId = (int)session.GetProcessID;
                if (processId > 0)
                {
                    string? path = GetProcessPathSafe(processId);
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        return ExtractIconFromFile(path);
                    }
                }
            }
            catch { }
            
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
                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = File.Exists("app.ico") ? new System.Drawing.Icon("app.ico") : System.Drawing.SystemIcons.Application,
                    Text = "Sound Mixer",
                    Visible = true
                };

                var contextMenu = new System.Windows.Forms.ContextMenuStrip();
                contextMenu.Items.Add("Öffnen", null, (s, e) => RestoreWindow());
                contextMenu.Items.Add("Einstellungen...", null, (s, e) => OpenSettingsWindow());
                contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
                contextMenu.Items.Add("Beenden", null, (s, e) => ShutdownApp());

                _notifyIcon.ContextMenuStrip = contextMenu;
                _notifyIcon.DoubleClick += (s, e) => RestoreWindow();
            }
            catch { }
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
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
        private void AppMuteButton_Unchecked(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: AudioSessionModel session }) session.IsMuted = false; }

        private void Window_MouseDown(object sender, MouseButtonEventArgs e) { if (e.ChangedButton == MouseButton.Left) DragMove(); }
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void CloseButton_Click(object sender, RoutedEventArgs e) => ShutdownApp();
        private void Window_Closing(object sender, CancelEventArgs e) 
        {
            ShutdownApp();
        }

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

            if (_autoScanTimer != null) _autoScanTimer.Stop();

            MasterVolume += (e.Delta > 0) ? 2 : -2;
            e.Handled = true;

            TriggerAudioSessionScan();
        }

        private void AppSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is Border border && border.DataContext is AudioSessionModel session)
            {
                if (_autoScanTimer != null) _autoScanTimer.Stop();

                float change = e.Delta > 0 ? 2f : -2f;
                session.Volume = Math.Clamp(session.Volume + change, 0f, 100f);
                
                e.Handled = true;
                TriggerAudioSessionScan();
            }
        }

        private void Slider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_autoScanTimer != null && _autoScanTimer.IsEnabled)
            {
                _autoScanTimer.Stop();
            }
            e.Handled = false;
        }

        private void Slider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_autoScanTimer != null && !_autoScanTimer.IsEnabled)
            {
                _autoScanTimer.Start();
                TriggerAudioSessionScan(); 
            }
            e.Handled = false;
        }

        private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }
        private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) { }

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
                    FindVisualParent<ToggleButton>(depObj) != null)
                {
                    return;
                }
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
                
                // Namentlich abspeichern
                AppSettings.Current.SessionOrder = Sessions.Select(s => s.Name).Distinct().ToList();
                AppSettings.Save();
            }
        }
    }
}

        private void UpdateVUMeters(object? sender, EventArgs e)
        {
            if (_selectedDevice != null)
            {
                try 
                { 
                    MasterPeak = _selectedDevice.AudioMeterInformation.MasterPeakValue * 100; 

                    bool winMute = _selectedDevice.AudioEndpointVolume.Mute;
                    if (_isMasterMutedCache != winMute)
                    {
                        _isMasterMutedCache = winMute;
                        OnPropertyChanged(nameof(IsMasterMuted));
                        OnPropertyChanged(nameof(IsMasterEnabled));

                        foreach (var session in Sessions)
                        {
                            session.IsMuted = winMute;
                        }
                    }

                    float winVol = _selectedDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100;
                    if (Math.Abs(_masterVolume - winVol) > 0.5f)
                    {
                        float oldMaster = _masterVolume;
                        _masterVolume = winVol;
                        OnPropertyChanged(nameof(MasterVolume));

                        if (AppSettings.Current.ProportionalMaster && oldMaster > 0)
                        {
                            float ratio = winVol / oldMaster;
                            foreach (var session in Sessions)
                            {
                                if (session.IsEnabled)
                                {
                                    float adjustedVol = Math.Clamp(session.Volume * ratio, 0f, 100f);
                                    session.Volume = adjustedVol;
                                }
                            }
                        }
                    }
                } 
                catch { }
            }

            for (int i = Sessions.Count - 1; i >= 0; i--)
            {
                try
                {
                    var session = Sessions[i];
                    session?.CheckExternalVolumeChanges();
                    session?.UpdatePeak();
                }
                catch { }
            }
        }

        public bool IsMasterEnabled => !IsMasterMuted;

        public bool IsMasterMuted
        {
            get => _selectedDevice?.AudioEndpointVolume?.Mute ?? _isMasterMutedCache;
            set
            {
                if (_selectedDevice != null)
                {
                    _selectedDevice.AudioEndpointVolume.Mute = value;
                    _isMasterMutedCache = value;
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
                    _selectedDevice = value;
                    OnPropertyChanged(nameof(SelectedDevice));
                    OnPropertyChanged(nameof(MasterVolume));
                    OnPropertyChanged(nameof(IsMasterMuted));
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

        private float _previousMasterVolume = 100f;

        public float MasterVolume
        {
            get => _selectedDevice != null ? _selectedDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100 : _masterVolume;
            set
            {
                if (_selectedDevice != null)
                {
                    float newMasterVol = Math.Clamp(value, 0f, 100f);
                    
                    if (AppSettings.Current.ProportionalMaster && _previousMasterVolume > 0)
                    {
                        float ratio = newMasterVol / _previousMasterVolume;

                        foreach (var session in Sessions)
                        {
                            if (session.IsEnabled)
                            {
                                float adjustedVol = Math.Clamp(session.Volume * ratio, 0f, 100f);
                                session.Volume = adjustedVol;
                            }
                        }
                    }

                    _previousMasterVolume = newMasterVol;
                    _selectedDevice.AudioEndpointVolume.MasterVolumeLevelScalar = newMasterVol / 100f;
                    OnPropertyChanged(nameof(MasterVolume));
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
                while (insertIndex < Sessions.Count && Sessions[insertIndex].IsPinned)
                {
                    insertIndex++;
                }
                Sessions.Insert(insertIndex, changedSession);

                // Zur Favoriten-Liste hinzufügen
                if (!AppSettings.Current.PinnedSessionIds.Contains(changedSession.Name, StringComparer.OrdinalIgnoreCase))
                {
                    AppSettings.Current.PinnedSessionIds.Add(changedSession.Name);
                }
            }
            else
            {
                Sessions.Add(changedSession);

                // Aus Favoriten-Liste entfernen
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
    // Fenster-Einstellungen sichern
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
    
    }
}