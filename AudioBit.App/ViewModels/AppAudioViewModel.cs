using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using AudioBit.Core;

namespace AudioBit.App.ViewModels;

public sealed class AppAudioViewModel : ObservableObject
{
    private readonly Action<int, float> _setVolume;
    private readonly Action<int, bool> _setMute;

    private bool _isApplyingSnapshot;
    private int _processId;
    private string _appName = string.Empty;
    private ImageSource? _icon;
    private double _volume = 1.0;
    private double _peak;
    private bool _isMuted;
    private DateTime _lastAudioTime = DateTime.UtcNow;
    private double _opacity = 1.0;

    public AppAudioViewModel(Action<int, float> setVolume, Action<int, bool> setMute)
    {
        _setVolume = setVolume;
        _setMute = setMute;
    }

    public int ProcessId
    {
        get => _processId;
        private set => SetProperty(ref _processId, value);
    }

    public string AppName
    {
        get => _appName;
        private set => SetProperty(ref _appName, value);
    }

    public ImageSource? Icon
    {
        get => _icon;
        private set => SetProperty(ref _icon, value);
    }

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (!SetProperty(ref _volume, clamped))
            {
                return;
            }

            OnPropertyChanged(nameof(VolumePercentText));

            if (_isApplyingSnapshot)
            {
                return;
            }

            _setVolume(ProcessId, (float)clamped);
        }
    }

    public double Peak
    {
        get => _peak;
        private set => SetProperty(ref _peak, value);
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (!SetProperty(ref _isMuted, value))
            {
                return;
            }

            if (_isApplyingSnapshot)
            {
                return;
            }

            _setMute(ProcessId, value);
        }
    }

    public DateTime LastAudioTime
    {
        get => _lastAudioTime;
        private set => SetProperty(ref _lastAudioTime, value);
    }

    public double Opacity
    {
        get => _opacity;
        private set => SetProperty(ref _opacity, value);
    }

    public bool IsActive => Peak > AppAudioModel.SilenceThreshold;

    public string VolumePercentText => $"{Math.Round(Volume * 100):0}%";

    public void Apply(AppAudioModel model)
    {
        _isApplyingSnapshot = true;

        ProcessId = model.ProcessId;
        AppName = model.AppName;
        Icon = model.Icon;
        Volume = model.Volume;
        Peak = model.Peak;
        IsMuted = model.IsMuted;
        LastAudioTime = model.LastAudioTime;
        Opacity = model.Opacity;

        _isApplyingSnapshot = false;
        OnPropertyChanged(nameof(IsActive));
    }

    public void SetMutedVisualState(bool isMuted)
    {
        _isApplyingSnapshot = true;
        IsMuted = isMuted;
        _isApplyingSnapshot = false;
    }
}
