using System.Windows.Threading;
using AudioBit.App.Models;
using AudioBit.App.Services;
using AudioBit.App.ViewModels;
using Xunit;

namespace AudioBit.App.Tests;

public sealed class SpotifyViewModelTests
{
    [StaFact]
    public void AppliesDisconnectedState()
    {
        using var service = new FakeSpotifyService();
        using var viewModel = new SpotifyViewModel(service, "0123456789abcdef0123456789abcdef", Dispatcher.CurrentDispatcher);

        service.Publish(SpotifyPlaybackSnapshot.Create(
            SpotifyConnectionState.Disconnected,
            isAuthenticated: false,
            hasActiveDevice: false,
            canControlPlayback: false,
            statusText: "Spotify not connected"));

        Assert.False(viewModel.IsAuthenticated);
        Assert.False(viewModel.HasTrack);
        Assert.Equal("Spotify", viewModel.TrackName);
        Assert.Equal("Spotify not connected", viewModel.ConnectionStatusText);
        Assert.False(viewModel.CanDisconnect);
    }

    [StaFact]
    public void AppliesPlayingStateAndInterpolatesProgress()
    {
        using var service = new FakeSpotifyService();
        using var viewModel = new SpotifyViewModel(service, "0123456789abcdef0123456789abcdef", Dispatcher.CurrentDispatcher);
        service.Publish(new SpotifyPlaybackSnapshot
        {
            ConnectionState = SpotifyConnectionState.Playing,
            IsAuthenticated = true,
            HasActiveDevice = true,
            CanControlPlayback = true,
            StatusText = "Playing on Spotify",
            SnapshotUtc = DateTimeOffset.UtcNow,
            Track = new SpotifyTrackModel
            {
                TrackId = "track-1",
                TrackName = "Dreams",
                ArtistName = "Fleetwood Mac",
                DurationMs = 1000,
                ProgressMs = 250,
                IsPlaying = true,
            },
        });

        var initialProgress = viewModel.ProgressPercent;
        PumpDispatcher(TimeSpan.FromMilliseconds(360));

        Assert.True(viewModel.IsAuthenticated);
        Assert.True(viewModel.HasTrack);
        Assert.True(viewModel.IsPlaying);
        Assert.True(viewModel.ShowProgressBar);
        Assert.True(viewModel.ProgressPercent > initialProgress);
    }

    [StaFact]
    public void UpdatesCommandAvailabilityFromPlaybackState()
    {
        using var service = new FakeSpotifyService();
        using var viewModel = new SpotifyViewModel(service, "0123456789abcdef0123456789abcdef", Dispatcher.CurrentDispatcher);

        Assert.True(viewModel.CanConnect);
        Assert.False(viewModel.CanDisconnect);
        Assert.False(viewModel.PlayPauseCommand.CanExecute(null));

        service.Publish(SpotifyPlaybackSnapshot.Create(
            SpotifyConnectionState.Paused,
            isAuthenticated: true,
            hasActiveDevice: true,
            canControlPlayback: true,
            statusText: "Paused in Spotify",
            track: new SpotifyTrackModel
            {
                TrackId = "track-2",
                TrackName = "Heroes",
                ArtistName = "David Bowie",
                DurationMs = 200000,
                ProgressMs = 1000,
                IsPlaying = false,
            }));

        Assert.True(viewModel.CanDisconnect);
        Assert.True(viewModel.PlayPauseCommand.CanExecute(null));
        Assert.True(viewModel.NextCommand.CanExecute(null));
        Assert.True(viewModel.PreviousCommand.CanExecute(null));
    }

    [StaFact]
    public void DisablesConnectWhenSpotifyClientIdIsMissing()
    {
        using var service = new FakeSpotifyService();
        using var viewModel = new SpotifyViewModel(service, string.Empty, Dispatcher.CurrentDispatcher);

        Assert.False(viewModel.IsConfigured);
        Assert.False(viewModel.CanConnect);
        Assert.False(viewModel.ConnectCommand.CanExecute(null));
    }

    private static void PumpDispatcher(TimeSpan duration)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = duration,
        };
        var frame = new DispatcherFrame();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private sealed class FakeSpotifyService : ISpotifyService
    {
        public event EventHandler<SpotifyPlaybackSnapshot>? PlaybackStateChanged;

        public SpotifyPlaybackSnapshot CurrentSnapshot { get; private set; } = SpotifyPlaybackSnapshot.Create(
            SpotifyConnectionState.Disconnected,
            isAuthenticated: false,
            hasActiveDevice: false,
            canControlPlayback: false,
            statusText: "Spotify not connected");

        public void Publish(SpotifyPlaybackSnapshot snapshot)
        {
            CurrentSnapshot = snapshot;
            PlaybackStateChanged?.Invoke(this, snapshot);
        }

        public Task InitializeAsync(string clientId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ConnectAsync(string clientId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PlayAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PauseAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task NextAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PreviousAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StartPollingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopPollingAsync() => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
