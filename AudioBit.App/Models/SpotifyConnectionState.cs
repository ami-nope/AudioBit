namespace AudioBit.App.Models;

public enum SpotifyConnectionState
{
    Disconnected,
    Authenticating,
    ConnectedIdle,
    Playing,
    Paused,
    NoActiveDevice,
    RateLimited,
    AuthExpired,
    Error,
}
