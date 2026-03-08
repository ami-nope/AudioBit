using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using AudioBit.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioBit.App.ViewModels;

public sealed class AppAudioViewModel : ObservableObject
{
    private static readonly Brush DiscordAccentBrush = CreateSolidBrush("#5865F2");
    private static readonly Brush SpotifyAccentBrush = CreateSolidBrush("#1ED760");
    private static readonly Brush BraveAccentBrush = CreateSolidBrush("#FB542B");
    private static readonly Brush EdgeAccentBrush = CreateSolidBrush("#1793FF");
    private static readonly Brush ValorantAccentBrush = CreateSolidBrush("#FF4655");
    private static readonly Brush ChromeAccentBrush = CreateLinearGradientBrush(
        ("#4285F4", 0.00),
        ("#EA4335", 0.33),
        ("#FBBC05", 0.66),
        ("#34A853", 1.00));
    private static readonly Brush FirefoxAccentBrush = CreateLinearGradientBrush(
        ("#FF7139", 0.00),
        ("#FF4F5E", 0.42),
        ("#C43AFF", 1.00));
    private static readonly Brush BrowserAccentBrush = CreateSolidBrush("#4DA3FF");
    private static readonly Brush GameAccentBrush = CreateSolidBrush("#F2C35D");
    private static readonly Brush SystemAccentBrush = CreateSolidBrush("#F28742");
    private static readonly Brush[] FallbackAccentBrushes =
    [
        CreateSolidBrush("#6A86FF"),
        CreateSolidBrush("#4BD58A"),
        CreateSolidBrush("#F2B652"),
        CreateSolidBrush("#FF8B6F"),
        CreateSolidBrush("#8C79FF"),
        CreateSolidBrush("#61CBE8"),
    ];

    private readonly Action<int, float> _setVolume;
    private readonly Action<int, bool> _setMute;
    private readonly Action<int, string?> _setPreferredPlaybackDevice;
    private readonly Action<int, string?> _setPreferredCaptureDevice;

    private bool _isApplyingSnapshot;
    private int _processId;
    private string _appName = string.Empty;
    private ImageSource? _icon;
    private double _volume = 1.0;
    private double _peak;
    private bool _isMuted;
    private DateTime _lastAudioTime = DateTime.UtcNow;
    private double _opacity = 1.0;
    private Brush _accentBrush = FallbackAccentBrushes[0];
    private string _selectedPlaybackDeviceId = string.Empty;
    private string _selectedCaptureDeviceId = string.Empty;

    public AppAudioViewModel(
        ReadOnlyObservableCollection<AudioDeviceOptionModel> playbackDevices,
        ReadOnlyObservableCollection<AudioDeviceOptionModel> captureDevices,
        Action<int, float> setVolume,
        Action<int, bool> setMute,
        Action<int, string?> setPreferredPlaybackDevice,
        Action<int, string?> setPreferredCaptureDevice)
    {
        PlaybackDevices = playbackDevices;
        CaptureDevices = captureDevices;
        _setVolume = setVolume;
        _setMute = setMute;
        _setPreferredPlaybackDevice = setPreferredPlaybackDevice;
        _setPreferredCaptureDevice = setPreferredCaptureDevice;
    }

    public ReadOnlyObservableCollection<AudioDeviceOptionModel> PlaybackDevices { get; }

    public ReadOnlyObservableCollection<AudioDeviceOptionModel> CaptureDevices { get; }

    public int ProcessId
    {
        get => _processId;
        private set
        {
            if (!SetProperty(ref _processId, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanRouteDevices));
        }
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
            OnPropertyChanged(nameof(DisplayPeak));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(ShouldShowMeter));
            OnPropertyChanged(nameof(HasRecentActivityText));

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
        private set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (!SetProperty(ref _peak, clamped))
            {
                return;
            }

            OnPropertyChanged(nameof(DisplayPeak));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(ShouldShowMeter));
            OnPropertyChanged(nameof(HasRecentActivityText));
        }
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

            OnPropertyChanged(nameof(DisplayPeak));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(ShouldShowMeter));
            OnPropertyChanged(nameof(HasRecentActivityText));

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

    public Brush AccentBrush
    {
        get => _accentBrush;
        private set => SetProperty(ref _accentBrush, value);
    }

    public string SelectedPlaybackDeviceId
    {
        get => _selectedPlaybackDeviceId;
        set
        {
            var normalized = NormalizeSelectionId(value);
            if (!SetProperty(ref _selectedPlaybackDeviceId, normalized))
            {
                return;
            }

            if (_isApplyingSnapshot)
            {
                return;
            }

            _setPreferredPlaybackDevice(ProcessId, normalized);
        }
    }

    public string SelectedCaptureDeviceId
    {
        get => _selectedCaptureDeviceId;
        set
        {
            var normalized = NormalizeSelectionId(value);
            if (!SetProperty(ref _selectedCaptureDeviceId, normalized))
            {
                return;
            }

            if (_isApplyingSnapshot)
            {
                return;
            }

            _setPreferredCaptureDevice(ProcessId, normalized);
        }
    }

    public double DisplayPeak => IsMuted ? 0.0 : Math.Clamp(Peak * Volume, 0.0, 1.0);

    public bool IsActive => DisplayPeak > AppAudioModel.SilenceThreshold;

    public bool ShouldShowMeter => IsActive || DateTime.UtcNow - LastAudioTime <= AppAudioModel.RecentlyActiveHold;

    public bool HasRecentActivityText => !ShouldShowMeter;

    public bool CanRouteDevices => ProcessId > 0;

    public string RecentActivityText => $"last heard {FormatRelativeTime(DateTime.UtcNow - LastAudioTime)}";

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
        AccentBrush = ResolveAccentBrush(model.AppName);
        SelectedPlaybackDeviceId = model.PreferredRenderDeviceId;
        SelectedCaptureDeviceId = model.PreferredCaptureDeviceId;

        _isApplyingSnapshot = false;
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ShouldShowMeter));
        OnPropertyChanged(nameof(HasRecentActivityText));
        OnPropertyChanged(nameof(RecentActivityText));
    }

    public void SetMutedVisualState(bool isMuted)
    {
        _isApplyingSnapshot = true;
        IsMuted = isMuted;
        _isApplyingSnapshot = false;
    }

    private static Brush ResolveAccentBrush(string appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return FallbackAccentBrushes[0];
        }

        var normalized = appName.Trim().ToLowerInvariant();
        if (normalized.Contains("discord", StringComparison.Ordinal))
        {
            return DiscordAccentBrush;
        }

        if (normalized.Contains("spotify", StringComparison.Ordinal))
        {
            return SpotifyAccentBrush;
        }

        if (normalized.Contains("brave", StringComparison.Ordinal))
        {
            return BraveAccentBrush;
        }

        if (normalized.Contains("chrome", StringComparison.Ordinal))
        {
            return ChromeAccentBrush;
        }

        if (normalized.Contains("edge", StringComparison.Ordinal))
        {
            return EdgeAccentBrush;
        }

        if (normalized.Contains("firefox", StringComparison.Ordinal))
        {
            return FirefoxAccentBrush;
        }

        if (normalized.Contains("valorant", StringComparison.Ordinal))
        {
            return ValorantAccentBrush;
        }

        if (normalized.Contains("browser", StringComparison.Ordinal)
            || normalized.Contains("arc", StringComparison.Ordinal)
            || normalized.Contains("opera", StringComparison.Ordinal))
        {
            return BrowserAccentBrush;
        }

        if (normalized.Contains("cyberpunk", StringComparison.Ordinal)
            || normalized.Contains("steam", StringComparison.Ordinal)
            || normalized.Contains("game", StringComparison.Ordinal))
        {
            return GameAccentBrush;
        }

        if (normalized.Contains("system", StringComparison.Ordinal))
        {
            return SystemAccentBrush;
        }

        var hash = 17;
        foreach (var character in normalized)
        {
            hash = (hash * 31) + character;
        }

        return FallbackAccentBrushes[Math.Abs(hash) % FallbackAccentBrushes.Length];
    }

    private static string FormatRelativeTime(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed < TimeSpan.FromSeconds(10))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return $"{Math.Max(1, (int)elapsed.TotalSeconds)} secs ago";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)elapsed.TotalMinutes);
            return minutes == 1 ? "1 min ago" : $"{minutes} mins ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = Math.Max(1, (int)elapsed.TotalHours);
            return hours == 1 ? "1 hr ago" : $"{hours} hrs ago";
        }

        var days = Math.Max(1, (int)elapsed.TotalDays);
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }

    private static string NormalizeSelectionId(string? deviceId)
    {
        return string.IsNullOrWhiteSpace(deviceId) ? string.Empty : deviceId;
    }

    private static SolidColorBrush CreateSolidBrush(string hexColor)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor)!);
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateLinearGradientBrush(params (string ColorHex, double Offset)[] stops)
    {
        var gradientStops = new GradientStopCollection();
        foreach (var (colorHex, offset) in stops)
        {
            gradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(colorHex)!, offset));
        }

        gradientStops.Freeze();

        var brush = new LinearGradientBrush(gradientStops, new Point(0, 0.5), new Point(1, 0.5));
        brush.Freeze();
        return brush;
    }
}
