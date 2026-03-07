using System.Collections.ObjectModel;
using System.Windows.Threading;
using AudioBit.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBit.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private enum OverlaySurface
    {
        None,
        Agents,
        Calibrate,
        CalibratedAgents,
    }

    private readonly AudioSessionService _audioSessionService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<int, AppAudioViewModel> _viewModelLookup = new();

    private int _refreshInProgress;
    private bool _disposed;
    private bool _isApplyingDeviceSnapshot;
    private string _statusText = "Monitoring default playback device";
    private string _currentDeviceName = "No playback device";
    private bool _hasPlaybackDevice;
    private bool _isAlwaysOnTop;
    private bool _hideToTrayOnMinimize = true;
    private bool _isEmptyStateVisible = true;
    private bool _isMonitoring;
    private bool _isDarkTheme = true;
    private OverlaySurface _activeOverlay;
    private string _selectedAgentKey = "mixer-core";
    private string _selectedAgentName = "Mixer Core";
    private string _selectedAgentSummary = "Live monitoring profile tuned for the default Windows playback device.";
    private string _selectedCalibrationMode = "Adaptive";
    private string _selectedCalibrationOption = "Balanced";
    private string _appliedCalibrationLabel = "Adaptive / Balanced";
    private double _masterVolume = 0.72;
    private bool _isMasterMuted;

    public MainViewModel(AudioSessionService audioSessionService)
    {
        _audioSessionService = audioSessionService;
        Sessions = new ObservableCollection<AppAudioViewModel>();

        RefreshCommand = new AsyncRelayCommand(() => RefreshNowAsync(allowWhenPaused: true));
        ToggleMuteAllCommand = new RelayCommand(ToggleMuteAll);
        StartMonitoringCommand = new RelayCommand(StartMonitoring, () => !IsMonitoring);
        StopMonitoringCommand = new RelayCommand(StopMonitoring, () => IsMonitoring);
        ToggleMonitoringCommand = new RelayCommand(ToggleMonitoring);
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        OpenAgentsCommand = new RelayCommand(() => ActiveOverlay = OverlaySurface.Agents);
        OpenCalibrateCommand = new RelayCommand(() => ActiveOverlay = OverlaySurface.Calibrate);
        OpenCalibratedAgentsCommand = new RelayCommand(() => ActiveOverlay = OverlaySurface.CalibratedAgents);
        CloseOverlayCommand = new RelayCommand(() => ActiveOverlay = OverlaySurface.None);
        SelectAgentCommand = new RelayCommand<string>(SelectAgent);
        SelectCalibrationModeCommand = new RelayCommand<string>(SelectCalibrationMode);
        SelectCalibrationOptionCommand = new RelayCommand<string>(SelectCalibrationOption);
        ApplyCalibrationCommand = new RelayCommand(ApplyCalibration);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _refreshTimer.Tick += RefreshTimerOnTick;
    }

    public ObservableCollection<AppAudioViewModel> Sessions { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IRelayCommand ToggleMuteAllCommand { get; }

    public IRelayCommand StartMonitoringCommand { get; }

    public IRelayCommand StopMonitoringCommand { get; }

    public IRelayCommand ToggleMonitoringCommand { get; }

    public IRelayCommand ToggleThemeCommand { get; }

    public IRelayCommand OpenAgentsCommand { get; }

    public IRelayCommand OpenCalibrateCommand { get; }

    public IRelayCommand OpenCalibratedAgentsCommand { get; }

    public IRelayCommand CloseOverlayCommand { get; }

    public IRelayCommand<string> SelectAgentCommand { get; }

    public IRelayCommand<string> SelectCalibrationModeCommand { get; }

    public IRelayCommand<string> SelectCalibrationOptionCommand { get; }

    public IRelayCommand ApplyCalibrationCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string CurrentDeviceName
    {
        get => _currentDeviceName;
        private set
        {
            if (!SetProperty(ref _currentDeviceName, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DeviceDisplayName));
            OnPropertyChanged(nameof(CalibrationSummary));
        }
    }

    public string DeviceDisplayName => HasPlaybackDevice ? CurrentDeviceName : "No output device";

    public bool HasPlaybackDevice
    {
        get => _hasPlaybackDevice;
        private set
        {
            if (!SetProperty(ref _hasPlaybackDevice, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DeviceDisplayName));
            OnPropertyChanged(nameof(MasterVolumePercentText));
            OnPropertyChanged(nameof(SystemStatusText));
        }
    }

    public bool IsAlwaysOnTop
    {
        get => _isAlwaysOnTop;
        set => SetProperty(ref _isAlwaysOnTop, value);
    }

    public bool HideToTrayOnMinimize
    {
        get => _hideToTrayOnMinimize;
        set => SetProperty(ref _hideToTrayOnMinimize, value);
    }

    public bool IsEmptyStateVisible
    {
        get => _isEmptyStateVisible;
        private set => SetProperty(ref _isEmptyStateVisible, value);
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        private set
        {
            if (!SetProperty(ref _isMonitoring, value))
            {
                return;
            }

            StartMonitoringCommand.NotifyCanExecuteChanged();
            StopMonitoringCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(MonitoringMenuLabel));
            OnPropertyChanged(nameof(SelectedAgentStatus));
            OnPropertyChanged(nameof(SelectedAgentFootnote));
            OnPropertyChanged(nameof(SystemStatusText));
        }
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (!SetProperty(ref _isDarkTheme, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ThemeToggleToolTip));
        }
    }

    public double MasterVolume
    {
        get => _masterVolume;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (!SetProperty(ref _masterVolume, clamped))
            {
                return;
            }

            OnPropertyChanged(nameof(MasterVolumePercentText));

            if (_isApplyingDeviceSnapshot || !HasPlaybackDevice)
            {
                return;
            }

            _audioSessionService.SetMasterVolume((float)clamped);
        }
    }

    public bool IsMasterMuted
    {
        get => _isMasterMuted;
        set
        {
            if (!SetProperty(ref _isMasterMuted, value))
            {
                return;
            }

            if (_isApplyingDeviceSnapshot || !HasPlaybackDevice)
            {
                return;
            }

            _audioSessionService.SetMasterMute(value);
        }
    }

    public string MasterVolumePercentText => HasPlaybackDevice ? $"{Math.Round(MasterVolume * 100):0}%" : "--";

    public int LiveSessionCount => Sessions.Count;

    public string ThemeToggleToolTip => IsDarkTheme ? "Switch to light theme" : "Switch to dark theme";

    public string MonitoringMenuLabel => IsMonitoring ? "Pause Monitoring" : "Start Monitoring";

    public string SelectedAgentKey
    {
        get => _selectedAgentKey;
        private set => SetProperty(ref _selectedAgentKey, value);
    }

    public string SelectedAgentName
    {
        get => _selectedAgentName;
        private set => SetProperty(ref _selectedAgentName, value);
    }

    public string SelectedAgentSummary
    {
        get => _selectedAgentSummary;
        private set => SetProperty(ref _selectedAgentSummary, value);
    }

    public string SelectedAgentStatus => IsMonitoring ? "Monitoring live" : "Paused";

    public string SelectedAgentFootnote =>
        $"{Sessions.Count} live source{(Sessions.Count == 1 ? string.Empty : "s")} - {AppliedCalibrationLabel}";

    public bool IsOverlayVisible => _activeOverlay != OverlaySurface.None;

    public bool IsAgentsOverlayOpen => _activeOverlay == OverlaySurface.Agents;

    public bool IsCalibrateOverlayOpen => _activeOverlay == OverlaySurface.Calibrate;

    public bool IsCalibratedAgentsOverlayOpen => _activeOverlay == OverlaySurface.CalibratedAgents;

    public string SelectedCalibrationMode
    {
        get => _selectedCalibrationMode;
        private set => SetProperty(ref _selectedCalibrationMode, value);
    }

    public string SelectedCalibrationOption
    {
        get => _selectedCalibrationOption;
        private set => SetProperty(ref _selectedCalibrationOption, value);
    }

    public string AppliedCalibrationLabel
    {
        get => _appliedCalibrationLabel;
        private set
        {
            if (!SetProperty(ref _appliedCalibrationLabel, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedAgentFootnote));
        }
    }

    public string CalibrationGuideText => $"{SelectedCalibrationMode} mode / {SelectedCalibrationOption} contour";

    public string CalibrationSummary =>
        $"Prepared for {DeviceDisplayName} with {Sessions.Count} live source{(Sessions.Count == 1 ? string.Empty : "s")}.";

    public string SystemStatusText
    {
        get
        {
            if (!HasPlaybackDevice)
            {
                return "No Output Device";
            }

            return IsMonitoring ? "System Audio Active" : "Monitoring Paused";
        }
    }

    public bool AreAllMuted => Sessions.Count > 0 && Sessions.All(session => session.IsMuted);

    public void Start()
    {
        StartMonitoring();
    }

    public void Stop()
    {
        _refreshTimer.Stop();
        IsMonitoring = false;
        UpdateStatusText();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimerOnTick;
    }

    private async void RefreshTimerOnTick(object? sender, EventArgs e)
    {
        await RefreshNowAsync(allowWhenPaused: false);
    }

    private async Task RefreshNowAsync(bool allowWhenPaused)
    {
        if (_disposed || (!allowWhenPaused && !IsMonitoring) || Interlocked.Exchange(ref _refreshInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            var snapshot = await Task.Run(_audioSessionService.Refresh).ConfigureAwait(true);
            if (_disposed || (!allowWhenPaused && !IsMonitoring))
            {
                return;
            }

            CurrentDeviceName = _audioSessionService.CurrentDeviceName;
            HasPlaybackDevice = _audioSessionService.HasPlaybackDevice;

            _isApplyingDeviceSnapshot = true;
            MasterVolume = _audioSessionService.MasterVolume;
            IsMasterMuted = _audioSessionService.IsMasterMuted;
            _isApplyingDeviceSnapshot = false;

            ApplySnapshot(snapshot);
            UpdateStatusText();
        }
        catch
        {
            StatusText = "Audio session monitoring is temporarily unavailable.";
            HasPlaybackDevice = false;
            CurrentDeviceName = "No playback device";

            _isApplyingDeviceSnapshot = true;
            MasterVolume = 0.0;
            IsMasterMuted = false;
            _isApplyingDeviceSnapshot = false;
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInProgress, 0);
            OnPropertyChanged(nameof(AreAllMuted));
            OnPropertyChanged(nameof(LiveSessionCount));
        }
    }

    private void ToggleMuteAll()
    {
        var nextMuteState = Sessions.Any(session => !session.IsMuted);
        _audioSessionService.SetAllMuted(nextMuteState);

        foreach (var session in Sessions)
        {
            session.SetMutedVisualState(nextMuteState);
        }

        OnPropertyChanged(nameof(AreAllMuted));
    }

    private void ApplySnapshot(IReadOnlyList<AppAudioModel> models)
    {
        var visibleIds = models.Select(model => model.ProcessId).ToHashSet();
        var staleIds = _viewModelLookup.Keys.Where(id => !visibleIds.Contains(id)).ToArray();

        foreach (var staleId in staleIds)
        {
            if (!_viewModelLookup.TryGetValue(staleId, out var staleViewModel))
            {
                continue;
            }

            Sessions.Remove(staleViewModel);
            _viewModelLookup.Remove(staleId);
        }

        for (var index = 0; index < models.Count; index++)
        {
            var model = models[index];
            if (!_viewModelLookup.TryGetValue(model.ProcessId, out var viewModel))
            {
                viewModel = new AppAudioViewModel(_audioSessionService.SetVolume, _audioSessionService.SetMute);
                _viewModelLookup[model.ProcessId] = viewModel;
                Sessions.Insert(index, viewModel);
            }

            viewModel.Apply(model);

            var currentIndex = Sessions.IndexOf(viewModel);
            if (currentIndex >= 0 && currentIndex != index)
            {
                Sessions.Move(currentIndex, index);
            }
        }

        IsEmptyStateVisible = Sessions.Count == 0;
        OnPropertyChanged(nameof(LiveSessionCount));
        OnPropertyChanged(nameof(AreAllMuted));
        OnPropertyChanged(nameof(SelectedAgentFootnote));
        OnPropertyChanged(nameof(CalibrationSummary));
    }

    private void StartMonitoring()
    {
        if (IsMonitoring)
        {
            return;
        }

        IsMonitoring = true;
        _refreshTimer.Start();
        UpdateStatusText();
        _ = RefreshNowAsync(allowWhenPaused: true);
    }

    private void StopMonitoring()
    {
        if (!IsMonitoring)
        {
            return;
        }

        _refreshTimer.Stop();
        IsMonitoring = false;
        UpdateStatusText();
    }

    private void ToggleMonitoring()
    {
        if (IsMonitoring)
        {
            StopMonitoring();
            return;
        }

        StartMonitoring();
    }

    private void UpdateStatusText()
    {
        if (!HasPlaybackDevice)
        {
            StatusText = IsMonitoring
                ? "Waiting for a default playback device."
                : "No playback device available.";
            return;
        }

        if (!IsMonitoring)
        {
            StatusText = $"Monitoring paused on {CurrentDeviceName}";
            return;
        }

        StatusText = Sessions.Count == 0
            ? $"Waiting for active audio on {CurrentDeviceName}"
            : $"Monitoring {CurrentDeviceName}";
    }

    private void SelectAgent(string? agentKey)
    {
        switch (agentKey)
        {
            case "device-watch":
                SelectedAgentKey = "device-watch";
                SelectedAgentName = "Device Watch";
                SelectedAgentSummary = "Focuses on the active playback target and tracks when the default route changes.";
                break;

            case "quiet-night":
                SelectedAgentKey = "quiet-night";
                SelectedAgentName = "Quiet Night";
                SelectedAgentSummary = "Uses a softer profile for calmer peak response and low-distraction listening.";
                break;

            default:
                SelectedAgentKey = "mixer-core";
                SelectedAgentName = "Mixer Core";
                SelectedAgentSummary = "Live monitoring profile tuned for the default Windows playback device.";
                break;
        }
    }

    private void SelectCalibrationMode(string? mode)
    {
        SelectedCalibrationMode = string.IsNullOrWhiteSpace(mode) ? "Adaptive" : mode;
        OnPropertyChanged(nameof(CalibrationGuideText));
        OnPropertyChanged(nameof(CalibrationSummary));
    }

    private void SelectCalibrationOption(string? option)
    {
        SelectedCalibrationOption = string.IsNullOrWhiteSpace(option) ? "Balanced" : option;
        OnPropertyChanged(nameof(CalibrationGuideText));
        OnPropertyChanged(nameof(CalibrationSummary));
    }

    private void ApplyCalibration()
    {
        AppliedCalibrationLabel = $"{SelectedCalibrationMode} / {SelectedCalibrationOption}";
        ActiveOverlay = OverlaySurface.None;
    }

    private OverlaySurface ActiveOverlay
    {
        get => _activeOverlay;
        set
        {
            if (_activeOverlay == value)
            {
                return;
            }

            _activeOverlay = value;
            OnPropertyChanged(nameof(IsOverlayVisible));
            OnPropertyChanged(nameof(IsAgentsOverlayOpen));
            OnPropertyChanged(nameof(IsCalibrateOverlayOpen));
            OnPropertyChanged(nameof(IsCalibratedAgentsOverlayOpen));
        }
    }
}
