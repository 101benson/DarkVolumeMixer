using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using NAudio.CoreAudioApi;

namespace DarkVolumeMixer
{
    public class AudioSessionModel : INotifyPropertyChanged, IDisposable
    {
        private readonly object _sessionLock = new();
        private readonly List<AudioSessionControl> _sessions = new();

        private string _name = string.Empty;
        private ImageSource? _icon;
        private float _volume = 100f;
        private bool _isMuted;
        private float _peak;
        private bool _isPinned;
        private bool _isEnabled = true;
        private bool _isDisposed;

        private long _lastUserInteractionTicks;
        private int _isApplyingVolume;
        private float _pendingVolume = -1f;

        public Action? RequestMasterUnmute { get; set; }

        public AudioSessionModel(AudioSessionControl initialSession, string name, ImageSource? icon)
        {
            _name = name;
            _icon = icon;

            if (initialSession != null)
            {
                _sessions.Add(initialSession);

                try
                {
                    var sav = initialSession.SimpleAudioVolume;
                    if (sav != null)
                    {
                        _volume = sav.Volume * 100f;
                        _isMuted = sav.Mute;
                        _isEnabled = !_isMuted;
                    }
                }
                catch
                {
                    _volume = 100f;
                    _isMuted = false;
                    _isEnabled = true;
                }
            }
        }

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
            set
            {
                float clamped = Math.Clamp(value, 0f, 100f);
                if (Math.Abs(_volume - clamped) > 0.01f)
                {
                    _volume = clamped;
                    Interlocked.Exchange(ref _lastUserInteractionTicks, DateTime.UtcNow.Ticks);
                    OnPropertyChanged();

                    QueueVolumeApply(clamped / 100f);
                }
            }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (_isMuted != value)
                {
                    _isMuted = value;
                    IsEnabled = !value;
                    Interlocked.Exchange(ref _lastUserInteractionTicks, DateTime.UtcNow.Ticks);
                    OnPropertyChanged();
                    QueueMuteApply(value);

                    // Nur wenn der Nutzer diese App aktiv entmutet, wird der Master mit entmutet
                    if (!value)
                    {
                        RequestMasterUnmute?.Invoke();
                    }
                }
            }
        }

        public float Peak
        {
            get => _peak;
            set
            {
                if (Math.Abs(_peak - value) > 0.3f || (value == 0 && _peak != 0))
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

        public void AddSession(AudioSessionControl session)
        {
            if (session == null || _isDisposed) return;

            lock (_sessionLock)
            {
                for (int i = 0; i < _sessions.Count; i++)
                {
                    if (ReferenceEquals(_sessions[i], session)) return;
                }

                _sessions.Add(session);

                try
                {
                    var sav = session.SimpleAudioVolume;
                    if (sav != null)
                    {
                        sav.Volume = _volume / 100f;
                        sav.Mute = _isMuted;
                    }
                }
                catch { }
            }
        }

        public void CheckExternalVolumeChanges()
        {
            if (_isDisposed) return;

            long lastInteraction = Interlocked.Read(ref _lastUserInteractionTicks);
            if (DateTime.UtcNow.Ticks - lastInteraction < TimeSpan.FromMilliseconds(800).Ticks)
            {
                return;
            }

            lock (_sessionLock)
            {
                for (int i = _sessions.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        var sav = _sessions[i].SimpleAudioVolume;
                        if (sav != null)
                        {
                            float currentVol = sav.Volume * 100f;
                            bool currentMute = sav.Mute;

                            if (Math.Abs(_volume - currentVol) > 1.5f)
                            {
                                _volume = currentVol;
                                DispatcherRun(() => OnPropertyChanged(nameof(Volume)));
                            }

                            if (_isMuted != currentMute)
                            {
                                _isMuted = currentMute;
                                _isEnabled = !currentMute;
                                DispatcherRun(() =>
                                {
                                    OnPropertyChanged(nameof(IsMuted));
                                    OnPropertyChanged(nameof(IsEnabled));
                                });
                            }
                            return;
                        }
                    }
                    catch
                    {
                        _sessions.RemoveAt(i);
                    }
                }
            }
        }

        public void UpdatePeak()
        {
            if (_isDisposed || _isMuted || _volume <= 0.01f)
            {
                Peak = Math.Max(0f, _peak - 18f);
                return;
            }

            float maxPeak = 0f;

            lock (_sessionLock)
            {
                for (int i = _sessions.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        var meter = _sessions[i].AudioMeterInformation;
                        if (meter != null)
                        {
                            float rawVal = meter.MasterPeakValue * 100f;
                            float scaledPeak = rawVal * (_volume / 100f);
                            if (scaledPeak > maxPeak) maxPeak = scaledPeak;
                        }
                    }
                    catch
                    {
                        _sessions.RemoveAt(i);
                    }
                }
            }

            float target = Math.Min(maxPeak, _volume);

            if (target >= _peak)
                Peak = target;
            else
                Peak = Math.Max(0f, _peak - ((_peak - target) * 0.35f) - 1.5f);
        }

        private void QueueVolumeApply(float volScalar)
        {
            _pendingVolume = volScalar;

            if (Interlocked.Exchange(ref _isApplyingVolume, 1) == 1) return;

            Task.Run(() =>
            {
                try
                {
                    while (true)
                    {
                        float current = _pendingVolume;
                        if (current < 0) break;
                        _pendingVolume = -1f;

                        lock (_sessionLock)
                        {
                            for (int i = _sessions.Count - 1; i >= 0; i--)
                            {
                                try
                                {
                                    var sav = _sessions[i].SimpleAudioVolume;
                                    if (sav != null) sav.Volume = current;
                                }
                                catch
                                {
                                    _sessions.RemoveAt(i);
                                }
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    Interlocked.Exchange(ref _isApplyingVolume, 0);
                }
            });
        }

        private void QueueMuteApply(bool mute)
        {
            Task.Run(() =>
            {
                lock (_sessionLock)
                {
                    for (int i = _sessions.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            var sav = _sessions[i].SimpleAudioVolume;
                            if (sav != null) sav.Mute = mute;
                        }
                        catch
                        {
                            _sessions.RemoveAt(i);
                        }
                    }
                }
            });
        }

        private static void DispatcherRun(Action action)
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            lock (_sessionLock)
            {
                _sessions.Clear();
            }

            RequestMasterUnmute = null;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            DispatcherRun(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}