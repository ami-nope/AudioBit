using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioBit.Core;

public sealed class ExternalLinksConfiguration
{
    public const string RemoteConfigurationUrl = "https://raw.githubusercontent.com/ami-nope/AudioBit/main/external-links.json";
    private const string DefaultRelayHttp = "https://audiobit-relay-production.up.railway.app/";
    private const string DefaultRelayWs = "wss://audiobit-relay-production.up.railway.app/ws";
    private const string DefaultRemoteConnectBaseUrl = "https://audiobit-remote.vercel.app/connect";
    private const string DefaultGeoIpLookupUrlTemplate = "https://ipapi.co/{ip}/json/";
    private const string DefaultAboutUrl = "https://github.com/ami-nope/AudioBit";

    public static ExternalLinksConfiguration Default { get; } = new(
        relayHttpBaseUri: new Uri(DefaultRelayHttp, UriKind.Absolute),
        relayWebSocketUri: new Uri(DefaultRelayWs, UriKind.Absolute),
        remoteConnectBaseUri: new Uri(DefaultRemoteConnectBaseUrl, UriKind.Absolute),
        geoIpLookupUrlTemplate: DefaultGeoIpLookupUrlTemplate,
        aboutUri: new Uri(DefaultAboutUrl, UriKind.Absolute),
        source: "built-in defaults");

    public ExternalLinksConfiguration(
        Uri relayHttpBaseUri,
        Uri relayWebSocketUri,
        Uri remoteConnectBaseUri,
        string geoIpLookupUrlTemplate,
        Uri aboutUri,
        string source)
    {
        RelayHttpBaseUri = relayHttpBaseUri ?? throw new ArgumentNullException(nameof(relayHttpBaseUri));
        RelayWebSocketUri = relayWebSocketUri ?? throw new ArgumentNullException(nameof(relayWebSocketUri));
        RemoteConnectBaseUri = remoteConnectBaseUri ?? throw new ArgumentNullException(nameof(remoteConnectBaseUri));
        GeoIpLookupUrlTemplate = string.IsNullOrWhiteSpace(geoIpLookupUrlTemplate)
            ? throw new ArgumentException("Geo-IP URL template is required.", nameof(geoIpLookupUrlTemplate))
            : geoIpLookupUrlTemplate;
        AboutUri = aboutUri ?? throw new ArgumentNullException(nameof(aboutUri));
        Source = string.IsNullOrWhiteSpace(source) ? "unknown" : source;
    }

    public Uri RelayHttpBaseUri { get; }

    public Uri RelayWebSocketUri { get; }

    public Uri RemoteConnectBaseUri { get; }

    public string GeoIpLookupUrlTemplate { get; }

    public Uri AboutUri { get; }

    public string Source { get; }
}

public static class ExternalLinksConfigurationLoader
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static ExternalLinksConfiguration Load(string? localFallbackPath = null, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(3);
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero.");
        }

        try
        {
            using var cts = new CancellationTokenSource(effectiveTimeout);
            var remoteConfiguration = TryLoadFromRemoteAsync(cts.Token).GetAwaiter().GetResult();
            if (remoteConfiguration is not null)
            {
                return remoteConfiguration;
            }
        }
        catch
        {
            // Fall back to a bundled file or built-in defaults when remote config is unavailable.
        }

        if (!string.IsNullOrWhiteSpace(localFallbackPath) && File.Exists(localFallbackPath))
        {
            try
            {
                var localJson = File.ReadAllText(localFallbackPath);
                var localConfiguration = TryParseJson(localJson, $"local file '{localFallbackPath}'");
                if (localConfiguration is not null)
                {
                    return localConfiguration;
                }
            }
            catch
            {
                // Ignore local fallback failures and continue to built-in defaults.
            }
        }

        return ExternalLinksConfiguration.Default;
    }

    public static ExternalLinksConfiguration? TryParseJson(string json, string source = "json")
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        ExternalLinksDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ExternalLinksDocument>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document is null)
        {
            return null;
        }

        var defaults = ExternalLinksConfiguration.Default;

        var relayHttpBaseUri = TryReadAbsoluteUri(document.Relay?.HttpBaseUrl, Uri.UriSchemeHttp, Uri.UriSchemeHttps);
        var relayWebSocketUri = TryReadAbsoluteUri(document.Relay?.WsUrl, "ws", "wss");
        relayHttpBaseUri ??= DeriveHttpBaseFromWs(relayWebSocketUri);
        relayWebSocketUri ??= DeriveWsFromHttp(relayHttpBaseUri);
        relayHttpBaseUri ??= defaults.RelayHttpBaseUri;
        relayWebSocketUri ??= defaults.RelayWebSocketUri;

        var remoteConnectBaseUri = TryReadAbsoluteUri(document.RemoteWeb?.ConnectBaseUrl, Uri.UriSchemeHttp, Uri.UriSchemeHttps)
            ?? defaults.RemoteConnectBaseUri;
        var aboutUri = TryReadAbsoluteUri(document.Project?.AboutUrl, Uri.UriSchemeHttp, Uri.UriSchemeHttps)
            ?? defaults.AboutUri;
        var geoIpLookupUrlTemplate = NormalizeGeoIpLookupUrlTemplate(document.Services?.GeoIpLookupUrlTemplate)
            ?? defaults.GeoIpLookupUrlTemplate;

        return new ExternalLinksConfiguration(
            relayHttpBaseUri,
            relayWebSocketUri,
            remoteConnectBaseUri,
            geoIpLookupUrlTemplate,
            aboutUri,
            source);
    }

    private static async Task<ExternalLinksConfiguration?> TryLoadFromRemoteAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ExternalLinksConfiguration.RemoteConfigurationUrl);
        using var response = await HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(contentStream);
        var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return TryParseJson(json, $"remote url '{ExternalLinksConfiguration.RemoteConfigurationUrl}'");
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static Uri? TryReadAbsoluteUri(string? value, params string[] allowedSchemes)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        for (var index = 0; index < allowedSchemes.Length; index++)
        {
            if (string.Equals(uri.Scheme, allowedSchemes[index], StringComparison.OrdinalIgnoreCase))
            {
                return uri;
            }
        }

        return null;
    }

    private static string? NormalizeGeoIpLookupUrlTemplate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.IndexOf("{ip}", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        var sampleUri = normalized.Replace("{ip}", "127.0.0.1", StringComparison.OrdinalIgnoreCase);
        var parsedUri = TryReadAbsoluteUri(sampleUri, Uri.UriSchemeHttp, Uri.UriSchemeHttps);
        return parsedUri is null ? null : normalized;
    }

    private static Uri? DeriveHttpBaseFromWs(Uri? wsUri)
    {
        if (wsUri is null)
        {
            return null;
        }

        if (!string.Equals(wsUri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(wsUri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var builder = new UriBuilder(wsUri)
        {
            Scheme = string.Equals(wsUri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            Port = wsUri.IsDefaultPort ? -1 : wsUri.Port,
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri;
    }

    private static Uri? DeriveWsFromHttp(Uri? httpUri)
    {
        if (httpUri is null)
        {
            return null;
        }

        if (!string.Equals(httpUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(httpUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var builder = new UriBuilder(httpUri)
        {
            Scheme = string.Equals(httpUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Port = httpUri.IsDefaultPort ? -1 : httpUri.Port,
            Path = "/ws",
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri;
    }

    private sealed class ExternalLinksDocument
    {
        [JsonPropertyName("relay")]
        public RelayDocument? Relay { get; init; }

        [JsonPropertyName("remote_web")]
        public RemoteWebDocument? RemoteWeb { get; init; }

        [JsonPropertyName("services")]
        public ServicesDocument? Services { get; init; }

        [JsonPropertyName("project")]
        public ProjectDocument? Project { get; init; }
    }

    private sealed class RelayDocument
    {
        [JsonPropertyName("http_base_url")]
        public string? HttpBaseUrl { get; init; }

        [JsonPropertyName("ws_url")]
        public string? WsUrl { get; init; }
    }

    private sealed class RemoteWebDocument
    {
        [JsonPropertyName("connect_base_url")]
        public string? ConnectBaseUrl { get; init; }
    }

    private sealed class ServicesDocument
    {
        [JsonPropertyName("geo_ip_lookup_url_template")]
        public string? GeoIpLookupUrlTemplate { get; init; }
    }

    private sealed class ProjectDocument
    {
        [JsonPropertyName("about_url")]
        public string? AboutUrl { get; init; }
    }
}
