using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AudioBit.App.Models;

namespace AudioBit.App.Services;

internal sealed class RemoteSessionManager : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Action<string> _log;
    private readonly object _syncRoot = new();

    private Uri _httpBaseUri;

    private bool _disposed;

    public RemoteSessionManager(Uri httpBaseUri, Action<string> log)
    {
        _log = log;
        _httpBaseUri = httpBaseUri;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
    }

    public RemoteSessionInfo CurrentSession { get; private set; } = RemoteSessionInfo.Empty;

    public Uri HttpBaseUri
    {
        get
        {
            lock (_syncRoot)
            {
                return _httpBaseUri;
            }
        }
    }

    public void SetHttpBaseUri(Uri httpBaseUri)
    {
        ThrowIfDisposed();

        lock (_syncRoot)
        {
            _httpBaseUri = httpBaseUri;
        }
    }

    public async Task<RemoteSessionInfo> CreateSessionAsync(
        CancellationToken cancellationToken,
        RemoteSessionRequest? sessionRequest = null)
    {
        ThrowIfDisposed();

        Uri httpBaseUri;
        lock (_syncRoot)
        {
            httpBaseUri = _httpBaseUri;
        }

        var session = await CreateSessionAsync(httpBaseUri, cancellationToken, sessionRequest).ConfigureAwait(false);
        CurrentSession = session;
        return session;
    }

    public async Task<RemoteSessionInfo> CreateSessionAsync(
        Uri httpBaseUri,
        CancellationToken cancellationToken,
        RemoteSessionRequest? sessionRequest = null)
    {
        ThrowIfDisposed();

        var createSessionUri = new Uri(httpBaseUri, "create-session");
        var payloadJson = BuildCreateSessionPayload(sessionRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, createSessionUri)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var payloadStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(payloadStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        var session = new RemoteSessionInfo
        {
            SessionId = ReadString(root, "sid"),
            PairCode = ReadString(root, "pair_code"),
            ExpiresAtUtc = ReadTimestamp(root, "expires"),
            QrUrl = ReadString(root, "qr_url"),
            IsRelayConnected = false,
            IsConnected = false,
            Status = "Pairing session created",
        };

        _log($"Session created: sid={session.SessionId}, code={session.PairCode}, relay={httpBaseUri}");
        return session;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return DateTimeOffset.UtcNow.AddMinutes(10);
        }

        if (property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out var numeric))
        {
            return numeric > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(numeric)
                : DateTimeOffset.FromUnixTimeSeconds(numeric);
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var raw = property.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return DateTimeOffset.UtcNow.AddMinutes(10);
            }

            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToUniversalTime();
            }

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNumeric))
            {
                return parsedNumeric > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(parsedNumeric)
                    : DateTimeOffset.FromUnixTimeSeconds(parsedNumeric);
            }
        }

        return DateTimeOffset.UtcNow.AddMinutes(10);
    }

    private static string BuildCreateSessionPayload(RemoteSessionRequest? sessionRequest)
    {
        if (!sessionRequest.HasValue || sessionRequest.Value.IsEmpty)
        {
            return "{}";
        }

        var payload = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(sessionRequest.Value.SessionId))
        {
            payload["sid"] = sessionRequest.Value.SessionId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(sessionRequest.Value.PairCode))
        {
            payload["pair_code"] = sessionRequest.Value.PairCode.Trim();
        }

        return JsonSerializer.Serialize(payload);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
