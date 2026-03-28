namespace AudioBit.App.Models;

public sealed class SpotifyTokenState
{
    public string ClientId { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    public string Scope { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MinValue;

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(AccessToken)
        || string.IsNullOrWhiteSpace(RefreshToken);
}
