using System.Windows;
using System.Windows.Threading;
using AudioBit.App.Models;
using AudioBit.App.Services;
using AudioBit.Core.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBit.App.ViewModels;

internal sealed class DiscordViewModel : ObservableObject, IDisposable
{
    private readonly DiscordRpcService _rpcService;
    private readonly Dispatcher _dispatcher;

    private bool _disposed;
    private bool _isMuted;
    private bool _isDeafened;
    private bool _isConnected;
    private bool _isBusy;
    private bool _hasVoiceActivity;
    private bool _wasMutedBeforeDeafen;
    private bool _pendingMuteToggle;
    private bool _pendingDeafenToggle;
    private double _livePeak;
    private string _statusText = "Not connected";
    private string _connectionStatusText = "Not connected";
    private DiscordConnectionState _connectionState = DiscordConnectionState.Disconnected;
    private string _lastLoggedVoiceState = string.Empty;

    public DiscordViewModel(DiscordRpcService rpcService, Dispatcher? dispatcher = null)
    {
        _rpcService = rpcService ?? throw new ArgumentNullException(nameof(rpcService));
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        ToggleMuteCommand = new AsyncRelayCommand(ToggleMuteAsync, () => !IsBusy && IsConfigured);
        ToggleDeafenCommand = new AsyncRelayCommand(ToggleDeafenAsync, () => !IsBusy && IsConfigured);
        ConnectCommand = new RelayCommand(DoConnect, () => !IsConnected && !IsBusy && IsConfigured);
        DisconnectCommand = new RelayCommand(DoDisconnect, () => IsConnected && !IsBusy);

        _rpcService.VoiceSettingsChanged += OnVoiceSettingsChanged;
        _rpcService.ConnectionStateChanged += OnConnectionStateChanged;
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set => SetProperty(ref _isMuted, value);
    }

    public bool IsDeafened
    {
        get => _isDeafened;
        private set => SetProperty(ref _isDeafened, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (!SetProperty(ref _isConnected, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ConnectButtonText));
            ToggleMuteCommand.NotifyCanExecuteChanged();
            ToggleDeafenCommand.NotifyCanExecuteChanged();
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ConnectButtonText));
            ToggleMuteCommand.NotifyCanExecuteChanged();
            ToggleDeafenCommand.NotifyCanExecuteChanged();
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasVoiceActivity
    {
        get => _hasVoiceActivity;
        private set => SetProperty(ref _hasVoiceActivity, value);
    }

    public double LivePeak
    {
        get => _livePeak;
        private set => SetProperty(ref _livePeak, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        private set => SetProperty(ref _connectionStatusText, value);
    }

    public DiscordConnectionState CurrentConnectionState
    {
        get => _connectionState;
        private set => SetProperty(ref _connectionState, value);
    }

    public bool IsConfigured => _rpcService.IsConfigured;

    public string ConnectButtonText => IsBusy
        ? "Connecting..."
        : IsConnected
            ? "Connected to Discord"
            : "Connect Discord";

    public IAsyncRelayCommand ToggleMuteCommand { get; }

    public IAsyncRelayCommand ToggleDeafenCommand { get; }

    public IRelayCommand ConnectCommand { get; }

    public IRelayCommand DisconnectCommand { get; }

    public void Start()
    {
        if (!_rpcService.IsConfigured)
        {
            AppLog.Trace("DiscordViewModel", "Discord auto-connect skipped because the client is not configured.");
            return;
        }

        if (!_rpcService.HasSavedAuthorization)
        {
            AppLog.Trace("DiscordViewModel", "Discord auto-connect skipped because no saved authorization was found.");
            return;
        }

        AppLog.Info("DiscordViewModel", "Discord auto-connect requested using saved authorization.");
        _rpcService.Start();
    }

    public void Stop()
    {
        _rpcService.Stop();
        UpdateLocalAudioState(0.0, false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        AppLog.Info("DiscordViewModel", "Disposing Discord view model.");
        _rpcService.VoiceSettingsChanged -= OnVoiceSettingsChanged;
        _rpcService.ConnectionStateChanged -= OnConnectionStateChanged;
        _rpcService.Dispose();
    }

    private void DoConnect()
    {
        if (IsBusy || IsConnected)
        {
            return;
        }

        AppLog.Info("DiscordViewModel", "Discord connect requested.");
        _rpcService.Start();
    }

    private void DoDisconnect()
    {
        if (IsBusy || !IsConnected)
        {
            return;
        }

        AppLog.Info("DiscordViewModel", "Discord disconnect requested.");
        _rpcService.DisconnectAndForgetAuthorization();
        _pendingMuteToggle = false;
        _pendingDeafenToggle = false;
        IsMuted = false;
        IsDeafened = false;
        UpdateLocalAudioState(0.0, false);
        IsConnected = false;
        StatusText = "Not connected";
        ConnectionStatusText = "Not connected";
        CurrentConnectionState = DiscordConnectionState.Disconnected;
    }

    public void UpdateLocalAudioState(double peak, bool hasVoiceActivity)
    {
        var clamped = Math.Clamp(peak, 0.0, 1.0);
        if (_dispatcher.CheckAccess())
        {
            LivePeak = clamped;
            HasVoiceActivity = hasVoiceActivity;
            return;
        }

        _dispatcher.BeginInvoke(
            DispatcherPriority.DataBind,
            new Action(() =>
            {
                LivePeak = clamped;
                HasVoiceActivity = hasVoiceActivity;
            }));
    }

    private async Task ToggleMuteAsync()
    {
        
        if (!IsConnected)
        {
            _pendingMuteToggle = true;
            _pendingDeafenToggle = false;
            DoConnect();
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var newMute = !IsMuted;

            
            var newDeaf = IsDeafened;
            if (!newMute && IsDeafened)
            {
                newDeaf = false;
            }

            _wasMutedBeforeDeafen = newMute;
            AppLog.Info("DiscordViewModel", $"Discord mute toggle requested. mute={newMute} deaf={newDeaf}");
            await _rpcService.SetVoiceSettingsAsync(newMute, newDeaf).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => IsBusy = false);
        }
    }

    private async Task ToggleDeafenAsync()
    {
        
        if (!IsConnected)
        {
            _pendingDeafenToggle = true;
            _pendingMuteToggle = false;
            DoConnect();
            return;
        }

        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var newDeaf = !IsDeafened;

            bool newMute;
            if (newDeaf)
            {
                _wasMutedBeforeDeafen = IsMuted;
                newMute = true;
            }
            else
            {
                newMute = _wasMutedBeforeDeafen;
            }

            AppLog.Info("DiscordViewModel", $"Discord deafen toggle requested. mute={newMute} deaf={newDeaf}");
            await _rpcService.SetVoiceSettingsAsync(newMute, newDeaf).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => IsBusy = false);
        }
    }

    private void OnVoiceSettingsChanged(object? sender, DiscordVoiceSettings settings)
    {
        RunOnDispatcher(() =>
        {
            IsMuted = settings.Mute;
            IsDeafened = settings.Deaf;
            var summary = $"Discord voice settings: muted={settings.Mute} deafened={settings.Deaf}";
            if (!string.Equals(summary, _lastLoggedVoiceState, StringComparison.Ordinal))
            {
                _lastLoggedVoiceState = summary;
                AppLog.Trace("DiscordViewModel", summary);
            }
        });
    }

    private void OnConnectionStateChanged(object? sender, DiscordConnectionState state)
    {
        RunOnDispatcher(() =>
        {
            CurrentConnectionState = state;
            IsConnected = state == DiscordConnectionState.Connected;

            var text = state switch
            {
                DiscordConnectionState.Disconnected => "Not connected",
                DiscordConnectionState.Connecting => "Connecting...",
                DiscordConnectionState.WaitingForAuthorization => "Waiting for authorization...",
                DiscordConnectionState.Connected => "Connected to Discord",
                DiscordConnectionState.Error => "Connection error",
                _ => "Not connected",
            };

            StatusText = text;
            ConnectionStatusText = text;
            AppLog.Info("DiscordViewModel", $"Discord connection state changed to {state}.");

            
            if (state == DiscordConnectionState.Connected)
            {
                if (_pendingMuteToggle)
                {
                    _pendingMuteToggle = false;
                    _ = ToggleMuteAsync();
                }
                else if (_pendingDeafenToggle)
                {
                    _pendingDeafenToggle = false;
                    _ = ToggleDeafenAsync();
                }
            }
            else if (state == DiscordConnectionState.Error || state == DiscordConnectionState.Disconnected)
            {
                _pendingMuteToggle = false;
                _pendingDeafenToggle = false;
                UpdateLocalAudioState(0.0, false);
            }
        });
    }

    private void RunOnDispatcher(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.DataBind, action);
    }
}
