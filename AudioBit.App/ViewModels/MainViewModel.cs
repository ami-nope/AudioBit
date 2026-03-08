using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using AudioBit.App.Infrastructure;
using AudioBit.App.Models;
using AudioBit.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AudioBit.App.ViewModels;

internal sealed class MainViewModel : ObservableObject, IDisposable
{
    private const ulong LowPerformanceModeMemoryThresholdBytes = 12UL * 1024UL * 1024UL * 1024UL;

    private enum OverlaySurface
    {
        None,
        Calibrate,
        CalibratedAgents,
    }

    private enum BottomTab
    {
        Eq,
        Profiles,
        Settings,
    }

    private readonly AudioSessionService _audioSessionService;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly StartupRegistrationService _startupRegistrationService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<int, AppAudioViewModel> _viewModelLookup = new();
    private readonly ObservableCollection<AudioDeviceOptionModel> _playbackDevices = new();
    private readonly ObservableCollection<AudioDeviceOptionModel> _captureDevices = new();

    private int _refreshInProgress;
    private bool _disposed;
    private bool _isApplyingDeviceSnapshot;
    private bool _isApplyingSettings;
    private bool _resumeMonitoringWhenRestored;
    private bool _startMinimizedPending;
    private string _statusText = "Monitoring default playback device";
    private string _currentDeviceName = "No playback device";
    private bool _hasPlaybackDevice;
    private bool _isLowPerformanceMode = GetDefaultLowPerformanceMode();
    private bool _isAlwaysOnTop;
    private bool _showAlwaysOnTopPin = true;
    private bool _hideToTrayOnMinimize = true;
    private bool _isEmptyStateVisible = true;
    private bool _isMonitoring;
    private bool _isDarkTheme = true;
    private bool _openOnStartup;
    private bool _startMinimized;
    private bool _autoReconnectRemote = true;
    private bool _runAsBackgroundService = true;
    private CloseButtonBehavior _closeButtonBehavior = CloseButtonBehavior.Exit;
    private string _defaultPlaybackDeviceId = string.Empty;
    private string _defaultMicrophoneDeviceId = string.Empty;
    private string _micMuteHotKey = "Ctrl + Shift + M";
    private int _volumeStepPercent = 5;
    private bool _autoMuteMicOnSoundboard;
    private bool _debugMode;
    private bool _isAdvancedSettingsOpen;
    private bool _isResetConfirmationVisible;
    private bool _isCapturingMicMuteHotKey;
    private OverlaySurface _activeOverlay;
    private BottomTab _selectedBottomTab = BottomTab.Eq;
    private string _selectedAgentKey = "mixer-core";
    private string _selectedAgentName = "Mixer Core";
    private string _selectedAgentSummary = "Live monitoring profile tuned for the default Windows playback device.";
    private string _selectedCalibrationMode = "Adaptive";
    private string _selectedCalibrationOption = "Balanced";
    private string _appliedCalibrationLabel = "Adaptive / Balanced";
    private double _masterVolume = 0.72;
    private bool _isMasterMuted;

    public MainViewModel(
        AudioSessionService audioSessionService,
        AppSettingsStore appSettingsStore,
        StartupRegistrationService startupRegistrationService)
    {
        _audioSessionService = audioSessionService;
        _appSettingsStore = appSettingsStore;
        _startupRegistrationService = startupRegistrationService;
        Sessions = new ObservableCollection<AppAudioViewModel>();
        PlaybackDevices = new ReadOnlyObservableCollection<AudioDeviceOptionModel>(_playbackDevices);
        CaptureDevices = new ReadOnlyObservableCollection<AudioDeviceOptionModel>(_captureDevices);

        RefreshCommand = new AsyncRelayCommand(() => RefreshNowAsync(allowWhenPaused: true));
        ToggleMuteAllCommand = new RelayCommand(ToggleMuteAll);
        StartMonitoringCommand = new RelayCommand(StartMonitoring, () => !IsMonitoring);
        StopMonitoringCommand = new RelayCommand(StopMonitoring, () => IsMonitoring);
        ToggleMonitoringCommand = new RelayCommand(ToggleMonitoring);
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        OpenCalibrateCommand = new RelayCommand(() => ActiveOverlay = OverlaySurface.Calibrate);
        OpenCalibratedAgentsCommand = new RelayCommand(() => ActiveOverlay = OverlaySurface.CalibratedAgents);
        CloseOverlayCommand = new RelayCommand(() => ActiveOverlay = OverlaySurface.None);
        SelectAgentCommand = new RelayCommand<string>(SelectAgent);
        SelectCalibrationModeCommand = new RelayCommand<string>(SelectCalibrationMode);
        SelectCalibrationOptionCommand = new RelayCommand<string>(SelectCalibrationOption);
        ApplyCalibrationCommand = new RelayCommand(ApplyCalibration);
        SelectEqTabCommand = new RelayCommand(() => SelectedBottomTab = BottomTab.Eq);
        SelectProfilesTabCommand = new RelayCommand(() => SelectedBottomTab = BottomTab.Profiles);
        SelectSettingsTabCommand = new RelayCommand(() => SelectedBottomTab = BottomTab.Settings);
        ToggleAdvancedSettingsCommand = new RelayCommand(() => IsAdvancedSettingsOpen = !IsAdvancedSettingsOpen);
        SelectCloseButtonBehaviorCommand = new RelayCommand<string>(SelectCloseButtonBehavior);
        SelectVolumeStepCommand = new RelayCommand<string>(SelectVolumeStep);
        BeginMicMuteHotKeyCaptureCommand = new RelayCommand(BeginMicMuteHotKeyCapture);
        CancelMicMuteHotKeyCaptureCommand = new RelayCommand(CancelMicMuteHotKeyCapture);
        ToggleDefaultCaptureMuteCommand = new RelayCommand(() => _audioSessionService.ToggleDefaultCaptureMute());
        ShowResetConfirmationCommand = new RelayCommand(() => IsResetConfirmationVisible = true);
        CancelResetConfirmationCommand = new RelayCommand(() => IsResetConfirmationVisible = false);
        ConfirmResetSettingsCommand = new RelayCommand(ResetSettings);
        ExportProfilesCommand = new RelayCommand(ExportProfiles);
        ImportProfilesCommand = new RelayCommand(ImportProfiles);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _refreshTimer.Tick += RefreshTimerOnTick;

        ApplySettingsSnapshot(_appSettingsStore.Load(), persistToDisk: false);
    }

    public ObservableCollection<AppAudioViewModel> Sessions { get; }

    public ReadOnlyObservableCollection<AudioDeviceOptionModel> PlaybackDevices { get; }

    public ReadOnlyObservableCollection<AudioDeviceOptionModel> CaptureDevices { get; }

    public IAsyncRelayCommand RefreshCommand { get; }

    public IRelayCommand ToggleMuteAllCommand { get; }

    public IRelayCommand StartMonitoringCommand { get; }

    public IRelayCommand StopMonitoringCommand { get; }

    public IRelayCommand ToggleMonitoringCommand { get; }

    public IRelayCommand ToggleThemeCommand { get; }

    public IRelayCommand OpenCalibrateCommand { get; }

    public IRelayCommand OpenCalibratedAgentsCommand { get; }

    public IRelayCommand CloseOverlayCommand { get; }

    public IRelayCommand<string> SelectAgentCommand { get; }

    public IRelayCommand<string> SelectCalibrationModeCommand { get; }

    public IRelayCommand<string> SelectCalibrationOptionCommand { get; }

    public IRelayCommand ApplyCalibrationCommand { get; }

    public IRelayCommand SelectEqTabCommand { get; }

    public IRelayCommand SelectProfilesTabCommand { get; }

    public IRelayCommand SelectSettingsTabCommand { get; }

    public IRelayCommand ToggleAdvancedSettingsCommand { get; }

    public IRelayCommand<string> SelectCloseButtonBehaviorCommand { get; }

    public IRelayCommand<string> SelectVolumeStepCommand { get; }

    public IRelayCommand BeginMicMuteHotKeyCaptureCommand { get; }

    public IRelayCommand CancelMicMuteHotKeyCaptureCommand { get; }

    public IRelayCommand ToggleDefaultCaptureMuteCommand { get; }

    public IRelayCommand ShowResetConfirmationCommand { get; }

    public IRelayCommand CancelResetConfirmationCommand { get; }

    public IRelayCommand ConfirmResetSettingsCommand { get; }

    public IRelayCommand ExportProfilesCommand { get; }

    public IRelayCommand ImportProfilesCommand { get; }

    public IRelayCommand OpenLogFolderCommand { get; }

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
        set
        {
            var normalized = _showAlwaysOnTopPin && value;
            if (!SetProperty(ref _isAlwaysOnTop, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(PinToggleToolTip));
            PersistSettingsIfReady();
        }
    }

    public bool ShowAlwaysOnTopPin
    {
        get => _showAlwaysOnTopPin;
        set
        {
            if (!SetProperty(ref _showAlwaysOnTopPin, value))
            {
                return;
            }

            if (!value)
            {
                IsAlwaysOnTop = false;
            }

            PersistSettingsIfReady();
        }
    }

    public bool IsLowPerformanceMode
    {
        get => _isLowPerformanceMode;
        set
        {
            if (!SetProperty(ref _isLowPerformanceMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(EnableEnhancedVisuals));
            OnPropertyChanged(nameof(SessionPopupAnimation));
            PersistSettingsIfReady();
        }
    }

    public bool EnableEnhancedVisuals => !IsLowPerformanceMode;

    public PopupAnimation SessionPopupAnimation => IsLowPerformanceMode ? PopupAnimation.None : PopupAnimation.Fade;

    public bool HideToTrayOnMinimize
    {
        get => _hideToTrayOnMinimize;
        set
        {
            if (!SetProperty(ref _hideToTrayOnMinimize, value))
            {
                return;
            }

            PersistSettingsIfReady();
        }
    }

    public bool OpenOnStartup
    {
        get => _openOnStartup;
        set
        {
            if (!SetProperty(ref _openOnStartup, value))
            {
                return;
            }

            SyncStartupRegistration();
            PersistSettingsIfReady();
        }
    }

    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (!SetProperty(ref _startMinimized, value))
            {
                return;
            }

            _startMinimizedPending = value;
            PersistSettingsIfReady();
        }
    }

    public bool AutoReconnectRemote
    {
        get => _autoReconnectRemote;
        set
        {
            if (!SetProperty(ref _autoReconnectRemote, value))
            {
                return;
            }

            PersistSettingsIfReady();
        }
    }

    public bool RunAsBackgroundService
    {
        get => _runAsBackgroundService;
        set
        {
            if (!SetProperty(ref _runAsBackgroundService, value))
            {
                return;
            }

            PersistSettingsIfReady();
        }
    }

    public CloseButtonBehavior CloseButtonBehavior
    {
        get => _closeButtonBehavior;
        set
        {
            if (!SetProperty(ref _closeButtonBehavior, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CloseButtonBehaviorKey));
            PersistSettingsIfReady();
        }
    }

    public string CloseButtonBehaviorKey => CloseButtonBehavior.ToString();

    public string DefaultPlaybackDeviceId
    {
        get => _defaultPlaybackDeviceId;
        set
        {
            var normalized = NormalizeSelectionId(value);
            if (!SetProperty(ref _defaultPlaybackDeviceId, normalized))
            {
                return;
            }

            PersistSettingsIfReady();
        }
    }

    public string DefaultMicrophoneDeviceId
    {
        get => _defaultMicrophoneDeviceId;
        set
        {
            var normalized = NormalizeSelectionId(value);
            if (!SetProperty(ref _defaultMicrophoneDeviceId, normalized))
            {
                return;
            }

            PersistSettingsIfReady();
        }
    }

    public string MicMuteHotKey
    {
        get => _micMuteHotKey;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
            if (!SetProperty(ref _micMuteHotKey, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(MicMuteHotKeyDisplayText));
            PersistSettingsIfReady();
        }
    }

    public string MicMuteHotKeyDisplayText =>
        IsCapturingMicMuteHotKey
            ? "Press keys..."
            : string.IsNullOrWhiteSpace(MicMuteHotKey) ? "Not set" : MicMuteHotKey;

    public string MicMuteHotKeyHelperText =>
        IsCapturingMicMuteHotKey ? "Press Esc to cancel" : "Click to change";

    public int VolumeStepPercent
    {
        get => _volumeStepPercent;
        set
        {
            var normalized = NormalizeVolumeStep(value);
            if (!SetProperty(ref _volumeStepPercent, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(VolumeStepValue));
            OnPropertyChanged(nameof(VolumeStepKey));
            PersistSettingsIfReady();
        }
    }

    public string VolumeStepKey => VolumeStepPercent.ToString();

    public double VolumeStepValue => VolumeStepPercent / 100.0;

    public bool AutoMuteMicOnSoundboard
    {
        get => _autoMuteMicOnSoundboard;
        set
        {
            if (!SetProperty(ref _autoMuteMicOnSoundboard, value))
            {
                return;
            }

            PersistSettingsIfReady();
        }
    }

    public bool DebugMode
    {
        get => _debugMode;
        set
        {
            if (!SetProperty(ref _debugMode, value))
            {
                return;
            }

            PersistSettingsIfReady();
        }
    }

    public bool IsAdvancedSettingsOpen
    {
        get => _isAdvancedSettingsOpen;
        set => SetProperty(ref _isAdvancedSettingsOpen, value);
    }

    public bool IsResetConfirmationVisible
    {
        get => _isResetConfirmationVisible;
        set => SetProperty(ref _isResetConfirmationVisible, value);
    }

    public bool IsCapturingMicMuteHotKey
    {
        get => _isCapturingMicMuteHotKey;
        private set
        {
            if (!SetProperty(ref _isCapturingMicMuteHotKey, value))
            {
                return;
            }

            OnPropertyChanged(nameof(MicMuteHotKeyDisplayText));
            OnPropertyChanged(nameof(MicMuteHotKeyHelperText));
        }
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
            PersistSettingsIfReady();
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

    public string PinToggleToolTip => IsAlwaysOnTop ? "Disable always on top" : "Keep window on top";

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

    public bool IsCalibrateOverlayOpen => _activeOverlay == OverlaySurface.Calibrate;

    public bool IsCalibratedAgentsOverlayOpen => _activeOverlay == OverlaySurface.CalibratedAgents;

    public bool IsEqTabSelected => _selectedBottomTab == BottomTab.Eq;

    public bool IsProfilesTabSelected => _selectedBottomTab == BottomTab.Profiles;

    public bool IsSettingsTabSelected => _selectedBottomTab == BottomTab.Settings;

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

    public bool ConsumeStartMinimized()
    {
        if (!_startMinimizedPending)
        {
            return false;
        }

        _startMinimizedPending = false;
        return true;
    }

    public bool ShouldHideOnClose => CloseButtonBehavior == CloseButtonBehavior.Tray;

    public void OnHiddenToTray()
    {
        if (RunAsBackgroundService || !IsMonitoring)
        {
            _resumeMonitoringWhenRestored = false;
            return;
        }

        StopMonitoring();
        _resumeMonitoringWhenRestored = true;
    }

    public void OnRestoredFromTray()
    {
        if (!_resumeMonitoringWhenRestored)
        {
            return;
        }

        _resumeMonitoringWhenRestored = false;
        StartMonitoring();
    }

    public void HandleGlobalMicMuteHotKey()
    {
        _audioSessionService.ToggleDefaultCaptureMute();
    }

    public bool TryHandleMicMuteHotKeyCapture(KeyEventArgs e)
    {
        if (!IsCapturingMicMuteHotKey)
        {
            return false;
        }

        if (e.Key == Key.Escape)
        {
            CancelMicMuteHotKeyCapture();
            return true;
        }

        if (!HotKeyGesture.TryCreate(e, out var gesture))
        {
            return true;
        }

        MicMuteHotKey = gesture.ToString();
        IsCapturingMicMuteHotKey = false;
        return true;
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
            SyncDeviceOptions(_playbackDevices, _audioSessionService.RenderDeviceOptions);
            SyncDeviceOptions(_captureDevices, _audioSessionService.CaptureDeviceOptions);
            NormalizePersistedDeviceSelections();

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
                viewModel = new AppAudioViewModel(
                    PlaybackDevices,
                    CaptureDevices,
                    _audioSessionService.SetVolume,
                    _audioSessionService.SetMute,
                    _audioSessionService.SetPreferredPlaybackDevice,
                    _audioSessionService.SetPreferredCaptureDevice);
                _viewModelLookup[model.ProcessId] = viewModel;
                Sessions.Insert(index, viewModel);
            }

            viewModel.Apply(model);
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
        ApplySelectedAgent(agentKey);
        PersistSettingsIfReady();
    }

    private void ApplySelectedAgent(string? agentKey)
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
        PersistSettingsIfReady();
    }

    private void SelectCalibrationOption(string? option)
    {
        SelectedCalibrationOption = string.IsNullOrWhiteSpace(option) ? "Balanced" : option;
        OnPropertyChanged(nameof(CalibrationGuideText));
        OnPropertyChanged(nameof(CalibrationSummary));
        PersistSettingsIfReady();
    }

    private void ApplyCalibration()
    {
        AppliedCalibrationLabel = $"{SelectedCalibrationMode} / {SelectedCalibrationOption}";
        ActiveOverlay = OverlaySurface.None;
        PersistSettingsIfReady();
    }

    private void SelectCloseButtonBehavior(string? behavior)
    {
        CloseButtonBehavior = Enum.TryParse<CloseButtonBehavior>(behavior, ignoreCase: true, out var parsed)
            ? parsed
            : CloseButtonBehavior.Exit;
    }

    private void SelectVolumeStep(string? step)
    {
        if (int.TryParse(step, out var parsed))
        {
            VolumeStepPercent = parsed;
        }
    }

    private void BeginMicMuteHotKeyCapture()
    {
        IsCapturingMicMuteHotKey = true;
    }

    private void CancelMicMuteHotKeyCapture()
    {
        IsCapturingMicMuteHotKey = false;
    }

    private void ResetSettings()
    {
        ApplySettingsSnapshot(new AppSettingsSnapshot(), persistToDisk: true);
        IsAdvancedSettingsOpen = false;
        IsResetConfirmationVisible = false;
    }

    private void ExportProfiles()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = ".json",
                Filter = "AudioBit Settings (*.json)|*.json|All Files (*.*)|*.*",
                FileName = "AudioBit-settings.json",
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            _appSettingsStore.SaveTo(dialog.FileName, CreateSettingsSnapshot());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to export settings.\n\n{ex.Message}", "AudioBit", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportProfiles()
    {
        try
        {
            var dialog = new OpenFileDialog
            {
                CheckFileExists = true,
                Filter = "AudioBit Settings (*.json)|*.json|All Files (*.*)|*.*",
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var importedSnapshot = _appSettingsStore.LoadFrom(dialog.FileName);
            ApplySettingsSnapshot(importedSnapshot, persistToDisk: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to import settings.\n\n{ex.Message}", "AudioBit", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AudioBitPaths.LogsDirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = AudioBitPaths.LogsDirectoryPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to open the log folder.\n\n{ex.Message}", "AudioBit", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplySettingsSnapshot(AppSettingsSnapshot snapshot, bool persistToDisk)
    {
        _isApplyingSettings = true;

        try
        {
            OpenOnStartup = snapshot.OpenOnStartup;
            HideToTrayOnMinimize = snapshot.HideToTrayOnMinimize;
            StartMinimized = snapshot.StartMinimized;
            AutoReconnectRemote = snapshot.AutoReconnectRemote;
            RunAsBackgroundService = snapshot.RunAsBackgroundService;
            IsAlwaysOnTop = snapshot.IsAlwaysOnTop;
            ShowAlwaysOnTopPin = snapshot.ShowAlwaysOnTopPin;
            IsLowPerformanceMode = snapshot.IsLowPerformanceMode ?? GetDefaultLowPerformanceMode();
            CloseButtonBehavior = snapshot.CloseButtonBehavior;
            DefaultPlaybackDeviceId = snapshot.DefaultPlaybackDeviceId;
            DefaultMicrophoneDeviceId = snapshot.DefaultMicrophoneDeviceId;
            MicMuteHotKey = snapshot.MicMuteHotKey;
            VolumeStepPercent = snapshot.VolumeStepPercent;
            AutoMuteMicOnSoundboard = snapshot.AutoMuteMicOnSoundboard;
            DebugMode = snapshot.DebugMode;
            IsDarkTheme = snapshot.IsDarkTheme;
            ApplySelectedAgent(snapshot.SelectedAgentKey);
            SelectedCalibrationMode = string.IsNullOrWhiteSpace(snapshot.SelectedCalibrationMode) ? "Adaptive" : snapshot.SelectedCalibrationMode;
            SelectedCalibrationOption = string.IsNullOrWhiteSpace(snapshot.SelectedCalibrationOption) ? "Balanced" : snapshot.SelectedCalibrationOption;
            AppliedCalibrationLabel = string.IsNullOrWhiteSpace(snapshot.AppliedCalibrationLabel)
                ? $"{SelectedCalibrationMode} / {SelectedCalibrationOption}"
                : snapshot.AppliedCalibrationLabel;
            OnPropertyChanged(nameof(CalibrationGuideText));
            OnPropertyChanged(nameof(CalibrationSummary));
            OnPropertyChanged(nameof(SelectedAgentFootnote));
            NormalizePersistedDeviceSelections();
            _startMinimizedPending = StartMinimized;
        }
        finally
        {
            _isApplyingSettings = false;
        }

        SyncStartupRegistration();

        if (persistToDisk)
        {
            PersistSettingsIfReady();
        }
    }

    private AppSettingsSnapshot CreateSettingsSnapshot()
    {
        return new AppSettingsSnapshot
        {
            OpenOnStartup = OpenOnStartup,
            HideToTrayOnMinimize = HideToTrayOnMinimize,
            StartMinimized = StartMinimized,
            AutoReconnectRemote = AutoReconnectRemote,
            RunAsBackgroundService = RunAsBackgroundService,
            IsAlwaysOnTop = IsAlwaysOnTop,
            ShowAlwaysOnTopPin = ShowAlwaysOnTopPin,
            IsLowPerformanceMode = IsLowPerformanceMode,
            CloseButtonBehavior = CloseButtonBehavior,
            DefaultPlaybackDeviceId = DefaultPlaybackDeviceId,
            DefaultMicrophoneDeviceId = DefaultMicrophoneDeviceId,
            MicMuteHotKey = MicMuteHotKey,
            VolumeStepPercent = VolumeStepPercent,
            AutoMuteMicOnSoundboard = AutoMuteMicOnSoundboard,
            DebugMode = DebugMode,
            IsDarkTheme = IsDarkTheme,
            SelectedAgentKey = SelectedAgentKey,
            SelectedCalibrationMode = SelectedCalibrationMode,
            SelectedCalibrationOption = SelectedCalibrationOption,
            AppliedCalibrationLabel = AppliedCalibrationLabel,
        };
    }

    private static bool GetDefaultLowPerformanceMode()
    {
        return TryGetTotalPhysicalMemory(out var totalPhysicalMemory)
            && totalPhysicalMemory <= LowPerformanceModeMemoryThresholdBytes;
    }

    private static bool TryGetTotalPhysicalMemory(out ulong totalPhysicalMemory)
    {
        var memoryStatus = new MemoryStatusEx();
        if (GlobalMemoryStatusEx(memoryStatus))
        {
            totalPhysicalMemory = memoryStatus.TotalPhysicalMemory;
            return true;
        }

        totalPhysicalMemory = 0;
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    private void PersistSettingsIfReady()
    {
        if (_isApplyingSettings)
        {
            return;
        }

        try
        {
            _appSettingsStore.Save(CreateSettingsSnapshot());
        }
        catch
        {
            // Settings persistence should never break the running session.
        }
    }

    private void SyncStartupRegistration()
    {
        try
        {
            _startupRegistrationService.SetRegistered(OpenOnStartup);
        }
        catch
        {
            // Startup registration is best effort; keep the app responsive if registry access fails.
        }
    }

    private void NormalizePersistedDeviceSelections()
    {
        if (_playbackDevices.Count == 0 && _captureDevices.Count == 0)
        {
            return;
        }

        _isApplyingSettings = true;

        try
        {
            DefaultPlaybackDeviceId = NormalizeToAvailableOption(DefaultPlaybackDeviceId, PlaybackDevices);
            DefaultMicrophoneDeviceId = NormalizeToAvailableOption(DefaultMicrophoneDeviceId, CaptureDevices);
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private static void SyncDeviceOptions(ObservableCollection<AudioDeviceOptionModel> target, IReadOnlyList<AudioDeviceOptionModel> source)
    {
        if (DeviceOptionsMatch(target, source))
        {
            return;
        }

        target.Clear();
        foreach (var option in source)
        {
            target.Add(option);
        }
    }

    private static bool DeviceOptionsMatch(ObservableCollection<AudioDeviceOptionModel> target, IReadOnlyList<AudioDeviceOptionModel> source)
    {
        if (target.Count != source.Count)
        {
            return false;
        }

        for (var index = 0; index < target.Count; index++)
        {
            var existing = target[index];
            var incoming = source[index];

            if (!string.Equals(existing.Id, incoming.Id, StringComparison.Ordinal)
                || !string.Equals(existing.DisplayName, incoming.DisplayName, StringComparison.Ordinal)
                || existing.Flow != incoming.Flow
                || existing.IsSystemDefault != incoming.IsSystemDefault)
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeSelectionId(string? deviceId)
    {
        return string.IsNullOrWhiteSpace(deviceId) ? string.Empty : deviceId;
    }

    private static string NormalizeToAvailableOption(string selectionId, IEnumerable<AudioDeviceOptionModel> options)
    {
        var normalized = NormalizeSelectionId(selectionId);
        return options.Any(option => string.Equals(option.Id, normalized, StringComparison.Ordinal))
            ? normalized
            : string.Empty;
    }

    private static int NormalizeVolumeStep(int value)
    {
        return value switch
        {
            1 => 1,
            10 => 10,
            _ => 5,
        };
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
            OnPropertyChanged(nameof(IsCalibrateOverlayOpen));
            OnPropertyChanged(nameof(IsCalibratedAgentsOverlayOpen));
        }
    }

    private BottomTab SelectedBottomTab
    {
        get => _selectedBottomTab;
        set
        {
            if (!SetProperty(ref _selectedBottomTab, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsEqTabSelected));
            OnPropertyChanged(nameof(IsProfilesTabSelected));
            OnPropertyChanged(nameof(IsSettingsTabSelected));
        }
    }
}
