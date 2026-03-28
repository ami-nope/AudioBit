using AudioBit.App.Models;

namespace AudioBit.App.Services;

public interface ISpotifyService : IDisposable
{
    event EventHandler<SpotifyPlaybackSnapshot>? PlaybackStateChanged;

    SpotifyPlaybackSnapshot CurrentSnapshot { get; }

    Task InitializeAsync(string clientId, CancellationToken cancellationToken);

    Task ConnectAsync(string clientId, CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    Task PlayAsync(CancellationToken cancellationToken);

    Task PauseAsync(CancellationToken cancellationToken);

    Task NextAsync(CancellationToken cancellationToken);

    Task PreviousAsync(CancellationToken cancellationToken);

    Task StartPollingAsync(CancellationToken cancellationToken);

    Task StopPollingAsync();
}
