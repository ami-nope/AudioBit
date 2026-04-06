namespace AudioBit.App.Models;

public sealed class DiscordTokenState
{
    public string AccessToken { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MinValue;

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(AccessToken)
        || string.IsNullOrWhiteSpace(ClientId);

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAtUtc;
}
