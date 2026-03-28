namespace AudioBit.App.Models;

public sealed class SpotifyPlaybackSnapshot
{
    public SpotifyConnectionState ConnectionState { get; init; } = SpotifyConnectionState.Disconnected;

    public SpotifyTrackModel? Track { get; init; }

    public bool IsAuthenticated { get; init; }

    public bool HasActiveDevice { get; init; }

    public bool CanControlPlayback { get; init; }

    public string StatusText { get; init; } = "Spotify not connected";

    public DateTimeOffset SnapshotUtc { get; init; } = DateTimeOffset.UtcNow;

    public string DeviceName { get; init; } = string.Empty;

    public static SpotifyPlaybackSnapshot Create(
        SpotifyConnectionState connectionState,
        bool isAuthenticated,
        bool hasActiveDevice,
        bool canControlPlayback,
        string statusText,
        SpotifyTrackModel? track = null,
        string? deviceName = null)
    {
        return new SpotifyPlaybackSnapshot
        {
            ConnectionState = connectionState,
            IsAuthenticated = isAuthenticated,
            HasActiveDevice = hasActiveDevice,
            CanControlPlayback = canControlPlayback,
            StatusText = statusText,
            Track = track,
            DeviceName = deviceName ?? string.Empty,
            SnapshotUtc = DateTimeOffset.UtcNow,
        };
    }
}
