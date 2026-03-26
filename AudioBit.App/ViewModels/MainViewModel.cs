using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioBit.App.Infrastructure;
using AudioBit.App.Models;
using AudioBit.App.Services;
using AudioBit.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AudioBit.App.ViewModels;

internal sealed class MainViewModel : ObservableObject, IDisposable
{
    private const ulong LowPerformanceModeMemoryThresholdBytes = 12UL * 1024UL * 1024UL * 1024UL;
    private static readonly TimeSpan LocalMasterVolumeSyncHold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RemoteMasterVolumeAnimationDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan RemoteMasterVolumeAnimationFrameInterval = TimeSpan.FromMilliseconds(16);
    private const double RemoteMasterVolumeAnimationThreshold = 0.012;
    private const float MasterVolumeDispatchEpsilon = 0.0005f;
    private const int GoodRemoteLatencyThresholdMs = 50;
    private static readonly TimeSpan RemoteLatencyStaleAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ActiveRefreshInterval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan BalancedRefreshInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan LowPerformanceRefreshInterval = TimeSpan.FromMilliseconds(80);

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

    private enum RemotePanelMode
    {
        None,
        Qr,
        Sid,
    }

    private readonly AudioSessionService _audioSessionService;
    private readonly RemoteClientService _remoteClientService;
    private readonly QrCodeService _qrCodeService;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly StartupRegistrationService _startupRegistrationService;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _masterVolumeAnimationTimer;
    private readonly DispatcherTimer _remoteConnectionTimer;
    private readonly Dictionary<int, AppAudioViewModel> _viewModelLookup = new();
    private readonly HashSet<int> _visibleSessionIdsBuffer = new();
    private readonly List<int> _staleSessionIdsBuffer = new();
    private readonly ObservableCollection<AudioDeviceOptionModel> _playbackDevices = new();
    private readonly ObservableCollection<AudioDeviceOptionModel> _captureDevices = new();
    private readonly DispatcherTimer _persistDebounceTimer;

    private int _refreshInProgress;
    private bool _disposed;
    private bool _isApplyingDeviceSnapshot;
    private bool _isApplyingRemoteMasterVolumeAnimation;
    private bool _isApplyingSettings;
    private bool _resumeMonitoringWhenRestored;
    private bool _startMinimizedPending;
    private string _statusText = "Monitoring default playback device";
    private string? _customBackground;
    private string _customBackgroundDraft = string.Empty;
    private string _customBackgroundValidationMessage = string.Empty;
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
    private float _queuedMasterVolume = 0.72f;
    private DateTime _lastLocalMasterVolumeUpdateUtc = DateTime.MinValue;
    private bool _isMasterMuted;
    private int _isMasterVolumeDispatching;
    private DateTime _remoteMasterVolumeAnimationStartedUtc;
    private double _remoteMasterVolumeAnimationStart;
    private double _remoteMasterVolumeAnimationTarget;
    private string _remoteStatus = "Initializing remote service...";
    private string _remotePairCode = "------";
    private string _remoteQrUrl = string.Empty;
    private string _remoteSessionId = string.Empty;
    private string _generatedRemoteQrKey = string.Empty;
    private DateTimeOffset _remoteSessionExpiresAtUtc = DateTimeOffset.MinValue;
    private bool _isRemoteConnected;
    private ImageSource? _remoteQrCodeImage;
    private RemotePanelMode _remotePanelMode = RemotePanelMode.None;
    private bool _isRemoteSidCredentialsRevealed;
    private DateTimeOffset _remoteConnectedAtUtc = DateTimeOffset.MinValue;
    private TimeSpan _remoteConnectedElapsed = TimeSpan.Zero;
    private string _remoteConnectionProtocol = "QR-Auth";
    private string _remoteDeviceId = string.Empty;
    private string _remoteDeviceName = string.Empty;
    private string _remoteDeviceLocation = string.Empty;
    private string _remoteDeviceConnectionType = string.Empty;
    private string _remoteDeviceIpAddress = string.Empty;
    private string _remoteDeviceUserAgent = string.Empty;
    private int? _remoteDeviceLatencyMs;
    private DateTimeOffset _remoteDeviceLatencyUpdatedAtUtc = DateTimeOffset.MinValue;
    private string _remoteRelayRouteLabel = string.Empty;
    private int? _remoteRelayProbeLatencyMs;
    private int? _remoteConnectedDeviceCount;
    private bool _isRemoteDeviceInfoPanelPinned;
    private bool _isRemoteDeviceInfoPanelHovered;
    private readonly ObservableCollection<RemoteSessionHistoryEntry> _remoteSessionHistoryEntries = [];
    private ReadOnlyObservableCollection<RemoteSessionHistoryEntry>? _remoteSessionHistoryView;
    private bool _isRemoteSessionOverlayPinned;
    private string _lastRemoteSessionHistoryKey = string.Empty;
    private DateTimeOffset _lastRemoteSessionHistoryAtUtc = DateTimeOffset.MinValue;

    public MainViewModel(
        AudioSessionService audioSessionService,
        RemoteClientService remoteClientService,
        QrCodeService qrCodeService,
        AppSettingsStore appSettingsStore,
        StartupRegistrationService startupRegistrationService)
    {
        _audioSessionService = audioSessionService;
        _remoteClientService = remoteClientService;
        _qrCodeService = qrCodeService;
        _appSettingsStore = appSettingsStore;
        _startupRegistrationService = startupRegistrationService;
        Sessions = new ObservableCollection<AppAudioViewModel>();
        PlaybackDevices = new ReadOnlyObservableCollection<AudioDeviceOptionModel>(_playbackDevices);
        CaptureDevices = new ReadOnlyObservableCollection<AudioDeviceOptionModel>(_captureDevices);
        _remoteSessionHistoryView = new ReadOnlyObservableCollection<RemoteSessionHistoryEntry>(_remoteSessionHistoryEntries);

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
        SelectRemotePanelModeCommand = new RelayCommand<string>(SelectRemotePanelMode);
        RevealRemoteSidCredentialsCommand = new RelayCommand(RevealRemoteSidCredentials, () => !IsRemoteSidCredentialsRevealed);
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
        RefreshRemotePairingCommand = new AsyncRelayCommand(RefreshRemotePairingAsync);
        GenerateRemoteQrCodeCommand = new RelayCommand(GenerateRemoteQrCode, CanGenerateRemoteQrCode);
        OpenRemoteQrCommand = new RelayCommand(OpenRemoteQrUrl, () => !string.IsNullOrWhiteSpace(RemoteQrUrl));
        CopyRemotePairCodeCommand = new RelayCommand(CopyRemotePairCode, () => !string.IsNullOrWhiteSpace(RemotePairCode) && !string.Equals(RemotePairCode, "------", StringComparison.Ordinal));
        CopyRemoteSessionIdCommand = new RelayCommand(CopyRemoteSessionId, () => !string.IsNullOrWhiteSpace(RemoteSessionId));
        RemoveRemoteDeviceCommand = new AsyncRelayCommand(RemoveRemoteDeviceAsync, CanRemoveRemoteDevice);
        ToggleRemoteDeviceInfoPanelCommand = new RelayCommand(ToggleRemoteDeviceInfoPanel, CanToggleRemoteDeviceInfoPanel);
        ToggleRemoteSessionOverlayCommand = new RelayCommand(ToggleRemoteSessionOverlay);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = BalancedRefreshInterval,
        };
        _refreshTimer.Tick += RefreshTimerOnTick;
        _masterVolumeAnimationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RemoteMasterVolumeAnimationFrameInterval,
        };
        _masterVolumeAnimationTimer.Tick += MasterVolumeAnimationTimerOnTick;
        _remoteConnectionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _remoteConnectionTimer.Tick += RemoteConnectionTimerOnTick;
        _persistDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _persistDebounceTimer.Tick += PersistDebounceTimerOnTick;
        _remoteClientService.SessionInfoChanged += OnRemoteSessionInfoChanged;

        ApplySettingsSnapshot(_appSettingsStore.Load(), persistToDisk: false);
        ApplyRemoteSessionInfo(_remoteClientService.SessionInfo);
        _remoteClientService.SetAutoReconnect(AutoReconnectRemote);
        _remoteClientService.Start();
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

    public IRelayCommand<string> SelectRemotePanelModeCommand { get; }

    public IRelayCommand RevealRemoteSidCredentialsCommand { get; }

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

    public IAsyncRelayCommand RefreshRemotePairingCommand { get; }

    public IRelayCommand GenerateRemoteQrCodeCommand { get; }

    public IRelayCommand OpenRemoteQrCommand { get; }

    public IRelayCommand CopyRemotePairCodeCommand { get; }

    public IRelayCommand CopyRemoteSessionIdCommand { get; }

    public IAsyncRelayCommand RemoveRemoteDeviceCommand { get; }

    public IRelayCommand ToggleRemoteDeviceInfoPanelCommand { get; }

    public IRelayCommand ToggleRemoteSessionOverlayCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string RemoteStatus
    {
        get => _remoteStatus;
        private set => SetProperty(ref _remoteStatus, value);
    }

    public string RemotePairCode
    {
        get => _remotePairCode;
        private set
        {
            if (!SetProperty(ref _remotePairCode, value))
            {
                return;
            }

            CopyRemotePairCodeCommand.NotifyCanExecuteChanged();
            GenerateRemoteQrCodeCommand.NotifyCanExecuteChanged();
        }
    }

    public string RemoteQrUrl
    {
        get => _remoteQrUrl;
        private set
        {
            if (!SetProperty(ref _remoteQrUrl, value))
            {
                return;
            }

            OpenRemoteQrCommand.NotifyCanExecuteChanged();
            GenerateRemoteQrCodeCommand.NotifyCanExecuteChanged();
        }
    }

    public string RemoteSessionId
    {
        get => _remoteSessionId;
        private set
        {
            if (!SetProperty(ref _remoteSessionId, value))
            {
                return;
            }

            CopyRemoteSessionIdCommand.NotifyCanExecuteChanged();
            GenerateRemoteQrCodeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(RemoteConnectedDeviceSubtitle));
        }
    }

    public bool IsRemoteConnected
    {
        get => _isRemoteConnected;
        private set
        {
            if (!SetProperty(ref _isRemoteConnected, value))
            {
                return;
            }

            if (value)
            {
                if (_remotePanelMode == RemotePanelMode.Sid)
                {
                    RemoteConnectionProtocol = "SID-Auth";
                }
                else if (_remotePanelMode == RemotePanelMode.Qr)
                {
                    RemoteConnectionProtocol = "QR-Auth";
                }

                _remotePanelMode = RemotePanelMode.None;
                _remoteConnectedAtUtc = DateTimeOffset.UtcNow;
                _remoteConnectedElapsed = TimeSpan.Zero;
                _remoteConnectionTimer.Start();
                CloseRemoteSessionOverlay();
            }
            else
            {
                _remoteConnectionTimer.Stop();
                _remoteConnectedAtUtc = DateTimeOffset.MinValue;
                _remoteConnectedElapsed = TimeSpan.Zero;
                SetRemoteSidCredentialsRevealed(false);
                CloseRemoteDeviceInfoPanel();
                if (_remotePanelMode == RemotePanelMode.None && !string.IsNullOrWhiteSpace(RemoteQrUrl))
                {
                    _remotePanelMode = RemotePanelMode.Qr;
                    if (RemoteQrCodeImage is null)
                    {
                        GenerateRemoteQrCode();
                    }
                }
            }

            RemoveRemoteDeviceCommand.NotifyCanExecuteChanged();
            ToggleRemoteDeviceInfoPanelCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsRemoteDeviceInfoPanelVisible));
            OnPropertyChanged(nameof(IsAnyOverlayVisible));
            OnPropertyChanged(nameof(RemoteSessionOverlayStatusText));
            OnPropertyChanged(nameof(RemoteSessionOverlayCurrentDeviceText));
            OnPropertyChanged(nameof(RemoteSessionOverlayDeviceCountText));
            NotifyRemotePanelStateChanged();
        }
    }

    public ImageSource? RemoteQrCodeImage
    {
        get => _remoteQrCodeImage;
        private set
        {
            if (!SetProperty(ref _remoteQrCodeImage, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasRemoteQrCodeImage));
        }
    }

    public bool HasRemoteQrCodeImage => RemoteQrCodeImage is not null;

    public bool IsRemoteModeMenuVisible => !IsRemoteConnected;

    public bool IsRemoteDefaultPanelVisible => !IsRemoteConnected && _remotePanelMode == RemotePanelMode.None;

    public bool IsRemoteQrPanelVisible => !IsRemoteConnected && _remotePanelMode == RemotePanelMode.Qr;

    public bool IsRemoteSidPanelVisible => !IsRemoteConnected && _remotePanelMode == RemotePanelMode.Sid;

    public bool IsRemoteQrPanelActive => _remotePanelMode == RemotePanelMode.Qr;

    public bool IsRemoteSidPanelActive => _remotePanelMode == RemotePanelMode.Sid;

    public bool IsRemoteSidCredentialsRevealed => _isRemoteSidCredentialsRevealed;

    public bool IsRemoteSidCredentialsHiddenViewVisible => !_isRemoteSidCredentialsRevealed;

    public bool IsRemoteSidCredentialsViewVisible => _isRemoteSidCredentialsRevealed;

    public string RemoteSessionRemainingMinutesText => "No timeout";

    public string RemoteConnectedThroughText
    {
        get
        {
            var relayLabel = string.IsNullOrWhiteSpace(_remoteRelayRouteLabel)
                ? "Unknown relay"
                : _remoteRelayRouteLabel;
            if (_remoteRelayProbeLatencyMs.HasValue && _remoteRelayProbeLatencyMs.Value > 0)
            {
                return $"{relayLabel} ({_remoteRelayProbeLatencyMs.Value} ms)";
            }

            return relayLabel;
        }
    }

    public string RemoteConnectedLatencyText =>
        EffectiveRemoteLatencyMs.HasValue
            ? $"{EffectiveRemoteLatencyMs.Value} ms"
            : "--";

    public string RemoteConnectedStrengthText
    {
        get
        {
            if (!EffectiveRemoteLatencyMs.HasValue)
            {
                return "Unknown";
            }

            return EffectiveRemoteLatencyMs.Value < GoodRemoteLatencyThresholdMs
                ? "Good"
                : "Weak";
        }
    }

    public Brush RemoteConnectedStrengthBrush
    {
        get
        {
            if (!EffectiveRemoteLatencyMs.HasValue)
            {
                return Application.Current?.Resources["TextSecondaryBrush"] as Brush ?? Brushes.Gray;
            }

            return EffectiveRemoteLatencyMs.Value < GoodRemoteLatencyThresholdMs
                ? Brushes.LimeGreen
                : Brushes.OrangeRed;
        }
    }

    public string RemoteConnectionProtocol
    {
        get => _remoteConnectionProtocol;
        private set
        {
            if (!SetProperty(ref _remoteConnectionProtocol, value))
            {
                return;
            }

            OnPropertyChanged(nameof(RemoteConnectedProtocolText));
            OnPropertyChanged(nameof(RemoteConnectedSubtitleText));
            OnPropertyChanged(nameof(RemoteDeviceConnectionTypeText));
        }
    }

    public string RemoteConnectedProtocolText => RemoteConnectionProtocol;

    public string RemoteConnectedSubtitleText =>
        string.Equals(RemoteConnectionProtocol, "SID-Auth", StringComparison.Ordinal)
            ? "via SID & Code"
            : "via QR Code Scan";

    public string RemoteConnectedSessionDurationText => FormatRemoteConnectedDuration(_remoteConnectedElapsed);

    public string RemoteConnectedDeviceSubtitle
    {
        get
        {
            var label = ResolveRemoteDeviceLabel(_remoteDeviceName, _remoteDeviceUserAgent);
            return string.IsNullOrWhiteSpace(label) ? "AudioBit Mobile" : label;
        }
    }

    public string RemoteDeviceIdText => string.IsNullOrWhiteSpace(_remoteDeviceId) ? "Unavailable" : _remoteDeviceId;

    public string RemoteDeviceNameText
    {
        get
        {
            var label = ResolveRemoteDeviceLabel(_remoteDeviceName, _remoteDeviceUserAgent);
            return string.IsNullOrWhiteSpace(label) ? "Unknown device" : label;
        }
    }

    public string RemoteDeviceLocationText => string.IsNullOrWhiteSpace(_remoteDeviceLocation) ? "Unknown" : _remoteDeviceLocation;

    public string RemoteDeviceConnectionTypeText =>
        string.IsNullOrWhiteSpace(_remoteDeviceConnectionType)
            ? RemoteConnectionProtocol
            : _remoteDeviceConnectionType;

    public string RemoteDeviceIpAddressText => string.IsNullOrWhiteSpace(_remoteDeviceIpAddress) ? "Unknown" : _remoteDeviceIpAddress;

    public string RemoteDeviceUserAgentText => string.IsNullOrWhiteSpace(_remoteDeviceUserAgent) ? "Unknown" : _remoteDeviceUserAgent;

    public bool IsRemoteDeviceInfoPanelVisible => IsRemoteConnected && (_isRemoteDeviceInfoPanelPinned || _isRemoteDeviceInfoPanelHovered);

    public string RemoteDeviceInfoToggleToolTip => _isRemoteDeviceInfoPanelPinned ? "Hide device info" : "Show device info";

    public bool IsRemoteSessionOverlayVisible
    {
        get => _isRemoteSessionOverlayPinned;
        set
        {
            if (!SetProperty(ref _isRemoteSessionOverlayPinned, value))
            {
                return;
            }

            OnPropertyChanged(nameof(RemoteSessionOverlayToggleToolTip));
            OnPropertyChanged(nameof(IsAnyOverlayVisible));
        }
    }

    public string RemoteSessionOverlayToggleToolTip => _isRemoteSessionOverlayPinned ? "Hide connected devices" : "Show connected devices";

    public string RemoteSessionOverlayStatusText => IsRemoteConnected ? "Connected" : "Not connected";

    public string RemoteSessionOverlayCurrentSessionText =>
        string.IsNullOrWhiteSpace(RemoteSessionId)
            ? "Unavailable"
            : RemoteSessionId;

    public string RemoteSessionOverlayCurrentDeviceText =>
        IsRemoteConnected
            ? RemoteConnectedDeviceSubtitle
            : "No device connected";

    public string RemoteSessionOverlayDeviceCountText
    {
        get
        {
            if (_remoteConnectedDeviceCount.HasValue)
            {
                var count = _remoteConnectedDeviceCount.Value;
                if (count <= 0)
                {
                    return "No devices";
                }

                return count == 1 ? "1 device" : $"{count} devices";
            }

            return IsRemoteConnected ? "1 device" : "No devices";
        }
    }

    public bool HasRemoteSessionHistory => _remoteSessionHistoryEntries.Count > 0;

    public ReadOnlyObservableCollection<RemoteSessionHistoryEntry> RemoteSessionHistoryEntries =>
        _remoteSessionHistoryView ??= new ReadOnlyObservableCollection<RemoteSessionHistoryEntry>(_remoteSessionHistoryEntries);

    public string RemoteSessionExpiresText => "No timeout";

    private bool HasFreshRemoteLatency =>
        _remoteDeviceLatencyMs.HasValue
        && _remoteDeviceLatencyUpdatedAtUtc != DateTimeOffset.MinValue
        && DateTimeOffset.UtcNow - _remoteDeviceLatencyUpdatedAtUtc <= RemoteLatencyStaleAfter;

    private int? EffectiveRemoteLatencyMs
    {
        get
        {
            if (HasFreshRemoteLatency)
            {
                return _remoteDeviceLatencyMs;
            }

            if (_remoteRelayProbeLatencyMs.HasValue && _remoteRelayProbeLatencyMs.Value > 0)
            {
                return _remoteRelayProbeLatencyMs;
            }

            return null;
        }
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
            UpdateRefreshInterval();
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

            _remoteClientService.SetAutoReconnect(value);
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

            if (!_isApplyingSettings && !string.IsNullOrEmpty(normalized))
            {
                _ = Task.Run(() =>
                {
                    try { _audioSessionService.SetSystemDefaultDevice(normalized, AudioDeviceFlow.Render); }
                    catch { /* best effort */ }
                });
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

            if (!_isApplyingSettings && !string.IsNullOrEmpty(normalized))
            {
                _ = Task.Run(() =>
                {
                    try { _audioSessionService.SetSystemDefaultDevice(normalized, AudioDeviceFlow.Capture); }
                    catch { /* best effort */ }
                });
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

    public string? CustomBackground
    {
        get => _customBackground;
        private set
        {
            if (!SetProperty(ref _customBackground, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasCustomBackground));
            PersistSettingsIfReady();
        }
    }

    public string CustomBackgroundDraft
    {
        get => _customBackgroundDraft;
        set => SetCustomBackgroundDraft(value, clearValidationMessage: true);
    }

    public bool HasCustomBackground => !string.IsNullOrWhiteSpace(CustomBackground);

    public string CustomBackgroundValidationMessage
    {
        get => _customBackgroundValidationMessage;
        private set
        {
            var normalized = value ?? string.Empty;
            if (!SetProperty(ref _customBackgroundValidationMessage, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(HasCustomBackgroundValidationError));
        }
    }

    public bool HasCustomBackgroundValidationError => !string.IsNullOrWhiteSpace(CustomBackgroundValidationMessage);

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

            if (_isApplyingDeviceSnapshot || _isApplyingRemoteMasterVolumeAnimation || !HasPlaybackDevice)
            {
                return;
            }

            if (_masterVolumeAnimationTimer.IsEnabled)
            {
                StopMasterVolumeAnimation();
            }

            _lastLocalMasterVolumeUpdateUtc = DateTime.UtcNow;
            QueueMasterVolumeUpdate((float)clamped);
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

    public bool IsAnyOverlayVisible =>
        IsOverlayVisible
        || IsRemoteSessionOverlayVisible;

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
        _masterVolumeAnimationTimer.Stop();
        _masterVolumeAnimationTimer.Tick -= MasterVolumeAnimationTimerOnTick;
        _remoteConnectionTimer.Stop();
        _remoteConnectionTimer.Tick -= RemoteConnectionTimerOnTick;
        _persistDebounceTimer.Stop();
        _persistDebounceTimer.Tick -= PersistDebounceTimerOnTick;
        _remoteClientService.SessionInfoChanged -= OnRemoteSessionInfoChanged;
    }

    private async void RefreshTimerOnTick(object? sender, EventArgs e)
    {
        await RefreshNowAsync(allowWhenPaused: false);
    }

    private void RemoteConnectionTimerOnTick(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(RemoteConnectedLatencyText));
        OnPropertyChanged(nameof(RemoteConnectedStrengthText));
        OnPropertyChanged(nameof(RemoteConnectedStrengthBrush));
    }

    private void UpdateRemoteConnectedDuration()
    {
        if (!IsRemoteConnected || _remoteConnectedAtUtc == DateTimeOffset.MinValue)
        {
            return;
        }

        var elapsedSeconds = Math.Max(0, (int)(DateTimeOffset.UtcNow - _remoteConnectedAtUtc).TotalSeconds);
        var elapsed = TimeSpan.FromSeconds(elapsedSeconds);
        if (elapsed == _remoteConnectedElapsed)
        {
            return;
        }

        _remoteConnectedElapsed = elapsed;
        OnPropertyChanged(nameof(RemoteConnectedSessionDurationText));
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
            try
            {
                ApplyMasterVolumeSnapshot(_audioSessionService.MasterVolume);
                IsMasterMuted = _audioSessionService.IsMasterMuted;
            }
            finally
            {
                _isApplyingDeviceSnapshot = false;
            }

            ApplySnapshot(snapshot);
            _remoteClientService.UpdateAudioSnapshot(
                snapshot,
                _audioSessionService.MasterVolume,
                _audioSessionService.IsMasterMuted,
                _audioSessionService.IsDefaultCaptureMuted,
                _audioSessionService.CurrentPlaybackDeviceId,
                _audioSessionService.CurrentCaptureDeviceId,
                _audioSessionService.RenderDeviceOptions,
                _audioSessionService.CaptureDeviceOptions);
            UpdateStatusText();
        }
        catch
        {
            StatusText = "Audio session monitoring is temporarily unavailable.";
            HasPlaybackDevice = false;
            CurrentDeviceName = "No playback device";

            _isApplyingDeviceSnapshot = true;
            try
            {
                MasterVolume = 0.0;
                IsMasterMuted = false;
            }
            finally
            {
                _isApplyingDeviceSnapshot = false;
            }
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInProgress, 0);
            UpdateRefreshInterval();
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

    private void ApplyMasterVolumeSnapshot(float snapshotVolume)
    {
        var elapsed = DateTime.UtcNow - _lastLocalMasterVolumeUpdateUtc;
        if (elapsed < LocalMasterVolumeSyncHold && Math.Abs(_masterVolume - snapshotVolume) > 0.02)
        {
            return;
        }

        var clamped = Math.Clamp((double)snapshotVolume, 0.0, 1.0);
        if (Math.Abs(_masterVolume - clamped) < RemoteMasterVolumeAnimationThreshold)
        {
            StopMasterVolumeAnimation();
            MasterVolume = clamped;
            return;
        }

        _remoteMasterVolumeAnimationStart = _masterVolume;
        _remoteMasterVolumeAnimationTarget = clamped;
        _remoteMasterVolumeAnimationStartedUtc = DateTime.UtcNow;
        if (!_masterVolumeAnimationTimer.IsEnabled)
        {
            _masterVolumeAnimationTimer.Start();
        }
    }

    private void MasterVolumeAnimationTimerOnTick(object? sender, EventArgs e)
    {
        var duration = RemoteMasterVolumeAnimationDuration.TotalMilliseconds;
        if (duration <= 0.0)
        {
            CompleteMasterVolumeAnimation();
            return;
        }

        var elapsedMilliseconds = (DateTime.UtcNow - _remoteMasterVolumeAnimationStartedUtc).TotalMilliseconds;
        var progress = Math.Clamp(elapsedMilliseconds / duration, 0.0, 1.0);
        var eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
        var nextVolume = _remoteMasterVolumeAnimationStart + ((_remoteMasterVolumeAnimationTarget - _remoteMasterVolumeAnimationStart) * eased);

        _isApplyingRemoteMasterVolumeAnimation = true;
        try
        {
            MasterVolume = nextVolume;
        }
        finally
        {
            _isApplyingRemoteMasterVolumeAnimation = false;
        }

        if (progress >= 1.0 || Math.Abs(_remoteMasterVolumeAnimationTarget - _masterVolume) < 0.0005)
        {
            CompleteMasterVolumeAnimation();
        }
    }

    private void CompleteMasterVolumeAnimation()
    {
        _isApplyingRemoteMasterVolumeAnimation = true;
        try
        {
            MasterVolume = _remoteMasterVolumeAnimationTarget;
        }
        finally
        {
            _isApplyingRemoteMasterVolumeAnimation = false;
        }

        StopMasterVolumeAnimation();
    }

    private void StopMasterVolumeAnimation()
    {
        if (!_masterVolumeAnimationTimer.IsEnabled)
        {
            return;
        }

        _masterVolumeAnimationTimer.Stop();
    }

    private void QueueMasterVolumeUpdate(float volume)
    {
        Volatile.Write(ref _queuedMasterVolume, volume);
        if (Interlocked.CompareExchange(ref _isMasterVolumeDispatching, 1, 0) == 0)
        {
            _ = Task.Run(FlushQueuedMasterVolumeUpdates);
        }
    }

    private void FlushQueuedMasterVolumeUpdates()
    {
        try
        {
            while (true)
            {
                var volumeToApply = Volatile.Read(ref _queuedMasterVolume);
                _audioSessionService.SetMasterVolume(volumeToApply);

                var latestRequested = Volatile.Read(ref _queuedMasterVolume);
                if (Math.Abs(latestRequested - volumeToApply) > MasterVolumeDispatchEpsilon)
                {
                    continue;
                }

                Interlocked.Exchange(ref _isMasterVolumeDispatching, 0);
                latestRequested = Volatile.Read(ref _queuedMasterVolume);
                if (Math.Abs(latestRequested - volumeToApply) <= MasterVolumeDispatchEpsilon
                    || Interlocked.CompareExchange(ref _isMasterVolumeDispatching, 1, 0) != 0)
                {
                    break;
                }
            }
        }
        catch
        {
            Interlocked.Exchange(ref _isMasterVolumeDispatching, 0);
        }
    }

    private void ApplySnapshot(IReadOnlyList<AppAudioModel> models)
    {
        _visibleSessionIdsBuffer.Clear();
        for (var index = 0; index < models.Count; index++)
        {
            _visibleSessionIdsBuffer.Add(models[index].ProcessId);
        }

        _staleSessionIdsBuffer.Clear();
        foreach (var existingId in _viewModelLookup.Keys)
        {
            if (!_visibleSessionIdsBuffer.Contains(existingId))
            {
                _staleSessionIdsBuffer.Add(existingId);
            }
        }

        foreach (var staleId in _staleSessionIdsBuffer)
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
        UpdateRefreshInterval();
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

    private void UpdateRefreshInterval()
    {
        if (!IsMonitoring)
        {
            return;
        }

        _refreshTimer.Interval = IsLowPerformanceMode
            ? LowPerformanceRefreshInterval
            : Sessions.Count > 0 ? ActiveRefreshInterval : BalancedRefreshInterval;
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

    private async Task RefreshRemotePairingAsync()
    {
        try
        {
            await _remoteClientService.RefreshPairingSessionAsync();
            ApplyRemoteSessionInfo(_remoteClientService.SessionInfo);
            GenerateRemoteQrCode();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to renew session.\n\n{ex.Message}", "AudioBit", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanGenerateRemoteQrCode()
    {
        return !string.IsNullOrWhiteSpace(RemoteQrUrl);
    }

    private void GenerateRemoteQrCode()
    {
        if (!CanGenerateRemoteQrCode())
        {
            return;
        }

        try
        {
            var image = _qrCodeService.GeneratePairQr(RemoteSessionId, RemotePairCode);
            if (image is null)
            {
                return;
            }

            RemoteQrCodeImage = image;
            _generatedRemoteQrKey = BuildRemoteQrKey(RemoteSessionId, RemotePairCode);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to generate QR code.\n\n{ex.Message}", "AudioBit", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenRemoteQrUrl()
    {
        if (string.IsNullOrWhiteSpace(RemoteQrUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = RemoteQrUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to open the pairing URL.\n\n{ex.Message}", "AudioBit", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyRemotePairCode()
    {
        CopyTextToClipboard(RemotePairCode, "pair code");
    }

    private void CopyRemoteSessionId()
    {
        CopyTextToClipboard(RemoteSessionId, "session ID");
    }

    private bool CanRemoveRemoteDevice()
    {
        return IsRemoteConnected;
    }

    private async Task RemoveRemoteDeviceAsync()
    {
        try
        {
            await _remoteClientService.RemoveConnectedDeviceAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to remove the connected device.\n\n{ex.Message}", "AudioBit", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanToggleRemoteDeviceInfoPanel()
    {
        return IsRemoteConnected;
    }

    private void ToggleRemoteDeviceInfoPanel()
    {
        if (!CanToggleRemoteDeviceInfoPanel())
        {
            return;
        }

        _isRemoteDeviceInfoPanelPinned = !_isRemoteDeviceInfoPanelPinned;
        OnPropertyChanged(nameof(IsRemoteDeviceInfoPanelVisible));
        OnPropertyChanged(nameof(IsAnyOverlayVisible));
        OnPropertyChanged(nameof(RemoteDeviceInfoToggleToolTip));
    }

    internal void SetRemoteDeviceInfoPanelHover(bool isHovered)
    {
        if (_isRemoteDeviceInfoPanelHovered == isHovered)
        {
            return;
        }

        _isRemoteDeviceInfoPanelHovered = isHovered;
        OnPropertyChanged(nameof(IsRemoteDeviceInfoPanelVisible));
        OnPropertyChanged(nameof(IsAnyOverlayVisible));
    }

    internal void CloseRemoteDeviceInfoPanel()
    {
        if (!_isRemoteDeviceInfoPanelPinned && !_isRemoteDeviceInfoPanelHovered)
        {
            return;
        }

        _isRemoteDeviceInfoPanelPinned = false;
        _isRemoteDeviceInfoPanelHovered = false;
        OnPropertyChanged(nameof(IsRemoteDeviceInfoPanelVisible));
        OnPropertyChanged(nameof(IsAnyOverlayVisible));
        OnPropertyChanged(nameof(RemoteDeviceInfoToggleToolTip));
    }

    private void ToggleRemoteSessionOverlay()
    {
        IsRemoteSessionOverlayVisible = !IsRemoteSessionOverlayVisible;
    }

    internal void CloseRemoteSessionOverlay()
    {
        if (!IsRemoteSessionOverlayVisible)
        {
            return;
        }

        IsRemoteSessionOverlayVisible = false;
    }

    private void OnRemoteSessionInfoChanged(RemoteSessionInfo info)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyRemoteSessionInfo(info);
            return;
        }

        _ = dispatcher.BeginInvoke(new Action(() => ApplyRemoteSessionInfo(info)));
    }

    private void ApplyRemoteSessionInfo(RemoteSessionInfo info)
    {
        var previousSessionKey = BuildRemoteQrKey(RemoteSessionId, RemotePairCode);
        var qrUrl = _qrCodeService.BuildPairUrl(info.SessionId, info.PairCode);
        var currentQrKey = BuildRemoteQrKey(info.SessionId, info.PairCode);
        var sessionChanged = !string.Equals(previousSessionKey, currentQrKey, StringComparison.Ordinal);

        RemoteStatus = info.Status;
        RemotePairCode = string.IsNullOrWhiteSpace(info.PairCode) ? "------" : info.PairCode;
        RemoteQrUrl = qrUrl ?? string.Empty;
        RemoteSessionId = info.SessionId ?? string.Empty;
        _remoteSessionExpiresAtUtc = info.ExpiresAtUtc;
        _remoteDeviceId = info.DeviceId ?? string.Empty;
        _remoteDeviceName = info.DeviceName ?? string.Empty;
        _remoteDeviceLocation = info.DeviceLocation ?? string.Empty;
        _remoteDeviceConnectionType = info.ConnectionType ?? string.Empty;
        _remoteDeviceIpAddress = info.IpAddress ?? string.Empty;
        _remoteDeviceUserAgent = info.UserAgent ?? string.Empty;
        _remoteDeviceLatencyMs = info.DeviceLatencyMs;
        _remoteDeviceLatencyUpdatedAtUtc = info.DeviceLatencyUpdatedAtUtc;
        _remoteRelayRouteLabel = info.RelayRouteLabel ?? string.Empty;
        _remoteRelayProbeLatencyMs = info.RelayProbeLatencyMs;
        _remoteConnectedDeviceCount = info.ConnectedDeviceCount;
        IsRemoteConnected = info.IsConnected;
        if (sessionChanged)
        {
            ClearRemoteSessionHistory();
        }

        AppendRemoteSessionHistory(info, currentQrKey);
        OnPropertyChanged(nameof(RemoteSessionExpiresText));
        OnPropertyChanged(nameof(RemoteSessionRemainingMinutesText));
        OnPropertyChanged(nameof(RemoteSessionOverlayStatusText));
        OnPropertyChanged(nameof(RemoteSessionOverlayCurrentSessionText));
        OnPropertyChanged(nameof(RemoteSessionOverlayCurrentDeviceText));
        OnPropertyChanged(nameof(RemoteSessionOverlayDeviceCountText));
        NotifyRemoteDeviceDetailsChanged();

        if (!string.Equals(previousSessionKey, currentQrKey, StringComparison.Ordinal))
        {
            SetRemoteSidCredentialsRevealed(false);
        }

        if (string.IsNullOrEmpty(currentQrKey))
        {
            _generatedRemoteQrKey = string.Empty;
            RemoteQrCodeImage = null;
            return;
        }

        if (!string.Equals(currentQrKey, _generatedRemoteQrKey, StringComparison.Ordinal))
        {
            RemoteQrCodeImage = null;
        }
    }

    private void NotifyRemoteDeviceDetailsChanged()
    {
        OnPropertyChanged(nameof(RemoteConnectedLatencyText));
        OnPropertyChanged(nameof(RemoteConnectedStrengthText));
        OnPropertyChanged(nameof(RemoteConnectedStrengthBrush));
        OnPropertyChanged(nameof(RemoteConnectedThroughText));
        OnPropertyChanged(nameof(RemoteConnectedDeviceSubtitle));
        OnPropertyChanged(nameof(RemoteDeviceIdText));
        OnPropertyChanged(nameof(RemoteDeviceNameText));
        OnPropertyChanged(nameof(RemoteDeviceLocationText));
        OnPropertyChanged(nameof(RemoteDeviceConnectionTypeText));
        OnPropertyChanged(nameof(RemoteDeviceIpAddressText));
        OnPropertyChanged(nameof(RemoteDeviceUserAgentText));
    }

    private void AppendRemoteSessionHistory(RemoteSessionInfo info, string currentSessionKey)
    {
        if (string.IsNullOrWhiteSpace(currentSessionKey))
        {
            return;
        }

        var infoSessionKey = BuildRemoteQrKey(info.SessionId, info.PairCode);
        if (!string.Equals(infoSessionKey, currentSessionKey, StringComparison.Ordinal))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(info.SessionId)
            && string.IsNullOrWhiteSpace(info.Status)
            && string.IsNullOrWhiteSpace(info.DeviceName)
            && string.IsNullOrWhiteSpace(info.DeviceId))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var sessionId = string.IsNullOrWhiteSpace(info.SessionId) ? "n/a" : info.SessionId.Trim();
        var status = string.IsNullOrWhiteSpace(info.Status)
            ? (info.IsConnected ? "connected" : "not connected")
            : info.Status.Trim();
        var deviceLabel = ResolveRemoteDeviceLabel(info.DeviceName, info.UserAgent);
        var device = !string.IsNullOrWhiteSpace(deviceLabel)
            ? deviceLabel
            : (string.IsNullOrWhiteSpace(info.DeviceId) ? "no device" : info.DeviceId.Trim());

        var key = $"{sessionId}|{status}|{device}|{(info.IsConnected ? 1 : 0)}";
        if (string.Equals(key, _lastRemoteSessionHistoryKey, StringComparison.Ordinal)
            && now - _lastRemoteSessionHistoryAtUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastRemoteSessionHistoryKey = key;
        _lastRemoteSessionHistoryAtUtc = now;
        _remoteSessionHistoryEntries.Insert(
            0,
            new RemoteSessionHistoryEntry(
                now.ToLocalTime().ToString("HH:mm:ss"),
                sessionId,
                status,
                device));

        while (_remoteSessionHistoryEntries.Count > 6)
        {
            _remoteSessionHistoryEntries.RemoveAt(_remoteSessionHistoryEntries.Count - 1);
        }

        OnPropertyChanged(nameof(HasRemoteSessionHistory));
    }

    private void ClearRemoteSessionHistory()
    {
        if (_remoteSessionHistoryEntries.Count == 0)
        {
            return;
        }

        _remoteSessionHistoryEntries.Clear();
        _lastRemoteSessionHistoryKey = string.Empty;
        _lastRemoteSessionHistoryAtUtc = DateTimeOffset.MinValue;
        OnPropertyChanged(nameof(HasRemoteSessionHistory));
    }

    private void SelectRemotePanelMode(string? modeKey)
    {
        var targetMode = modeKey?.Trim().ToLowerInvariant() switch
        {
            "qr" => RemotePanelMode.Qr,
            "sid" => RemotePanelMode.Sid,
            _ => RemotePanelMode.None,
        };

        if (targetMode == RemotePanelMode.None || _remotePanelMode == targetMode)
        {
            return;
        }

        _remotePanelMode = targetMode;
        if (targetMode != RemotePanelMode.Qr)
        {
            CloseRemoteSessionOverlay();
        }

        if (targetMode == RemotePanelMode.Qr && RemoteQrCodeImage is null)
        {
            GenerateRemoteQrCode();
        }

        NotifyRemotePanelStateChanged();
    }

    private void RevealRemoteSidCredentials()
    {
        SetRemoteSidCredentialsRevealed(true);
    }

    private void SetRemoteSidCredentialsRevealed(bool value)
    {
        if (_isRemoteSidCredentialsRevealed == value)
        {
            return;
        }

        _isRemoteSidCredentialsRevealed = value;
        OnPropertyChanged(nameof(IsRemoteSidCredentialsRevealed));
        OnPropertyChanged(nameof(IsRemoteSidCredentialsHiddenViewVisible));
        OnPropertyChanged(nameof(IsRemoteSidCredentialsViewVisible));
        RevealRemoteSidCredentialsCommand.NotifyCanExecuteChanged();
    }

    private void NotifyRemotePanelStateChanged()
    {
        OnPropertyChanged(nameof(IsRemoteModeMenuVisible));
        OnPropertyChanged(nameof(IsRemoteDefaultPanelVisible));
        OnPropertyChanged(nameof(IsRemoteQrPanelVisible));
        OnPropertyChanged(nameof(IsRemoteSidPanelVisible));
        OnPropertyChanged(nameof(IsRemoteQrPanelActive));
        OnPropertyChanged(nameof(IsRemoteSidPanelActive));
    }

    private static string ResolveRemoteDeviceLabel(string? deviceName, string? userAgent)
    {
        if (!string.IsNullOrWhiteSpace(deviceName) && !IsPlaceholderDeviceName(deviceName))
        {
            return deviceName.Trim();
        }

        return FormatUserAgentDeviceLabel(userAgent);
    }

    private static string FormatUserAgentDeviceLabel(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return string.Empty;
        }

        var os = DetectUserAgentOs(userAgent);
        var browser = DetectUserAgentBrowser(userAgent);
        if (string.IsNullOrWhiteSpace(os) && string.IsNullOrWhiteSpace(browser))
        {
            return string.Empty;
        }

        if (string.IsNullOrWhiteSpace(os))
        {
            return browser;
        }

        if (string.IsNullOrWhiteSpace(browser))
        {
            return os;
        }

        return $"{os} / {browser}";
    }

    private static bool IsPlaceholderDeviceName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= 2)
        {
            return true;
        }

        var normalized = NormalizeToken(trimmed);
        return normalized is "unknown"
            or "unknowndevice"
            or "unknownclient"
            or "unknownbrowser"
            or "unknownremote"
            or "na"
            or "none"
            or "device"
            or "mobile"
            or "browser"
            or "client"
            or "audiobit"
            or "audiobitmobile";
    }

    private static string NormalizeToken(string value)
    {
        var buffer = new char[value.Length];
        var length = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (!char.IsLetterOrDigit(ch))
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(ch);
        }

        return length == 0 ? string.Empty : new string(buffer, 0, length);
    }

    private static string DetectUserAgentOs(string userAgent)
    {
        if (HasToken(userAgent, "CrOS"))
        {
            return "ChromeOS";
        }

        if (HasToken(userAgent, "iPad"))
        {
            return "iPadOS";
        }

        if (HasToken(userAgent, "iPhone") || HasToken(userAgent, "iPod"))
        {
            return "iOS";
        }

        if (HasToken(userAgent, "Android"))
        {
            return "Android";
        }

        if (HasToken(userAgent, "Windows NT"))
        {
            return MapWindowsVersion(userAgent);
        }

        if (HasToken(userAgent, "Mac OS X")
            && !HasToken(userAgent, "iPhone")
            && !HasToken(userAgent, "iPad")
            && !HasToken(userAgent, "iPod"))
        {
            return "macOS";
        }

        if (HasToken(userAgent, "Linux"))
        {
            return "Linux";
        }

        return string.Empty;
    }

    private static string MapWindowsVersion(string userAgent)
    {
        if (HasToken(userAgent, "Windows NT 10.0"))
        {
            return "Windows 10/11";
        }

        if (HasToken(userAgent, "Windows NT 6.3"))
        {
            return "Windows 8.1";
        }

        if (HasToken(userAgent, "Windows NT 6.2"))
        {
            return "Windows 8";
        }

        if (HasToken(userAgent, "Windows NT 6.1"))
        {
            return "Windows 7";
        }

        if (HasToken(userAgent, "Windows NT 6.0"))
        {
            return "Windows Vista";
        }

        if (HasToken(userAgent, "Windows NT 5.1"))
        {
            return "Windows XP";
        }

        return "Windows";
    }

    private static string DetectUserAgentBrowser(string userAgent)
    {
        if (HasToken(userAgent, "EdgiOS") || HasToken(userAgent, "EdgA") || HasToken(userAgent, "Edg/"))
        {
            return "Edge";
        }

        if (HasToken(userAgent, "OPR/") || HasToken(userAgent, "Opera"))
        {
            return "Opera";
        }

        if (HasToken(userAgent, "SamsungBrowser"))
        {
            return "Samsung Internet";
        }

        if (HasToken(userAgent, "FxiOS") || HasToken(userAgent, "Firefox"))
        {
            return "Firefox";
        }

        if (HasToken(userAgent, "Chromium"))
        {
            return "Chromium";
        }

        if (HasToken(userAgent, "CriOS"))
        {
            return "Chrome";
        }

        if (HasToken(userAgent, "Chrome"))
        {
            return "Chrome";
        }

        if (HasToken(userAgent, "Safari")
            && !HasToken(userAgent, "Chrome")
            && !HasToken(userAgent, "Chromium")
            && !HasToken(userAgent, "CriOS")
            && !HasToken(userAgent, "Edg")
            && !HasToken(userAgent, "OPR")
            && !HasToken(userAgent, "SamsungBrowser"))
        {
            return "Safari";
        }

        if (HasToken(userAgent, "DuckDuckGo"))
        {
            return "DuckDuckGo";
        }

        return string.Empty;
    }

    private static bool HasToken(string source, string token)
    {
        return source.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string FormatRemoteConnectedDuration(TimeSpan duration)
    {
        var totalSeconds = Math.Max(0, (int)duration.TotalSeconds);
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        if (minutes < 100)
        {
            return $"{minutes:00}:{seconds:00}";
        }

        var hours = minutes / 60;
        minutes %= 60;
        return $"{hours:00}:{minutes:00}:{seconds:00}";
    }

    private static string BuildRemoteSessionPreview(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return string.Empty;
        }

        var trimmed = sessionId.Trim();
        return trimmed.Length <= 12
            ? trimmed
            : $"{trimmed[..12]}...";
    }

    private static string BuildRemoteQrKey(string? sessionId, string? pairCode)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(pairCode))
        {
            return string.Empty;
        }

        return $"{sessionId.Trim()}|{pairCode.Trim()}";
    }

    private static void CopyTextToClipboard(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        try
        {
            Clipboard.SetText(value);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Unable to copy {label}.\n\n{ex.Message}", "AudioBit", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public bool TryApplyCustomBackgroundDraft()
    {
        return TryApplyCustomBackground(CustomBackgroundDraft, showValidationMessage: true);
    }

    public bool TryApplyCustomBackground(string? value)
    {
        return TryApplyCustomBackground(value, showValidationMessage: true);
    }

    public void ResetCustomBackground()
    {
        ApplyCustomBackgroundValue(null, updateDraft: true, clearValidationMessage: true);
    }

    public void RevertCustomBackgroundDraft()
    {
        SetCustomBackgroundDraft(CustomBackground ?? string.Empty, clearValidationMessage: true);
    }

    private void ApplySettingsSnapshot(AppSettingsSnapshot snapshot, bool persistToDisk)
    {
        _isApplyingSettings = true;

        try
        {
            TryApplyCustomBackground(snapshot.CustomBackground, showValidationMessage: false);
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
            CustomBackground = CustomBackground,
        };
    }

    private bool TryApplyCustomBackground(string? value, bool showValidationMessage)
    {
        var normalized = CustomBackgroundParser.Normalize(value);
        if (normalized is null)
        {
            ApplyCustomBackgroundValue(null, updateDraft: true, clearValidationMessage: true);
            return true;
        }

        if (!CustomBackgroundParser.TryParse(normalized, out _))
        {
            if (!showValidationMessage)
            {
                ApplyCustomBackgroundValue(null, updateDraft: true, clearValidationMessage: true);
                return false;
            }

            SetCustomBackgroundDraft(normalized, clearValidationMessage: false);
            CustomBackgroundValidationMessage = "Use a color like #181824 or a gradient like #181824 -> #10111A.";
            return false;
        }

        ApplyCustomBackgroundValue(normalized, updateDraft: true, clearValidationMessage: true);
        return true;
    }

    private void ApplyCustomBackgroundValue(string? value, bool updateDraft, bool clearValidationMessage)
    {
        var normalized = CustomBackgroundParser.Normalize(value);
        if (updateDraft)
        {
            SetCustomBackgroundDraft(normalized ?? string.Empty, clearValidationMessage);
        }
        else if (clearValidationMessage)
        {
            CustomBackgroundValidationMessage = string.Empty;
        }

        CustomBackground = normalized;
    }

    private void SetCustomBackgroundDraft(string? value, bool clearValidationMessage)
    {
        var normalized = value ?? string.Empty;
        if (SetProperty(ref _customBackgroundDraft, normalized, nameof(CustomBackgroundDraft)) && clearValidationMessage)
        {
            CustomBackgroundValidationMessage = string.Empty;
            return;
        }

        if (clearValidationMessage)
        {
            CustomBackgroundValidationMessage = string.Empty;
        }
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

        // Debounce: restart the timer so rapid property changes batch into one write.
        _persistDebounceTimer.Stop();
        _persistDebounceTimer.Start();
    }

    private void PersistDebounceTimerOnTick(object? sender, EventArgs e)
    {
        _persistDebounceTimer.Stop();

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
            OnPropertyChanged(nameof(IsAnyOverlayVisible));
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
