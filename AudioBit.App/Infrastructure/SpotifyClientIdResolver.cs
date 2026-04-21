namespace AudioBit.App.Infrastructure;

internal static class SpotifyClientIdResolver
{
    
    private const string BuiltInClientId = "ee2c218f24834f1bb7ed892210193e68";

    public static string Resolve(AppSettingsStore appSettingsStore, SpotifyAuthStateStore authStateStore)
    {
        ArgumentNullException.ThrowIfNull(appSettingsStore);
        ArgumentNullException.ThrowIfNull(authStateStore);

        var legacySettings = appSettingsStore.Load();
        return ResolveCandidate(BuiltInClientId)
            ?? ResolveCandidate(authStateStore.Load()?.ClientId)
            ?? ResolveCandidate(legacySettings.SpotifyClientId)
            ?? string.Empty;
    }

    public static bool IsConfigured(string? clientId)
    {
        return ResolveCandidate(clientId) is not null;
    }

    public static string Normalize(string? clientId)
    {
        return NormalizeCore(clientId, allowEmptyFallback: false) ?? string.Empty;
    }

    private static string? ResolveCandidate(string? clientId)
    {
        return NormalizeCore(clientId, allowEmptyFallback: false);
    }

    private static string? NormalizeCore(string? clientId, bool allowEmptyFallback)
    {
        var trimmed = clientId?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return allowEmptyFallback ? string.Empty : null;
        }

        if (trimmed.Length != 32 || !trimmed.All(char.IsLetterOrDigit))
        {
            return null;
        }

        return trimmed;
    }
}
