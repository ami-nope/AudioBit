using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using AudioBit.Core;

namespace AudioBit.Installer;

public sealed record InstallerMetadataSnapshot(
    string LatestVersionText,
    double? RatingValue,
    int? RatingCount,
    int? StarsCount,
    string StatusText);

public sealed record InstallerWebsiteStats(
    double? RatingValue,
    int? RatingCount,
    int? StarsCount,
    double? BestRating = null);

public sealed class InstallerMetadataService
{
    public const string GitHubProfileUrl = "https://github.com/ami-nope";
    public const string GitHubRepositoryUrl = "https://github.com/ami-nope/AudioBit";
    public const string WebsiteUrl = "https://audiobit.amii.lol/";

    private static readonly Uri GitHubRepositoryApiUri = new("https://api.github.com/repos/ami-nope/AudioBit");
    private static readonly Uri GitHubLatestReleaseApiUri = new("https://api.github.com/repos/ami-nope/AudioBit/releases/latest");
    private static readonly Uri WebsiteHttpsUri = new(WebsiteUrl);
    private static readonly Uri WebsiteHttpUri = new("http://audiobit.amii.lol/");
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly Regex JsonLdScriptRegex = new(
        "<script[^>]*type=[\"']application/ld\\+json[\"'][^>]*>(?<json>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public async Task<InstallerMetadataSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var latestVersionTask = TryGetLatestReleaseVersionAsync(cancellationToken);
        var gitHubStarsTask = TryGetGitHubStarsAsync(cancellationToken);
        var websiteStatsTask = TryGetWebsiteStatsAsync(cancellationToken);

        var latestVersion = await latestVersionTask.ConfigureAwait(false);
        var gitHubStars = await gitHubStarsTask.ConfigureAwait(false);
        var websiteStats = await websiteStatsTask.ConfigureAwait(false);

        var versionText = string.IsNullOrWhiteSpace(latestVersion)
            ? AppVersionInfo.GetDisplayVersion(Assembly.GetExecutingAssembly())
            : latestVersion;

        var starCount = websiteStats?.StarsCount ?? gitHubStars;
        var statusText = websiteStats is not null
            ? "Live website stats"
            : gitHubStars.HasValue
                ? "Website unavailable, using GitHub stars"
                : "Stats unavailable on this network";

        return new InstallerMetadataSnapshot(
            versionText,
            websiteStats?.RatingValue,
            websiteStats?.RatingCount,
            starCount,
            statusText);
    }

    public static string? ParseLatestReleaseVersion(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (TryGetString(root, "tag_name", out var tagName) && !string.IsNullOrWhiteSpace(tagName))
            {
                return AppVersionInfo.NormalizeForDisplay(tagName);
            }

            if (TryGetString(root, "name", out var releaseName) && !string.IsNullOrWhiteSpace(releaseName))
            {
                return AppVersionInfo.NormalizeForDisplay(releaseName);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static int? ParseGitHubStars(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return TryGetInt(root, "stargazers_count");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static InstallerWebsiteStats? ParseWebsiteStats(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        if (html.Contains("Web Page Blocked", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (Match match in JsonLdScriptRegex.Matches(html))
        {
            var json = match.Groups["json"].Value;
            var stats = TryExtractStatsFromJson(json);
            if (stats is not null)
            {
                return stats;
            }
        }

        return TryExtractStatsFromJson(html) ?? TryExtractStatsFromText(html);
    }

    private static async Task<string?> TryGetLatestReleaseVersionAsync(CancellationToken cancellationToken)
    {
        var json = await TryGetStringAsync(GitHubLatestReleaseApiUri, cancellationToken).ConfigureAwait(false);
        return json is null ? null : ParseLatestReleaseVersion(json);
    }

    private static async Task<int?> TryGetGitHubStarsAsync(CancellationToken cancellationToken)
    {
        var json = await TryGetStringAsync(GitHubRepositoryApiUri, cancellationToken).ConfigureAwait(false);
        return json is null ? null : ParseGitHubStars(json);
    }

    private static async Task<InstallerWebsiteStats?> TryGetWebsiteStatsAsync(CancellationToken cancellationToken)
    {
        var httpsHtml = await TryGetStringAsync(WebsiteHttpsUri, cancellationToken).ConfigureAwait(false);
        var httpsStats = ParseWebsiteStats(httpsHtml ?? string.Empty);
        if (httpsStats is not null)
        {
            return httpsStats;
        }

        var httpHtml = await TryGetStringAsync(WebsiteHttpUri, cancellationToken).ConfigureAwait(false);
        return ParseWebsiteStats(httpHtml ?? string.Empty);
    }

    private static async Task<string?> TryGetStringAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            using var response = await HttpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutCts.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("AudioBit.Setup/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html;q=0.9, */*;q=0.8");
        return client;
    }

    private static InstallerWebsiteStats? TryExtractStatsFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryExtractStatsFromElement(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static InstallerWebsiteStats? TryExtractStatsFromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var ratingValue = TryGetDouble(element, "ratingValue") ?? TryGetDouble(element, "averageRating");
                var ratingCount = TryGetInt(element, "ratingCount")
                    ?? TryGetInt(element, "reviewCount")
                    ?? TryGetInt(element, "ratingsCount");
                var starsCount = TryGetInt(element, "stars")
                    ?? TryGetInt(element, "starCount")
                    ?? TryGetInt(element, "stargazers_count");
                var bestRating = TryGetDouble(element, "bestRating");

                if (element.TryGetProperty("aggregateRating", out var aggregateRating))
                {
                    var aggregateStats = TryExtractStatsFromElement(aggregateRating);
                    if (aggregateStats is not null)
                    {
                        return aggregateStats with
                        {
                            StarsCount = starsCount ?? aggregateStats.StarsCount
                        };
                    }
                }

                if (ratingValue.HasValue || ratingCount.HasValue || starsCount.HasValue)
                {
                    return new InstallerWebsiteStats(ratingValue, ratingCount, starsCount, bestRating);
                }

                foreach (var property in element.EnumerateObject())
                {
                    var nestedStats = TryExtractStatsFromElement(property.Value);
                    if (nestedStats is not null)
                    {
                        return nestedStats;
                    }
                }

                return null;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                {
                    var nestedStats = TryExtractStatsFromElement(item);
                    if (nestedStats is not null)
                    {
                        return nestedStats;
                    }
                }

                return null;
            }
            default:
                return null;
        }
    }

    private static InstallerWebsiteStats? TryExtractStatsFromText(string content)
    {
        var ratingValue = TryMatchDouble(
            content,
            "(?i)(?:ratingValue|averageRating|rating)[^0-9]{0,16}(?<value>\\d+(?:\\.\\d+)?)");
        var ratingCount = TryMatchInt(
            content,
            "(?i)(?:ratingCount|reviewCount|ratingsCount)[^0-9]{0,16}(?<value>[\\d,]+)");
        var starsCount = TryMatchInt(
            content,
            "(?i)(?:starCount|stars|stargazers_count)[^0-9]{0,16}(?<value>[\\d,]+)");

        if (!ratingValue.HasValue && !ratingCount.HasValue && !starsCount.HasValue)
        {
            return null;
        }

        return new InstallerWebsiteStats(ratingValue, ratingCount, starsCount);
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var numericValue) => numericValue,
            JsonValueKind.String when double.TryParse(
                property.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedValue) => parsedValue,
            _ => null,
        };
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var numericValue) => numericValue,
            JsonValueKind.String when int.TryParse(
                property.GetString(),
                NumberStyles.Integer | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var parsedValue) => parsedValue,
            _ => null,
        };
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    private static int? TryMatchInt(string content, string pattern)
    {
        var match = Regex.Match(content, pattern, RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        var rawValue = match.Groups["value"].Value.Replace(",", string.Empty, StringComparison.Ordinal);
        return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static double? TryMatchDouble(string content, string pattern)
    {
        var match = Regex.Match(content, pattern, RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        return double.TryParse(
            match.Groups["value"].Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    private static string FormatCompactNumber(int value)
    {
        if (value >= 1_000_000)
        {
            return $"{value / 1_000_000d:0.#}M";
        }

        if (value >= 1_000)
        {
            return $"{value / 1_000d:0.#}K";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }
}
