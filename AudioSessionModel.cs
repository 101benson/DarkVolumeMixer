using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Media;
using NAudio.CoreAudioApi;

namespace DarkVolumeMixer
{
    public class AudioSessionModel : INotifyPropertyChanged
    {
        private List<AudioSessionControl> _sessions;
        private ImageSource? _icon;
        private string _name;
        private float _volume;
        private float _volumeCache;
        private bool _isMuted;
        private bool _muteCache;
        private bool _isPinned;
        private bool _isEnabled = true;
        private float _peakValue;

        public List<AudioSessionControl> Sessions => _sessions;

        public string SessionId => _name;

        public ImageSource? Icon
        {
            get => _icon;
            set { _icon = value; OnPropertyChanged(nameof(Icon)); }
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        public float Volume
        {
            get => _volume;
            set
            {
                float clamped = Math.Clamp(value, 0f, 100f);
                if (_volume != clamped)
                {
                    _volume = clamped;
                    _volumeCache = clamped;
                    OnPropertyChanged(nameof(Volume));
                    OnPropertyChanged(nameof(IsNotMuted));

                    foreach (var s in _sessions)
                    {
                        if (s.SimpleAudioVolume != null)
                        {
                            s.SimpleAudioVolume.Volume = clamped / 100f;
                        }
                    }
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
                    _muteCache = value;
                    OnPropertyChanged(nameof(IsMuted));
                    OnPropertyChanged(nameof(IsNotMuted));
                    OnPropertyChanged(nameof(IsEnabled));

                    foreach (var s in _sessions)
                    {
                        if (s.SimpleAudioVolume != null)
                        {
                            s.SimpleAudioVolume.Mute = value;
                        }
                    }

                    if (!value)
                    {
                        RequestMasterUnmute?.Invoke();
                    }
                }
            }
        }

        public bool IsNotMuted => !_isMuted;

        private bool _isUpdatingPin = false;

        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (_isUpdatingPin) return;

                if (_isPinned != value)
                {
                    _isUpdatingPin = true;
                    try
                    {
                        _isPinned = value;
                        OnPropertyChanged(nameof(IsPinned));

                        // Direkt im Hauptfenster die Anordnung aktualisieren
                        if (System.Windows.Application.Current.MainWindow is MainWindow mainWindow)
                        {
                            mainWindow.ReorderSessionsAfterPin(this);
                        }
                    }
                    finally
                    {
                        _isUpdatingPin = false;
                    }
                }
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled && !_isMuted;
            set
            {
                if (_isEnabled != value)
                {
                    _isEnabled = value;
                    OnPropertyChanged(nameof(IsEnabled));
                }
            }
        }

        public float PeakValue
        {
            get => _peakValue;
            set
            {
                if (_peakValue != value)
                {
                    _peakValue = value;
                    OnPropertyChanged(nameof(PeakValue));
                }
            }
        }

        public Action? RequestMasterUnmute { get; set; }

        public AudioSessionModel(AudioSessionControl session, string name, ImageSource? icon)
        {
            _sessions = new List<AudioSessionControl> { session };
            _icon = icon;
            _name = name;

            if (session.SimpleAudioVolume != null)
            {
                _volume = Math.Clamp(session.SimpleAudioVolume.Volume * 100f, 0f, 100f);
                _volumeCache = _volume;

                _isMuted = session.SimpleAudioVolume.Mute;
                _muteCache = _isMuted;
            }
        }

        public void AddSession(AudioSessionControl session)
        {
            if (!_sessions.Contains(session))
            {
                _sessions.Add(session);
                if (session.SimpleAudioVolume != null)
                {
                    session.SimpleAudioVolume.Volume = _volume / 100f;
                    session.SimpleAudioVolume.Mute = _isMuted;
                }
            }
        }

        public void CheckExternalVolumeChanges()
        {
            if (_sessions == null || _sessions.Count == 0) return;
            var primarySession = _sessions[0];
            if (primarySession?.SimpleAudioVolume == null) return;

            float currentVol = primarySession.SimpleAudioVolume.Volume * 100f;
            if (Math.Abs(_volumeCache - currentVol) > 0.5f)
            {
                _volumeCache = currentVol;
                _volume = currentVol;
                OnPropertyChanged(nameof(Volume));
            }

            bool currentMute = primarySession.SimpleAudioVolume.Mute;
            if (_muteCache != currentMute)
            {
                _muteCache = currentMute;
                _isMuted = currentMute;
                OnPropertyChanged(nameof(IsMuted));
                OnPropertyChanged(nameof(IsNotMuted));
                OnPropertyChanged(nameof(IsEnabled));
            }
        }

        public void UpdatePeak()
        {
            try
            {
                if (_sessions == null || _sessions.Count == 0)
                {
                    PeakValue = 0f;
                    return;
                }

                float maxPeak = 0f;
                foreach (var session in _sessions)
                {
                    if (session.AudioMeterInformation != null)
                    {
                        float peak = session.AudioMeterInformation.MasterPeakValue * 100f;
                        if (peak > maxPeak) maxPeak = peak;
                    }
                }

                PeakValue = Math.Clamp(maxPeak * (_volume / 100f), 0f, 100f);
            }
            catch
            {
                PeakValue = 0f;
            }
        }

        public void Dispose()
        {
            _sessions.Clear();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}