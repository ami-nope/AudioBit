using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AudioBit.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AudioBit.App.ViewModels;

public sealed class AppAudioViewModel : ObservableObject
{
    private static readonly TimeSpan LocalVolumeSyncHold = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan RemoteVolumeAnimationDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan RemoteVolumeAnimationFrameInterval = TimeSpan.FromMilliseconds(16);
    private const double RemoteVolumeAnimationThreshold = 0.012;
    private const float VolumeDispatchEpsilon = 0.0005f;

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
    private readonly DispatcherTimer _remoteVolumeAnimationTimer;

    private bool _isApplyingSnapshot;
    private bool _isApplyingRemoteVolumeAnimation;
    private int _isVolumeDispatching;
    private int _isPlaybackRouteDispatching;
    private int _isCaptureRouteDispatching;
    private int _processId;
    private string _appName = string.Empty;
    private ImageSource? _icon;
    private double _volume = 1.0;
    private float _queuedVolume = 1.0f;
    private string _queuedPlaybackDeviceId = string.Empty;
    private string _queuedCaptureDeviceId = string.Empty;
    private DateTime _lastLocalVolumeUpdateUtc = DateTime.MinValue;
    private double _peak;
    private bool _isMuted;
    private DateTime _lastAudioTime = DateTime.UtcNow;
    private double _opacity = 1.0;
    private Brush _accentBrush = FallbackAccentBrushes[0];
    private string _selectedPlaybackDeviceId = string.Empty;
    private string _selectedCaptureDeviceId = string.Empty;
    private DateTime _remoteVolumeAnimationStartedUtc;
    private double _remoteVolumeAnimationStart;
    private double _remoteVolumeAnimationTarget;

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
        _remoteVolumeAnimationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RemoteVolumeAnimationFrameInterval,
        };
        _remoteVolumeAnimationTimer.Tick += RemoteVolumeAnimationTimerOnTick;
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

            if (_isApplyingSnapshot || _isApplyingRemoteVolumeAnimation)
            {
                return;
            }

            if (_remoteVolumeAnimationTimer.IsEnabled)
            {
                StopRemoteVolumeAnimation();
            }

            _lastLocalVolumeUpdateUtc = DateTime.UtcNow;
            QueueVolumeUpdate((float)clamped);
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
            var normalized = NormalizeSelectionIdToKnownOption(value, PlaybackDevices);
            if (!SetProperty(ref _selectedPlaybackDeviceId, normalized))
            {
                return;
            }

            if (_isApplyingSnapshot)
            {
                return;
            }

            QueuePlaybackRouteUpdate(normalized);
        }
    }

    public string SelectedCaptureDeviceId
    {
        get => _selectedCaptureDeviceId;
        set
        {
            var normalized = NormalizeSelectionIdToKnownOption(value, CaptureDevices);
            if (!SetProperty(ref _selectedCaptureDeviceId, normalized))
            {
                return;
            }

            if (_isApplyingSnapshot)
            {
                return;
            }

            QueueCaptureRouteUpdate(normalized);
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
        if (ShouldApplySnapshotVolume(model.Volume))
        {
            ApplySnapshotVolume(model.Volume);
        }

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

    private bool ShouldApplySnapshotVolume(double snapshotVolume)
    {
        var elapsed = DateTime.UtcNow - _lastLocalVolumeUpdateUtc;
        if (elapsed >= LocalVolumeSyncHold)
        {
            return true;
        }

        return Math.Abs(_volume - snapshotVolume) <= 0.02;
    }

    private void ApplySnapshotVolume(double snapshotVolume)
    {
        var clamped = Math.Clamp(snapshotVolume, 0.0, 1.0);
        if (Math.Abs(_volume - clamped) < RemoteVolumeAnimationThreshold)
        {
            StopRemoteVolumeAnimation();
            Volume = clamped;
            return;
        }

        _remoteVolumeAnimationStart = _volume;
        _remoteVolumeAnimationTarget = clamped;
        _remoteVolumeAnimationStartedUtc = DateTime.UtcNow;

        if (!_remoteVolumeAnimationTimer.IsEnabled)
        {
            _remoteVolumeAnimationTimer.Start();
        }
    }

    private void RemoteVolumeAnimationTimerOnTick(object? sender, EventArgs e)
    {
        var duration = RemoteVolumeAnimationDuration.TotalMilliseconds;
        if (duration <= 0.0)
        {
            CompleteRemoteVolumeAnimation();
            return;
        }

        var elapsedMilliseconds = (DateTime.UtcNow - _remoteVolumeAnimationStartedUtc).TotalMilliseconds;
        var progress = Math.Clamp(elapsedMilliseconds / duration, 0.0, 1.0);
        var eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
        var nextVolume = _remoteVolumeAnimationStart + ((_remoteVolumeAnimationTarget - _remoteVolumeAnimationStart) * eased);

        _isApplyingRemoteVolumeAnimation = true;
        try
        {
            Volume = nextVolume;
        }
        finally
        {
            _isApplyingRemoteVolumeAnimation = false;
        }

        if (progress >= 1.0 || Math.Abs(_remoteVolumeAnimationTarget - _volume) < 0.0005)
        {
            CompleteRemoteVolumeAnimation();
        }
    }

    private void CompleteRemoteVolumeAnimation()
    {
        _isApplyingRemoteVolumeAnimation = true;
        try
        {
            Volume = _remoteVolumeAnimationTarget;
        }
        finally
        {
            _isApplyingRemoteVolumeAnimation = false;
        }

        StopRemoteVolumeAnimation();
    }

    private void StopRemoteVolumeAnimation()
    {
        if (!_remoteVolumeAnimationTimer.IsEnabled)
        {
            return;
        }

        _remoteVolumeAnimationTimer.Stop();
    }

    private void QueueVolumeUpdate(float volume)
    {
        Volatile.Write(ref _queuedVolume, volume);
        if (Interlocked.CompareExchange(ref _isVolumeDispatching, 1, 0) == 0)
        {
            _ = Task.Run(FlushQueuedVolumeUpdates);
        }
    }

    private void FlushQueuedVolumeUpdates()
    {
        try
        {
            while (true)
            {
                var processId = ProcessId;
                if (processId <= 0)
                {
                    break;
                }

                var volumeToApply = Volatile.Read(ref _queuedVolume);
                _setVolume(processId, volumeToApply);

                var latestRequested = Volatile.Read(ref _queuedVolume);
                if (Math.Abs(latestRequested - volumeToApply) > VolumeDispatchEpsilon)
                {
                    continue;
                }

                Interlocked.Exchange(ref _isVolumeDispatching, 0);
                latestRequested = Volatile.Read(ref _queuedVolume);
                if (Math.Abs(latestRequested - volumeToApply) <= VolumeDispatchEpsilon
                    || Interlocked.CompareExchange(ref _isVolumeDispatching, 1, 0) != 0)
                {
                    break;
                }
            }
        }
        catch
        {
            Interlocked.Exchange(ref _isVolumeDispatching, 0);
        }
    }

    private void QueuePlaybackRouteUpdate(string deviceId)
    {
        Volatile.Write(ref _queuedPlaybackDeviceId, deviceId);
        if (Interlocked.CompareExchange(ref _isPlaybackRouteDispatching, 1, 0) == 0)
        {
            _ = Task.Run(FlushQueuedPlaybackRouteUpdates);
        }
    }

    private void FlushQueuedPlaybackRouteUpdates()
    {
        try
        {
            while (true)
            {
                var processId = ProcessId;
                if (processId <= 0)
                {
                    break;
                }

                var deviceToApply = Volatile.Read(ref _queuedPlaybackDeviceId);
                _setPreferredPlaybackDevice(processId, deviceToApply);

                var latestRequested = Volatile.Read(ref _queuedPlaybackDeviceId);
                if (!string.Equals(latestRequested, deviceToApply, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Interlocked.Exchange(ref _isPlaybackRouteDispatching, 0);
                latestRequested = Volatile.Read(ref _queuedPlaybackDeviceId);
                if (string.Equals(latestRequested, deviceToApply, StringComparison.OrdinalIgnoreCase)
                    || Interlocked.CompareExchange(ref _isPlaybackRouteDispatching, 1, 0) != 0)
                {
                    break;
                }
            }
        }
        catch
        {
            Interlocked.Exchange(ref _isPlaybackRouteDispatching, 0);
        }
    }

    private void QueueCaptureRouteUpdate(string deviceId)
    {
        Volatile.Write(ref _queuedCaptureDeviceId, deviceId);
        if (Interlocked.CompareExchange(ref _isCaptureRouteDispatching, 1, 0) == 0)
        {
            _ = Task.Run(FlushQueuedCaptureRouteUpdates);
        }
    }

    private void FlushQueuedCaptureRouteUpdates()
    {
        try
        {
            while (true)
            {
                var processId = ProcessId;
                if (processId <= 0)
                {
                    break;
                }

                var deviceToApply = Volatile.Read(ref _queuedCaptureDeviceId);
                _setPreferredCaptureDevice(processId, deviceToApply);

                var latestRequested = Volatile.Read(ref _queuedCaptureDeviceId);
                if (!string.Equals(latestRequested, deviceToApply, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Interlocked.Exchange(ref _isCaptureRouteDispatching, 0);
                latestRequested = Volatile.Read(ref _queuedCaptureDeviceId);
                if (string.Equals(latestRequested, deviceToApply, StringComparison.OrdinalIgnoreCase)
                    || Interlocked.CompareExchange(ref _isCaptureRouteDispatching, 1, 0) != 0)
                {
                    break;
                }
            }
        }
        catch
        {
            Interlocked.Exchange(ref _isCaptureRouteDispatching, 0);
        }
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

    private static string NormalizeSelectionIdToKnownOption(
        string? deviceId,
        IEnumerable<AudioDeviceOptionModel> options)
    {
        var normalized = NormalizeSelectionId(deviceId);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        foreach (var option in options)
        {
            if (string.Equals(option.Id, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return option.Id;
            }
        }

        foreach (var option in options)
        {
            if (normalized.Contains(option.Id, StringComparison.OrdinalIgnoreCase)
                || option.Id.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return option.Id;
            }
        }

        return string.Empty;
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
