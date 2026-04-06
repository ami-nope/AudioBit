namespace AudioBit.App.Infrastructure;

internal static class DiscordClientIdResolver
{
    private const string BuiltInClientId = "1490427586525003806";
    private const string BuiltInClientSecret = "sMwnScdBA2Pwm623n-KDWpUz05uMyulR";
    private const string BuiltInRedirectUri = "http://127.0.0.1";

    public static string ResolveClientId()
    {
        return BuiltInClientId;
    }

    public static string ResolveClientSecret()
    {
        return BuiltInClientSecret;
    }

    public static string ResolveRedirectUri()
    {
        return BuiltInRedirectUri;
    }

    public static bool IsConfigured(string? clientId)
    {
        return !string.IsNullOrWhiteSpace(clientId?.Trim());
    }
}
