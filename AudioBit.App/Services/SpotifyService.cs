using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioBit.App.Infrastructure;
using AudioBit.App.Models;

namespace AudioBit.App.Services;

public sealed class SpotifyService : ISpotifyService
{
    private const string AuthorizeEndpoint = "https://accounts.spotify.com/authorize";
    private const string TokenEndpoint = "https://accounts.spotify.com/api/token";
    private const string CurrentPlaybackEndpoint = "https://api.spotify.com/v1/me/player/currently-playing";
    private const string PlaybackStateEndpoint = "https://api.spotify.com/v1/me/player";
    private const string PlayEndpoint = "https://api.spotify.com/v1/me/player/play";
    private const string PauseEndpoint = "https://api.spotify.com/v1/me/player/pause";
    private const string NextEndpoint = "https://api.spotify.com/v1/me/player/next";
    private const string PreviousEndpoint = "https://api.spotify.com/v1/me/player/previous";
    private const string SpotifyScope = "user-read-currently-playing user-read-playback-state user-modify-playback-state";
    private const int AlbumArtCacheLimit = 32;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan AuthCallbackTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(30);
    private const string NotConfiguredStatusText = "Spotify login is not configured in this build.";
    private static readonly SpotifyPlaybackSnapshot DisconnectedSnapshot = SpotifyPlaybackSnapshot.Create(
        SpotifyConnectionState.Disconnected,
        isAuthenticated: false,
        hasActiveDevice: false,
        canControlPlayback: false,
        statusText: "Spotify not connected");

    private readonly SpotifyAuthStateStore _authStateStore;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, ImageSource> _albumArtCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _albumArtCacheOrder = new();
    private readonly string _logFilePath = Path.Combine(AudioBitPaths.LogsDirectoryPath, "spotify-service.log");

    private CancellationTokenSource? _pollingCts;
    private Task? _pollingTask;
    private SpotifyTokenState? _tokenState;
    private string _clientId = string.Empty;
    private bool _disposed;
    private DateTimeOffset _pollBackoffUntilUtc = DateTimeOffset.MinValue;
    private SpotifyPlaybackSnapshot _currentSnapshot = DisconnectedSnapshot;

    public SpotifyService(SpotifyAuthStateStore authStateStore, HttpClient httpClient)
    {
        _authStateStore = authStateStore ?? throw new ArgumentNullException(nameof(authStateStore));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_httpClient.Timeout == Timeout.InfiniteTimeSpan)
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(12);
        }
    }

    public static string RedirectUri => "http://127.0.0.1:43871/spotify/callback/";

    public event EventHandler<SpotifyPlaybackSnapshot>? PlaybackStateChanged;

    public SpotifyPlaybackSnapshot CurrentSnapshot => _currentSnapshot;

    public static string CreateCodeVerifier()
    {
        Span<byte> bytes = stackalloc byte[64];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string CreateCodeChallenge(string verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);

        var bytes = Encoding.ASCII.GetBytes(verifier);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryParseAuthorizeCallback(
        Uri? callbackUri,
        string expectedState,
        out string? code,
        out string? errorDescription)
    {
        code = null;
        errorDescription = null;

        if (callbackUri is null)
        {
            errorDescription = "Spotify sign-in failed.";
            return false;
        }

        var parameters = ParseQueryString(callbackUri.Query);
        if (parameters.TryGetValue("error", out var error))
        {
            errorDescription = string.Equals(error, "access_denied", StringComparison.OrdinalIgnoreCase)
                ? "Spotify sign-in was canceled."
                : "Spotify sign-in failed.";
            return false;
        }

        if (!parameters.TryGetValue("state", out var state)
            || !string.Equals(state, expectedState, StringComparison.Ordinal))
        {
            errorDescription = "Spotify sign-in failed.";
            return false;
        }

        if (!parameters.TryGetValue("code", out code)
            || string.IsNullOrWhiteSpace(code))
        {
            errorDescription = "Spotify sign-in failed.";
            return false;
        }

        return true;
    }

    public static bool TryParseRetryAfterSeconds(HttpResponseMessage response, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds));
            return true;
        }

        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var value = values.FirstOrDefault();
            if (int.TryParse(value, out retryAfterSeconds))
            {
                retryAfterSeconds = Math.Max(1, retryAfterSeconds);
                return true;
            }
        }

        return false;
    }

    public static SpotifyPlaybackSnapshot ParsePlaybackStateJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var document = JsonDocument.Parse(json);
        return ParsePlaybackStateElement(document.RootElement);
    }

    public async Task InitializeAsync(string clientId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        clientId = NormalizeClientId(clientId);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _clientId = clientId;
            _tokenState = null;
        }
        finally
        {
            _stateGate.Release();
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            PublishSnapshot(SpotifyPlaybackSnapshot.Create(
                SpotifyConnectionState.Error,
                isAuthenticated: false,
                hasActiveDevice: false,
                canControlPlayback: false,
                statusText: NotConfiguredStatusText));
            return;
        }

        var storedState = _authStateStore.Load();
        if (storedState is null
            || storedState.IsEmpty
            || !string.Equals(storedState.ClientId, clientId, StringComparison.Ordinal))
        {
            PublishSnapshot(DisconnectedSnapshot);
            return;
        }

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _tokenState = storedState;
        }
        finally
        {
            _stateGate.Release();
        }

        if (IsTokenExpiring(storedState))
        {
            var refreshed = await TryRefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed)
            {
                return;
            }
        }

        PublishSnapshot(SpotifyPlaybackSnapshot.Create(
            SpotifyConnectionState.ConnectedIdle,
            isAuthenticated: true,
            hasActiveDevice: false,
            canControlPlayback: false,
            statusText: "Open Spotify on a device"));
    }

    public async Task ConnectAsync(string clientId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        clientId = NormalizeClientId(clientId);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            PublishSnapshot(SpotifyPlaybackSnapshot.Create(
                SpotifyConnectionState.Error,
                isAuthenticated: false,
                hasActiveDevice: false,
                canControlPlayback: false,
                statusText: NotConfiguredStatusText));
            return;
        }

        await StopPollingAsync().ConfigureAwait(false);
        PublishSnapshot(SpotifyPlaybackSnapshot.Create(
            SpotifyConnectionState.Authenticating,
            isAuthenticated: false,
            hasActiveDevice: false,
            canControlPlayback: false,
            statusText: "Waiting for Spotify sign-in..."));

        var codeVerifier = CreateCodeVerifier();
        var codeChallenge = CreateCodeChallenge(codeVerifier);
        var state = Guid.NewGuid().ToString("N");

        using var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add(RedirectUri);
            listener.Start();
        }
        catch (Exception ex)
        {
            Log($"Failed to start Spotify callback listener: {ex}");
            PublishSnapshot(SpotifyPlaybackSnapshot.Create(
                SpotifyConnectionState.Error,
                isAuthenticated: false,
                hasActiveDevice: false,
                canControlPlayback: false,
                statusText: "Spotify sign-in failed."));
            return;
        }

        var authorizeUrl = BuildAuthorizeUrl(clientId, codeChallenge, state);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = authorizeUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log($"Failed to launch Spotify auth browser: {ex}");
            PublishSnapshot(DisconnectedSnapshot);
            return;
        }

        HttpListenerContext context;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(AuthCallbackTimeout);
            context = await listener.GetContextAsync().WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PublishSnapshot(DisconnectedSnapshot);
            return;
        }
        catch (Exception ex)
        {
            Log($"Spotify callback failed: {ex}");
            PublishSnapshot(SpotifyPlaybackSnapshot.Create(
                SpotifyConnectionState.Error,
                isAuthenticated: false,
                hasActiveDevice: false,
                canControlPlayback: false,
                statusText: "Spotify sign-in failed."));
            return;
        }

        if (!TryParseAuthorizeCallback(context.Request.Url, state, out var code, out var errorDescription))
        {
            await WriteBrowserResponseAsync(context.Response, BuildBrowserResponse(errorDescription ?? "Spotify sign-in failed.")).ConfigureAwait(false);
            PublishSnapshot(string.Equals(errorDescription, "Spotify sign-in was canceled.", StringComparison.Ordinal)
                ? DisconnectedSnapshot
                : SpotifyPlaybackSnapshot.Create(
                    SpotifyConnectionState.Error,
                    isAuthenticated: false,
                    hasActiveDevice: false,
                    canControlPlayback: false,
                    statusText: errorDescription ?? "Spotify sign-in failed."));
            return;
        }

        var tokenState = await ExchangeCodeForTokenAsync(clientId, code!, codeVerifier, cancellationToken).ConfigureAwait(false);
        if (tokenState is null)
        {
            await WriteBrowserResponseAsync(context.Response, BuildBrowserResponse("Spotify sign-in failed.")).ConfigureAwait(false);
            PublishSnapshot(SpotifyPlaybackSnapshot.Create(
                SpotifyConnectionState.Error,
                isAuthenticated: false,
                hasActiveDevice: false,
                canControlPlayback: false,
                statusText: "Reconnect Spotify in Settings"));
            return;
        }

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _clientId = clientId;
            _tokenState = tokenState;
            _pollBackoffUntilUtc = DateTimeOffset.MinValue;
            _authStateStore.Save(tokenState);
        }
        finally
        {
            _stateGate.Release();
        }

        await WriteBrowserResponseAsync(context.Response, BuildBrowserResponse("Spotify connected. You can return to AudioBit.")).ConfigureAwait(false);
        PublishSnapshot(SpotifyPlaybackSnapshot.Create(
            SpotifyConnectionState.ConnectedIdle,
            isAuthenticated: true,
            hasActiveDevice: false,
            canControlPlayback: false,
            statusText: "Open Spotify on a device"));
        await StartPollingAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await StopPollingAsync().ConfigureAwait(false);

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _tokenState = null;
            _pollBackoffUntilUtc = DateTimeOffset.MinValue;
            _albumArtCache.Clear();
            _albumArtCacheOrder.Clear();
            _authStateStore.Clear();
        }
        finally
        {
            _stateGate.Release();
        }

        PublishSnapshot(DisconnectedSnapshot);
    }

    public Task PlayAsync(CancellationToken cancellationToken)
    {
        return SendPlayerCommandAsync(HttpMethod.Put, PlayEndpoint, cancellationToken);
    }

    public Task PauseAsync(CancellationToken cancellationToken)
    {
        return SendPlayerCommandAsync(HttpMethod.Put, PauseEndpoint, cancellationToken);
    }

    public Task NextAsync(CancellationToken cancellationToken)
    {
        return SendPlayerCommandAsync(HttpMethod.Post, NextEndpoint, cancellationToken);
    }

    public Task PreviousAsync(CancellationToken cancellationToken)
    {
        return SendPlayerCommandAsync(HttpMethod.Post, PreviousEndpoint, cancellationToken);
    }

    public async Task StartPollingAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokenState is null || _tokenState.IsEmpty)
            {
                return;
            }

            if (_pollingTask is not null && !_pollingTask.IsCompleted)
            {
                return;
            }

            _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pollingTask = PollLoopAsync(_pollingCts.Token);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task StopPollingAsync()
    {
        CancellationTokenSource? pollingCts;
        Task? pollingTask;

        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            pollingCts = _pollingCts;
            pollingTask = _pollingTask;
            _pollingCts = null;
            _pollingTask = null;
        }
        finally
        {
            _stateGate.Release();
        }

        if (pollingCts is null)
        {
            return;
        }

        try
        {
            pollingCts.Cancel();
            if (pollingTask is not null)
            {
                await pollingTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"Spotify polling stopped with error: {ex}");
        }
        finally
        {
            pollingCts.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var pollingCts = _pollingCts;
        _pollingCts = null;
        _pollingTask = null;
        _disposed = true;
        pollingCts?.Cancel();
        pollingCts?.Dispose();
        _stateGate.Dispose();
        _refreshGate.Dispose();
        _httpClient.Dispose();
    }

    private static SpotifyPlaybackSnapshot ParsePlaybackStateElement(JsonElement root)
    {
        var deviceName = TryGetNestedString(root, "device", "name");
        var hasDeviceNode = root.TryGetProperty("device", out var deviceElement) && deviceElement.ValueKind == JsonValueKind.Object;

        if (TryGetProperty(root, "item", out var itemElement) && itemElement.ValueKind == JsonValueKind.Object)
        {
            var isPlaying = TryGetBoolean(root, "is_playing");
            var track = new SpotifyTrackModel
            {
                TrackId = TryGetString(itemElement, "id") ?? string.Empty,
                TrackName = TryGetString(itemElement, "name") ?? "Unknown track",
                ArtistName = ReadArtistNames(itemElement),
                AlbumName = TryGetNestedString(itemElement, "album", "name") ?? string.Empty,
                AlbumArtUrl = ReadAlbumArtUrl(itemElement),
                DurationMs = TryGetInt32(itemElement, "duration_ms"),
                ProgressMs = TryGetInt32(root, "progress_ms"),
                IsPlaying = isPlaying,
            };

            return SpotifyPlaybackSnapshot.Create(
                isPlaying ? SpotifyConnectionState.Playing : SpotifyConnectionState.Paused,
                isAuthenticated: true,
                hasActiveDevice: true,
                canControlPlayback: true,
                statusText: isPlaying ? "Playing on Spotify" : "Paused in Spotify",
                track: track,
                deviceName: deviceName);
        }

        if (hasDeviceNode)
        {
            return SpotifyPlaybackSnapshot.Create(
                SpotifyConnectionState.ConnectedIdle,
                isAuthenticated: true,
                hasActiveDevice: true,
                canControlPlayback: true,
                statusText: "Nothing playing",
                deviceName: deviceName);
        }

        return SpotifyPlaybackSnapshot.Create(
            SpotifyConnectionState.NoActiveDevice,
            isAuthenticated: true,
            hasActiveDevice: false,
            canControlPlayback: false,
            statusText: "Open Spotify on a device");
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        await RefreshPlaybackSnapshotAsync(cancellationToken).ConfigureAwait(false);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_pollBackoffUntilUtc > DateTimeOffset.UtcNow)
            {
                continue;
            }

            await RefreshPlaybackSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshPlaybackSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAuthorizedAsync(
                () => new HttpRequestMessage(HttpMethod.Get, CurrentPlaybackEndpoint),
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NoContent)
            {
                await RefreshIdleStateAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (response.StatusCode == (HttpStatusCode)429)
            {
                HandleRateLimited(response);
                return;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await HandleAuthenticationExpiredAsync().ConfigureAwait(false);
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                PublishSnapshot(SpotifyPlaybackSnapshot.Create(
                    SpotifyConnectionState.Error,
                    isAuthenticated: true,
                    hasActiveDevice: CurrentSnapshot.HasActiveDevice,
                    canControlPlayback: CurrentSnapshot.CanControlPlayback,
                    statusText: "Spotify unavailable"));
                return;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = ParsePlaybackStateJson(json);
            if (snapshot.Track is not null)
            {
                snapshot.Track.AlbumArt = await GetAlbumArtAsync(snapshot.Track.AlbumArtUrl, cancellationToken).ConfigureAwait(false);
            }

            PublishSnapshot(snapshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"Spotify polling error: {ex}");
            PublishSnapshot(SpotifyPlaybackSnapshot.Create(
                SpotifyConnectionState.Error,
                isAuthenticated: _tokenState is not null,
                hasActiveDevice: CurrentSnapshot.HasActiveDevice,
                canControlPlayback: CurrentSnapshot.CanControlPlayback,
                statusText: "Spotify unavailable",
                track: CurrentSnapshot.Track,
                deviceName: CurrentSnapshot.DeviceName));
        }
    }

    private async Task RefreshIdleStateAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAuthorizedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, PlaybackStateEndpoint),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            PublishSnapshot(SpotifyPlaybackSnapshot.Create(
                SpotifyConnectionState.NoActiveDevice,
                isAuthenticated: true,
                hasActiveDevice: false,
                canControlPlayback: false,
                statusText: "Open Spotify on a device"));
            return;
        }

        if (response.StatusCode == (HttpStatusCode)429)
        {
            HandleRateLimited(response);
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleAuthenticationExpiredAsync().ConfigureAwait(false);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            PublishSnapshot(SpotifyPlaybackSnapshot.Create(
                SpotifyConnectionState.NoActiveDevice,
                isAuthenticated: true,
                hasActiveDevice: false,
                canControlPlayback: false,
                statusText: "Open Spotify on a device"));
            return;
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = ParsePlaybackStateJson(json);
        if (snapshot.Track is not null)
        {
            snapshot.Track.AlbumArt = await GetAlbumArtAsync(snapshot.Track.AlbumArtUrl, cancellationToken).ConfigureAwait(false);
        }

        PublishSnapshot(snapshot);
    }

    private async Task SendPlayerCommandAsync(HttpMethod method, string endpoint, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (_tokenState is null || _tokenState.IsEmpty)
        {
            PublishSnapshot(DisconnectedSnapshot);
            return;
        }

        try
        {
            using var response = await SendAuthorizedAsync(
                () => new HttpRequestMessage(method, endpoint),
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == (HttpStatusCode)429)
            {
                HandleRateLimited(response);
                return;
            }

            if (response.StatusCode == HttpStatusCode.NotFound
                || response.StatusCode == HttpStatusCode.Forbidden
                || response.StatusCode == HttpStatusCode.BadRequest)
            {
                PublishSnapshot(SpotifyPlaybackSnapshot.Create(
                    SpotifyConnectionState.NoActiveDevice,
                    isAuthenticated: true,
                    hasActiveDevice: false,
                    canControlPlayback: false,
                    statusText: "Open Spotify on a device"));
                return;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                await HandleAuthenticationExpiredAsync().ConfigureAwait(false);
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                PublishSnapshot(SpotifyPlaybackSnapshot.Create(
                    SpotifyConnectionState.Error,
                    isAuthenticated: true,
                    hasActiveDevice: CurrentSnapshot.HasActiveDevice,
                    canControlPlayback: CurrentSnapshot.CanControlPlayback,
                    statusText: "Spotify unavailable",
                    track: CurrentSnapshot.Track,
                    deviceName: CurrentSnapshot.DeviceName));
                return;
            }

            await RefreshPlaybackSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"Spotify player command failed: {ex}");
            PublishSnapshot(SpotifyPlaybackSnapshot.Create(
                SpotifyConnectionState.Error,
                isAuthenticated: true,
                hasActiveDevice: CurrentSnapshot.HasActiveDevice,
                canControlPlayback: CurrentSnapshot.CanControlPlayback,
                statusText: "Spotify unavailable",
                track: CurrentSnapshot.Track,
                deviceName: CurrentSnapshot.DeviceName));
        }
    }

    private Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        return SendAuthorizedAsyncImpl(requestFactory, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsyncImpl(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        await EnsureAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var accessToken = _tokenState?.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        using var request = requestFactory();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        var refreshed = await TryRefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!refreshed)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        using var retryRequest = requestFactory();
        retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenState?.AccessToken);
        return await _httpClient.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        var tokenState = _tokenState;
        if (tokenState is null || !IsTokenExpiring(tokenState))
        {
            return;
        }

        await TryRefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryRefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokenState is null || _tokenState.IsEmpty || string.IsNullOrWhiteSpace(_clientId))
            {
                return false;
            }

            if (!IsTokenExpiring(_tokenState))
            {
                return true;
            }

            var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", _clientId),
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", _tokenState.RefreshToken),
            ]);

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = content,
            };

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log($"Spotify token refresh failed with status {(int)response.StatusCode}.");
                await HandleAuthenticationExpiredAsync().ConfigureAwait(false);
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var accessToken = TryGetString(root, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                await HandleAuthenticationExpiredAsync().ConfigureAwait(false);
                return false;
            }

            _tokenState.AccessToken = accessToken;
            _tokenState.Scope = TryGetString(root, "scope") ?? _tokenState.Scope;
            _tokenState.RefreshToken = TryGetString(root, "refresh_token") ?? _tokenState.RefreshToken;
            _tokenState.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, TryGetInt32(root, "expires_in")));
            _authStateStore.Save(_tokenState);
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<SpotifyTokenState?> ExchangeCodeForTokenAsync(
        string clientId,
        string code,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", RedirectUri),
                new KeyValuePair<string, string>("code_verifier", codeVerifier),
            ]);

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = content,
            };

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Log($"Spotify token exchange failed with status {(int)response.StatusCode}.");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var accessToken = TryGetString(root, "access_token");
            var refreshToken = TryGetString(root, "refresh_token");
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
            {
                return null;
            }

            return new SpotifyTokenState
            {
                ClientId = clientId,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                Scope = TryGetString(root, "scope") ?? SpotifyScope,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, TryGetInt32(root, "expires_in"))),
            };
        }
        catch (Exception ex)
        {
            Log($"Spotify token exchange threw: {ex}");
            return null;
        }
    }

    private async Task<ImageSource?> GetAlbumArtAsync(string albumArtUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(albumArtUrl))
        {
            return null;
        }

        if (_albumArtCache.TryGetValue(albumArtUrl, out var cached))
        {
            return cached;
        }

        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(albumArtUrl, cancellationToken).ConfigureAwait(false);
            await using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            AddAlbumArtToCache(albumArtUrl, image);
            return image;
        }
        catch (Exception ex)
        {
            Log($"Spotify album art download failed: {ex}");
            return null;
        }
    }

    private void AddAlbumArtToCache(string albumArtUrl, ImageSource image)
    {
        if (_albumArtCache.ContainsKey(albumArtUrl))
        {
            return;
        }

        _albumArtCache[albumArtUrl] = image;
        _albumArtCacheOrder.Enqueue(albumArtUrl);

        while (_albumArtCache.Count > AlbumArtCacheLimit && _albumArtCacheOrder.Count > 0)
        {
            var oldestKey = _albumArtCacheOrder.Dequeue();
            _albumArtCache.Remove(oldestKey);
        }
    }

    private async Task HandleAuthenticationExpiredAsync()
    {
        await StopPollingAsync().ConfigureAwait(false);

        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _tokenState = null;
            _authStateStore.Clear();
        }
        finally
        {
            _stateGate.Release();
        }

        PublishSnapshot(SpotifyPlaybackSnapshot.Create(
            SpotifyConnectionState.AuthExpired,
            isAuthenticated: false,
            hasActiveDevice: false,
            canControlPlayback: false,
            statusText: "Reconnect Spotify in Settings"));
    }

    private void HandleRateLimited(HttpResponseMessage response)
    {
        if (!TryParseRetryAfterSeconds(response, out var retryAfterSeconds))
        {
            retryAfterSeconds = Math.Max(2, (int)Math.Ceiling(PollInterval.TotalSeconds));
        }

        _pollBackoffUntilUtc = DateTimeOffset.UtcNow.AddSeconds(retryAfterSeconds);
        PublishSnapshot(SpotifyPlaybackSnapshot.Create(
            SpotifyConnectionState.RateLimited,
            isAuthenticated: true,
            hasActiveDevice: CurrentSnapshot.HasActiveDevice,
            canControlPlayback: CurrentSnapshot.CanControlPlayback,
            statusText: "Rate limited, retrying...",
            track: CurrentSnapshot.Track,
            deviceName: CurrentSnapshot.DeviceName));
    }

    private static bool IsTokenExpiring(SpotifyTokenState tokenState)
    {
        return tokenState.ExpiresAtUtc <= DateTimeOffset.UtcNow.Add(ExpirySkew);
    }

    private static string BuildAuthorizeUrl(string clientId, string codeChallenge, string state)
    {
        return $"{AuthorizeEndpoint}?{BuildQueryString(
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("response_type", "code"),
            new KeyValuePair<string, string>("redirect_uri", RedirectUri),
            new KeyValuePair<string, string>("code_challenge_method", "S256"),
            new KeyValuePair<string, string>("code_challenge", codeChallenge),
            new KeyValuePair<string, string>("scope", SpotifyScope),
            new KeyValuePair<string, string>("state", state))}";
    }

    private static string BuildQueryString(params KeyValuePair<string, string>[] parameters)
    {
        return string.Join(
            "&",
            parameters.Select(parameter =>
                $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(query))
        {
            return result;
        }

        var trimmedQuery = query.TrimStart('?');
        foreach (var pair in trimmedQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            var key = separatorIndex >= 0 ? pair[..separatorIndex] : pair;
            var value = separatorIndex >= 0 ? pair[(separatorIndex + 1)..] : string.Empty;
            result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }

        return result;
    }

    private static string BuildBrowserResponse(string message)
    {
        var encodedMessage = WebUtility.HtmlEncode(message);
        return $$"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                    <meta charset="utf-8" />
                    <title>AudioBit Spotify</title>
                    <style>
                        body { font-family: "Segoe UI", sans-serif; background: #11131A; color: #F4F2F8; margin: 0; padding: 32px; }
                        .card { max-width: 420px; margin: 32px auto; padding: 24px; border-radius: 20px; background: linear-gradient(145deg, #232632, #1A1D27); border: 1px solid #303444; }
                        .title { font-size: 14px; color: #1DB954; text-transform: uppercase; letter-spacing: 0.08em; }
                        .message { margin-top: 12px; font-size: 18px; font-weight: 600; }
                    </style>
                </head>
                <body>
                    <div class="card">
                        <div class="title">AudioBit</div>
                        <div class="message">{{encodedMessage}}</div>
                    </div>
                </body>
                </html>
                """;
    }

    private static Task WriteBrowserResponseAsync(HttpListenerResponse response, string html)
    {
        return WriteBrowserResponseCoreAsync(response, html);
    }

    private static async Task WriteBrowserResponseCoreAsync(HttpListenerResponse response, string html)
    {
        var buffer = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer).ConfigureAwait(false);
        response.OutputStream.Close();
    }

    private static string NormalizeClientId(string? clientId)
    {
        return SpotifyClientIdResolver.Normalize(clientId);
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out property))
        {
            return true;
        }

        property = default;
        return false;
    }

    private static bool TryGetBoolean(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            && property.GetBoolean();
    }

    private static int TryGetInt32(JsonElement element, string propertyName)
    {
        if (TryGetProperty(element, propertyName, out var property) && property.TryGetInt32(out var value))
        {
            return value;
        }

        return 0;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? TryGetNestedString(JsonElement element, string propertyName, string nestedPropertyName)
    {
        return TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.Object
            ? TryGetString(property, nestedPropertyName)
            : null;
    }

    private static string ReadArtistNames(JsonElement itemElement)
    {
        if (!TryGetProperty(itemElement, "artists", out var artistsElement) || artistsElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var artistNames = new List<string>();
        foreach (var artist in artistsElement.EnumerateArray())
        {
            var artistName = TryGetString(artist, "name");
            if (!string.IsNullOrWhiteSpace(artistName))
            {
                artistNames.Add(artistName);
            }
        }

        return string.Join(", ", artistNames);
    }

    private static string ReadAlbumArtUrl(JsonElement itemElement)
    {
        if (!TryGetProperty(itemElement, "album", out var albumElement)
            || albumElement.ValueKind != JsonValueKind.Object
            || !TryGetProperty(albumElement, "images", out var imagesElement)
            || imagesElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        foreach (var image in imagesElement.EnumerateArray())
        {
            var url = TryGetString(image, "url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return string.Empty;
    }

    private void PublishSnapshot(SpotifyPlaybackSnapshot snapshot)
    {
        _currentSnapshot = snapshot;
        PlaybackStateChanged?.Invoke(this, snapshot);
    }

    private void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AudioBitPaths.LogsDirectoryPath);
            File.AppendAllText(_logFilePath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging is best effort only.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
