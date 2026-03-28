using System.IO;

namespace AudioBit.App.Infrastructure;

internal static class SpotifyClientIdResolver
{
    // Production builds can use this built-in Spotify Web API client ID.
    private const string BuiltInClientId = "ee2c218f24834f1bb7ed892210193e68";
    private const string PrimaryClientIdKey = "AUDIOBIT_SPOTIFY_CLIENT_ID";
    private const string FallbackClientIdKey = "SPOTIFY_CLIENT_ID";

    public static string Resolve(AppSettingsStore appSettingsStore, SpotifyAuthStateStore authStateStore)
    {
        ArgumentNullException.ThrowIfNull(appSettingsStore);
        ArgumentNullException.ThrowIfNull(authStateStore);

        var legacySettings = appSettingsStore.Load();
        return ResolveCandidate(BuiltInClientId)
            ?? ResolveCandidate(Environment.GetEnvironmentVariable(PrimaryClientIdKey))
            ?? ResolveCandidate(Environment.GetEnvironmentVariable(FallbackClientIdKey))
            ?? ResolveCandidate(LoadDotEnvValue(PrimaryClientIdKey))
            ?? ResolveCandidate(LoadDotEnvValue(FallbackClientIdKey))
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

    private static string? LoadDotEnvValue(string key)
    {
        foreach (var path in EnumerateDotEnvPaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                foreach (var rawLine in File.ReadLines(path))
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    {
                        continue;
                    }

                    if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
                    {
                        line = line[7..].TrimStart();
                    }

                    var separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    var currentKey = line[..separatorIndex].Trim();
                    if (!string.Equals(currentKey, key, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var value = line[(separatorIndex + 1)..].Trim();
                    if (value.Length >= 2
                        && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                    {
                        value = value[1..^1];
                    }

                    return value;
                }
            }
            catch
            {
                // Ignore malformed or inaccessible dotenv files and continue to the next source.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateDotEnvPaths()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var startDirectory in new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory,
                 })
        {
            if (string.IsNullOrWhiteSpace(startDirectory))
            {
                continue;
            }

            var directory = new DirectoryInfo(startDirectory);
            while (directory is not null)
            {
                var path = Path.Combine(directory.FullName, ".env");
                if (seen.Add(path))
                {
                    yield return path;
                }

                directory = directory.Parent;
            }
        }
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
