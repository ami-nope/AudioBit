using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AudioBit.App.Infrastructure;
using AudioBit.App.Models;
using AudioBit.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AudioBit.App.ViewModels;

public sealed class SpotifyViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ProgressTickInterval = TimeSpan.FromMilliseconds(250);

    private readonly ISpotifyService _spotifyService;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _progressTimer;
    private readonly string _clientId;

    private bool _disposed;
    private bool _isAuthenticated;
    private bool _isBusy;
    private bool _isPlaying;
    private bool _hasTrack;
    private bool _hasActiveDevice;
    private bool _canControlPlayback;
    private string _trackName = "Spotify";
    private string _artistName = "Spotify not connected";
    private ImageSource? _albumArt;
    private string _statusText = "Spotify not connected";
    private string _connectionStatusText = "Spotify not connected";
    private double _progressPercent;
    private bool _showProgressBar;
    private double _livePeak;
    private int _progressBaseMs;
    private int _trackDurationMs;
    private DateTimeOffset _progressBaseUtc = DateTimeOffset.UtcNow;

    public SpotifyViewModel(ISpotifyService spotifyService, string clientId, Dispatcher? dispatcher = null)
    {
        _spotifyService = spotifyService ?? throw new ArgumentNullException(nameof(spotifyService));
        _clientId = SpotifyClientIdResolver.Normalize(clientId);
        _dispatcher = dispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnectToSpotify);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, CanDisconnectFromSpotify);
        PlayPauseCommand = new AsyncRelayCommand(TogglePlaybackAsync, CanControlSpotifyPlayback);
        NextCommand = new AsyncRelayCommand(NextAsync, CanControlSpotifyPlayback);
        PreviousCommand = new AsyncRelayCommand(PreviousAsync, CanControlSpotifyPlayback);

        _progressTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = ProgressTickInterval,
        };
        _progressTimer.Tick += ProgressTimerOnTick;

        _spotifyService.PlaybackStateChanged += SpotifyServiceOnPlaybackStateChanged;
        ApplySnapshot(_spotifyService.CurrentSnapshot);
    }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set
        {
            if (!SetProperty(ref _isAuthenticated, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ConnectButtonText));
            OnPropertyChanged(nameof(CanDisconnect));
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
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanDisconnect));
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
            PlayPauseCommand.NotifyCanExecuteChanged();
            NextCommand.NotifyCanExecuteChanged();
            PreviousCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!SetProperty(ref _isPlaying, value))
            {
                return;
            }

            PlayPauseCommand.NotifyCanExecuteChanged();
        }
    }

    public bool HasTrack
    {
        get => _hasTrack;
        private set => SetProperty(ref _hasTrack, value);
    }

    public bool HasActiveDevice
    {
        get => _hasActiveDevice;
        private set => SetProperty(ref _hasActiveDevice, value);
    }

    public bool CanControlPlayback
    {
        get => _canControlPlayback;
        private set
        {
            if (!SetProperty(ref _canControlPlayback, value))
            {
                return;
            }

            PlayPauseCommand.NotifyCanExecuteChanged();
            NextCommand.NotifyCanExecuteChanged();
            PreviousCommand.NotifyCanExecuteChanged();
        }
    }

    public string TrackName
    {
        get => _trackName;
        private set => SetProperty(ref _trackName, value);
    }

    public string ArtistName
    {
        get => _artistName;
        private set => SetProperty(ref _artistName, value);
    }

    public ImageSource? AlbumArt
    {
        get => _albumArt;
        private set => SetProperty(ref _albumArt, value);
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

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public bool ShowProgressBar
    {
        get => _showProgressBar;
        private set => SetProperty(ref _showProgressBar, value);
    }

    public double LivePeak
    {
        get => _livePeak;
        private set => SetProperty(ref _livePeak, value);
    }

    public string ClientId => _clientId;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_clientId);

    public string ConnectButtonText => !IsConfigured
        ? "Spotify Setup Required"
        : IsBusy
            ? "Connecting..."
            : IsAuthenticated
                ? "Disconnect Spotify"
                : "Connect Spotify";

    public bool CanConnect => CanConnectToSpotify();

    public bool CanDisconnect => CanDisconnectFromSpotify();

    public IAsyncRelayCommand ConnectCommand { get; }

    public IAsyncRelayCommand DisconnectCommand { get; }

    public IAsyncRelayCommand PlayPauseCommand { get; }

    public IAsyncRelayCommand NextCommand { get; }

    public IAsyncRelayCommand PreviousCommand { get; }

    public void Start()
    {
        _ = InitializeAsync(CancellationToken.None);
    }

    public void Stop()
    {
        _progressTimer.Stop();
        UpdateLivePeak(0.0);
        _ = _spotifyService.StopPollingAsync();
    }

    public void OnHiddenToTray()
    {
        _progressTimer.Stop();
        UpdateLivePeak(0.0);
        _ = _spotifyService.StopPollingAsync();
    }

    public void OnRestoredFromTray()
    {
        if (IsAuthenticated)
        {
            _ = _spotifyService.StartPollingAsync(CancellationToken.None);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _progressTimer.Stop();
        _progressTimer.Tick -= ProgressTimerOnTick;
        _spotifyService.PlaybackStateChanged -= SpotifyServiceOnPlaybackStateChanged;
        _spotifyService.Dispose();
    }

    public void UpdateLivePeak(double peak)
    {
        var clamped = Math.Clamp(peak, 0.0, 1.0);
        if (_dispatcher.CheckAccess())
        {
            LivePeak = clamped;
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() => LivePeak = clamped));
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _spotifyService.InitializeAsync(_clientId, cancellationToken).ConfigureAwait(false);
        if (_spotifyService.CurrentSnapshot.IsAuthenticated)
        {
            await _spotifyService.StartPollingAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ConnectAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _spotifyService.ConnectAsync(_clientId, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => IsBusy = false);
        }
    }

    private async Task DisconnectAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _spotifyService.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => IsBusy = false);
        }
    }

    private async Task TogglePlaybackAsync()
    {
        if (!CanControlSpotifyPlayback())
        {
            return;
        }

        if (IsPlaying)
        {
            await _spotifyService.PauseAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        await _spotifyService.PlayAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private Task NextAsync()
    {
        return _spotifyService.NextAsync(CancellationToken.None);
    }

    private Task PreviousAsync()
    {
        return _spotifyService.PreviousAsync(CancellationToken.None);
    }

    private void SpotifyServiceOnPlaybackStateChanged(object? sender, SpotifyPlaybackSnapshot snapshot)
    {
        if (_dispatcher.CheckAccess())
        {
            ApplySnapshot(snapshot);
            return;
        }

        _dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() => ApplySnapshot(snapshot)));
    }

    private void ApplySnapshot(SpotifyPlaybackSnapshot snapshot)
    {
        IsAuthenticated = snapshot.IsAuthenticated;
        HasActiveDevice = snapshot.HasActiveDevice;
        CanControlPlayback = snapshot.CanControlPlayback;
        StatusText = snapshot.StatusText;
        ConnectionStatusText = snapshot.StatusText;

        if (snapshot.Track is null)
        {
            HasTrack = false;
            IsPlaying = false;
            TrackName = "Spotify";
            ArtistName = snapshot.StatusText;
            AlbumArt = null;
            _trackDurationMs = 0;
            _progressBaseMs = 0;
            _progressBaseUtc = snapshot.SnapshotUtc;
            ProgressPercent = 0;
            ShowProgressBar = false;
            _progressTimer.Stop();
            return;
        }

        HasTrack = true;
        IsPlaying = snapshot.Track.IsPlaying;
        TrackName = string.IsNullOrWhiteSpace(snapshot.Track.TrackName) ? "Unknown track" : snapshot.Track.TrackName;
        ArtistName = string.IsNullOrWhiteSpace(snapshot.Track.ArtistName) ? "Unknown artist" : snapshot.Track.ArtistName;
        AlbumArt = snapshot.Track.AlbumArt;
        _trackDurationMs = Math.Max(0, snapshot.Track.DurationMs);
        _progressBaseMs = Math.Clamp(snapshot.Track.ProgressMs, 0, _trackDurationMs == 0 ? int.MaxValue : _trackDurationMs);
        _progressBaseUtc = snapshot.SnapshotUtc;
        ShowProgressBar = _trackDurationMs > 0;
        UpdateProgressPercent();

        if (IsPlaying && ShowProgressBar)
        {
            _progressTimer.Start();
        }
        else
        {
            _progressTimer.Stop();
        }
    }

    private void ProgressTimerOnTick(object? sender, EventArgs e)
    {
        UpdateProgressPercent();
    }

    private void UpdateProgressPercent()
    {
        if (_trackDurationMs <= 0 || !HasTrack)
        {
            ProgressPercent = 0;
            return;
        }

        var progressMs = _progressBaseMs;
        if (IsPlaying)
        {
            progressMs += Math.Max(0, (int)(DateTimeOffset.UtcNow - _progressBaseUtc).TotalMilliseconds);
        }

        progressMs = Math.Clamp(progressMs, 0, _trackDurationMs);
        ProgressPercent = Math.Clamp((double)progressMs / _trackDurationMs * 100.0, 0.0, 100.0);
    }

    private bool CanConnectToSpotify()
    {
        return !IsBusy && IsConfigured;
    }

    private bool CanDisconnectFromSpotify()
    {
        return !IsBusy && IsAuthenticated;
    }

    private bool CanControlSpotifyPlayback()
    {
        return !IsBusy && CanControlPlayback;
    }
}
