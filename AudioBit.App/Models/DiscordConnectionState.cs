namespace AudioBit.App.Models;

public enum DiscordConnectionState
{
    Disconnected,
    Connecting,
    WaitingForAuthorization,
    Connected,
    Error,
}
