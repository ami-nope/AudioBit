using System.Windows.Media;
using AudioBit.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioBit.App.ViewModels;

public sealed class AppAudioViewModel : ObservableObject
{
    private static readonly SolidColorBrush DiscordAccentBrush = CreateBrush("#5B74FF");
    private static readonly SolidColorBrush BrowserAccentBrush = CreateBrush("#B7BDD0");
    private static readonly SolidColorBrush SpotifyAccentBrush = CreateBrush("#45D96A");
    private static readonly SolidColorBrush GameAccentBrush = CreateBrush("#F2C35D");
    private static readonly SolidColorBrush SystemAccentBrush = CreateBrush("#F28742");
    private static readonly SolidColorBrush[] FallbackAccentBrushes =
    [
        CreateBrush("#6A86FF"),
        CreateBrush("#4BD58A"),
        CreateBrush("#F2B652"),
        CreateBrush("#FF8B6F"),
        CreateBrush("#8C79FF"),
        CreateBrush("#61CBE8"),
    ];

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
    private Brush _accentBrush = FallbackAccentBrushes[0];

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

    public Brush AccentBrush
    {
        get => _accentBrush;
        private set => SetProperty(ref _accentBrush, value);
    }

    public bool IsActive => Peak > AppAudioModel.SilenceThreshold;

    public bool HasRecentActivityText => !IsActive;

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

        _isApplyingSnapshot = false;
        OnPropertyChanged(nameof(IsActive));
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

        if (normalized.Contains("chrome", StringComparison.Ordinal)
            || normalized.Contains("edge", StringComparison.Ordinal)
            || normalized.Contains("firefox", StringComparison.Ordinal)
            || normalized.Contains("browser", StringComparison.Ordinal))
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

    private static SolidColorBrush CreateBrush(string hexColor)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hexColor)!);
        brush.Freeze();
        return brush;
    }
}
