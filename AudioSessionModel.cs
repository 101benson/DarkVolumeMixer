using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace DarkVolumeMixer
{
    /// <summary>
    /// Eine Kachel = eine App (kann mehrere Windows-Audiosessions bündeln).
    /// Regeln dieser Klasse:
    ///  - Geschrieben wird an Windows nur über CoalescingWriter (immer der neueste Wert, strikt nacheinander).
    ///  - Von Windows gelesene Änderungen ("extern") werden nur vom ERSTEN Session-Eintrag übernommen;
    ///    alle weiteren Sessions der Kachel werden an den Kartenwert angeglichen.
    ///    (Früher haben sich mehrere Sessions mit unterschiedlicher Lautstärke gegenseitig überschrieben –
    ///    das war das Springen zwischen z. B. 100 % und 90 %.)
    /// </summary>
    public class AudioSessionModel : INotifyPropertyChanged, IDisposable
    {
        private const float ExternalChangeTolerance = 0.6f;
        private static readonly long InteractionGraceTicks = TimeSpan.FromMilliseconds(800).Ticks;

        private readonly object _sessionLock = new();
        private volatile AudioSessionControl[] _sessions = Array.Empty<AudioSessionControl>();

        private readonly CoalescingWriter<float> _volumeWriter;
        private readonly CoalescingWriter<bool> _muteWriter;

        private string _name;
        private ImageSource? _icon;
        private volatile float _volume = 100f;
        private volatile bool _isMuted;
        private float _peak;
        private bool _isPinned;
        private bool _isEnabled = true;
        private volatile bool _isDisposed;

        // Was Windows nach unserem letzten Schreiben / Lesen für die erste Session hält
        private volatile float _lastKnownDeviceVolume = 100f;
        private volatile bool _lastKnownDeviceMute;

        private long _lastUserInteractionTicks;
        private int _adoptPending;
        private volatile float _rawPeak;

        public Action? RequestMasterUnmute { get; set; }

        /// <summary>
        /// Wird ausgelöst, wenn sich Lautstärke/Mute unabhängig vom Master geändert hat
        /// (Nutzer oder externe Änderung). Das Hauptfenster verwirft dann seine Proportional-Basis.
        /// </summary>
        public event Action? VolumeEdited;

        public AudioSessionModel(IEnumerable<AudioSessionControl> initialSessions, string name, ImageSource? icon)
        {
            _name = name;
            _icon = icon;

            _volumeWriter = new CoalescingWriter<float>(WriteVolume);
            _muteWriter = new CoalescingWriter<bool>(WriteMute);

            _sessions = initialSessions?.ToArray() ?? Array.Empty<AudioSessionControl>();

            foreach (var session in _sessions)
            {
                try
                {
                    var sav = session.SimpleAudioVolume;
                    if (sav == null) continue;
                    _volume = sav.Volume * 100f;
                    _isMuted = sav.Mute;
                    _isEnabled = !_isMuted;
                    break;
                }
                catch { }
            }

            _lastKnownDeviceVolume = _volume;
            _lastKnownDeviceMute = _isMuted;
        }

        // ---------------------------------------------------------------- Properties

        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        public ImageSource? Icon
        {
            get => _icon;
            set => SetField(ref _icon, value);
        }

        public float Volume
        {
            get => _volume;
            set => SetVolume(value, fromUser: true);
        }

        /// <summary>Setzt die Lautstärke "von innen" (z. B. durch den Master) – zählt nicht als Nutzeränderung.</summary>
        public void SetVolumeProgrammatic(float value) => SetVolume(value, fromUser: false);

        private void SetVolume(float value, bool fromUser)
        {
            float clamped = Math.Clamp(value, 0f, 100f);
            if (Math.Abs(_volume - clamped) <= 0.05f) return;

            _volume = clamped;
            Interlocked.Exchange(ref _lastUserInteractionTicks, DateTime.UtcNow.Ticks);
            OnPropertyChanged(nameof(Volume));

            if (clamped > 0 && _isMuted) IsMuted = false;

            if (fromUser)
            {
                if (clamped > 0) RequestMasterUnmute?.Invoke();
                VolumeEdited?.Invoke();
            }

            _volumeWriter.Post(clamped / 100f);
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (_isMuted == value) return;

                _isMuted = value;
                IsEnabled = !value;
                Interlocked.Exchange(ref _lastUserInteractionTicks, DateTime.UtcNow.Ticks);
                OnPropertyChanged(nameof(IsMuted));

                _muteWriter.Post(value);

                if (!value) RequestMasterUnmute?.Invoke();
                VolumeEdited?.Invoke();
            }
        }

        public float Peak
        {
            get => _peak;
            set
            {
                if (Math.Abs(_peak - value) > 1.0f || (value == 0f && _peak != 0f))
                {
                    _peak = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (_isPinned != value)
                {
                    _isPinned = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set => SetField(ref _isEnabled, value);
        }

        // ---------------------------------------------------------------- Sessions

        /// <summary>Ersetzt die Session-Liste durch den frischen Scan (alte werden nicht angesammelt).</summary>
        public void SyncSessions(IEnumerable<AudioSessionControl> newSessions)
        {
            if (_isDisposed) return;
            var array = newSessions.ToArray();
            lock (_sessionLock) { _sessions = array; }
        }

        private void RemoveSession(AudioSessionControl dead)
        {
            lock (_sessionLock)
            {
                _sessions = _sessions.Where(s => !ReferenceEquals(s, dead)).ToArray();
            }
        }

        // ---------------------------------------------------------------- Schreiben nach Windows

        private void WriteVolume(float scalar)
        {
            foreach (var session in _sessions)
            {
                try
                {
                    var sav = session.SimpleAudioVolume;
                    if (sav != null) sav.Volume = scalar;
                }
                catch { RemoveSession(session); }
            }
            _lastKnownDeviceVolume = scalar * 100f;
        }

        private void WriteMute(bool mute)
        {
            foreach (var session in _sessions)
            {
                try
                {
                    var sav = session.SimpleAudioVolume;
                    if (sav != null) sav.Mute = mute;
                }
                catch { RemoveSession(session); }
            }
            _lastKnownDeviceMute = mute;
        }

        // ---------------------------------------------------------------- Lesen aus Windows (Hintergrund-Thread)

        private bool IsUserInteracting() =>
            DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastUserInteractionTicks) < InteractionGraceTicks;

        /// <summary>Pegel messen (~30x pro Sekunde, nur Speicher, keine UI-Zugriffe).</summary>
        /// <summary>Aktuell gemessener Pegel dieser App (0..100, bereits auf Reglerstellung begrenzt).</summary>
        public float LevelPercent => _rawPeak;

        public void PollPeak()
        {
            if (_isDisposed) return;

            float volume = _volume;
            if (_isMuted || volume <= 0.01f)
            {
                _rawPeak = 0f;
                return;
            }

            float max = 0f;
            foreach (var session in _sessions)
            {
                try
                {
                    var meter = session.AudioMeterInformation;
                    if (meter == null) continue;
                    float p = meter.MasterPeakValue;
                    if (p > max) max = p;
                }
                catch { RemoveSession(session); }
            }

            // Balken reicht nie höher als der Regler: Signalstärke (0..100 %) x Reglerstellung
            _rawPeak = MeterMath.ToPercent(max) * volume / 100f;
        }

        /// <summary>Prüft, ob Windows/die App Lautstärke oder Mute selbst geändert hat (~2x pro Sekunde).</summary>
        public void PollExternalState()
        {
            if (_isDisposed) return;

            var snapshot = _sessions;
            if (snapshot.Length == 0) return;
            if (_volumeWriter.IsBusy || _muteWriter.IsBusy || IsUserInteracting()) return;

            // Erste lesbare Session = maßgeblich
            int primaryIndex = -1;
            float curVol = 0f;
            bool curMute = false;

            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    var sav = snapshot[i].SimpleAudioVolume;
                    if (sav == null) continue;
                    curVol = sav.Volume * 100f;
                    curMute = sav.Mute;
                    primaryIndex = i;
                    break;
                }
                catch { RemoveSession(snapshot[i]); }
            }

            if (primaryIndex < 0) return;

            bool volChanged = Math.Abs(curVol - _lastKnownDeviceVolume) > ExternalChangeTolerance;
            bool muteChanged = curMute != _lastKnownDeviceMute;

            if (volChanged || muteChanged)
            {
                // Übernahme passiert im UI-Thread, damit sie sich nie mit Slider-Eingaben überschneidet.
                if (Interlocked.Exchange(ref _adoptPending, 1) == 0)
                {
                    PostToUi(() => AdoptExternalState(curVol, curMute));
                }
                return;
            }

            // Keine externe Änderung: weitere Sessions der Kachel an den Kartenwert angleichen.
            for (int i = primaryIndex + 1; i < snapshot.Length; i++)
            {
                try
                {
                    var sav = snapshot[i].SimpleAudioVolume;
                    if (sav == null) continue;
                    if (Math.Abs(sav.Volume * 100f - _volume) > ExternalChangeTolerance) sav.Volume = _volume / 100f;
                    if (sav.Mute != _isMuted) sav.Mute = _isMuted;
                }
                catch { RemoveSession(snapshot[i]); }
            }
        }

        // ---------------------------------------------------------------- UI-Thread

        private void AdoptExternalState(float vol, bool mute)
        {
            Interlocked.Exchange(ref _adoptPending, 0);

            if (_isDisposed || IsUserInteracting() || _volumeWriter.IsBusy || _muteWriter.IsBusy) return;

            bool changed = false;

            if (Math.Abs(vol - _volume) > ExternalChangeTolerance)
            {
                _volume = vol;
                OnPropertyChanged(nameof(Volume));
                changed = true;
            }
            _lastKnownDeviceVolume = vol;

            if (mute != _isMuted)
            {
                _isMuted = mute;
                IsEnabled = !mute;
                OnPropertyChanged(nameof(IsMuted));
                changed = true;
            }
            _lastKnownDeviceMute = mute;

            if (changed) VolumeEdited?.Invoke();
        }

        /// <summary>Glättet den Pegel für die Anzeige (UI-Thread, pro Frame).</summary>
        public void RenderTickPeak()
        {
            float target = _rawPeak;
            if (target >= _peak)
            {
                Peak = target;
            }
            else
            {
                Peak = Math.Max(0f, _peak - ((_peak - target) * 0.45f) - 1.5f);
            }
        }

        private static void PostToUi(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(DispatcherPriority.Background, action);
        }

        // ---------------------------------------------------------------- Aufräumen / INPC

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            lock (_sessionLock) { _sessions = Array.Empty<AudioSessionControl>(); }
            RequestMasterUnmute = null;
            VolumeEdited = null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
