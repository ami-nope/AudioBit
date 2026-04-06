using System.Collections.Concurrent;
using System.Linq;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AudioBit.App.Infrastructure;
using AudioBit.App.Models;
using AudioBit.Core.Diagnostics;

namespace AudioBit.App.Services;

internal sealed class DiscordRpcService : IDisposable
{
    private const int OpcodeHandshake = 0;
    private const int OpcodeFrame = 1;
    private const int OpcodeClose = 2;
    private const int OpcodePing = 3;
    private const int OpcodePong = 4;
    private const int MaxPipeIndex = 9;
    private const int PipeConnectTimeoutMs = 5000;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AuthorizeTimeout = TimeSpan.FromSeconds(120);
    private static readonly string[] AuthorizeScopes = { "rpc", "identify" };

    private readonly DiscordAuthStateStore _authStateStore;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _redirectUri;
    private readonly string _logFilePath = Path.Combine(AudioBitPaths.LogsDirectoryPath, "discord-rpc.log");
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingCommands = new();

    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _connectionCts;
    private Task? _readLoopTask;
    private Task? _connectionTask;
    private DiscordTokenState? _tokenState;
    private DiscordConnectionState _connectionState = DiscordConnectionState.Disconnected;
    private DiscordVoiceSettings _currentVoiceSettings = new();
    private bool _disposed;

    public DiscordRpcService(DiscordAuthStateStore authStateStore, string clientId, string clientSecret, string redirectUri)
    {
        _authStateStore = authStateStore ?? throw new ArgumentNullException(nameof(authStateStore));
        _clientId = clientId ?? string.Empty;
        _clientSecret = clientSecret ?? string.Empty;
        _redirectUri = redirectUri ?? string.Empty;
    }

    public event EventHandler<DiscordVoiceSettings>? VoiceSettingsChanged;
    public event EventHandler<DiscordConnectionState>? ConnectionStateChanged;

    public DiscordConnectionState ConnectionState => _connectionState;
    public DiscordVoiceSettings CurrentVoiceSettings => _currentVoiceSettings;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_clientId)
        && !string.IsNullOrWhiteSpace(_clientSecret)
        && !string.IsNullOrWhiteSpace(_redirectUri);

    public bool HasSavedAuthorization => TryLoadSavedToken(out _);

    public void Start()
    {
        if (_disposed
            || !IsConfigured
            || _connectionState == DiscordConnectionState.Connecting
            || _connectionState == DiscordConnectionState.WaitingForAuthorization
            || _connectionState == DiscordConnectionState.Connected)
        {
            return;
        }

        _tokenState = _authStateStore.Load();
        try
        {
            _connectionCts?.Cancel();
            _connectionCts?.Dispose();
        }
        catch
        {
        }

        _connectionCts = new CancellationTokenSource();
        Log($"Discord RPC start requested. configured={IsConfigured} redirectConfigured={!string.IsNullOrWhiteSpace(_redirectUri)}");
        _connectionTask = ConnectLoopAsync(_connectionCts.Token);
    }

    public void Stop()
    {
        Log("Discord RPC stop requested.");
        DisconnectInternal();
    }

    public void DisconnectAndForgetAuthorization()
    {
        _tokenState = null;
        _authStateStore.Clear();
        DisconnectInternal();
        Log("Discord authorization cleared by user disconnect.");
    }

    public async Task SetVoiceSettingsAsync(bool mute, bool deaf)
    {
        if (_connectionState != DiscordConnectionState.Connected || _pipe is null)
        {
            return;
        }

        var nonce = Guid.NewGuid().ToString("N");
        var payload = new
        {
            cmd = "SET_VOICE_SETTINGS",
            args = new { mute, deaf },
            nonce,
        };

        try
        {
            var response = await SendCommandAsync(nonce, payload).ConfigureAwait(false);
            ParseAndPublishVoiceSettings(response);
        }
        catch (Exception ex)
        {
            Log(
                "SET_VOICE_SETTINGS failed.",
                ex,
                ("Operation", "SetVoiceSettingsAsync"),
                ("Mute", mute),
                ("Deaf", deaf));
        }
    }

    public async Task<DiscordVoiceSettings?> GetVoiceSettingsAsync()
    {
        if (_connectionState != DiscordConnectionState.Connected || _pipe is null)
        {
            return null;
        }

        var nonce = Guid.NewGuid().ToString("N");
        var payload = new
        {
            cmd = "GET_VOICE_SETTINGS",
            args = new { },
            nonce,
        };

        try
        {
            var response = await SendCommandAsync(nonce, payload).ConfigureAwait(false);
            return ParseVoiceSettingsFromData(response);
        }
        catch (Exception ex)
        {
            Log(
                "GET_VOICE_SETTINGS failed.",
                ex,
                ("Operation", "GetVoiceSettingsAsync"));
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisconnectInternal();
        _writeLock.Dispose();
    }

    private async Task ConnectLoopAsync(CancellationToken cancellationToken)
    {
        Log("Discord RPC connection loop started.");
        // Single attempt — no retry loop. User must explicitly reconnect on failure.
        try
        {
            SetConnectionState(DiscordConnectionState.Connecting);
            var connected = await TryConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!connected)
            {
                SetConnectionState(DiscordConnectionState.Error);
            }
        }
        catch (OperationCanceledException)
        {
            SetConnectionState(DiscordConnectionState.Disconnected);
        }
        catch (Exception ex)
        {
            Log(
                "Discord RPC connection attempt failed.",
                ex,
                ("Operation", "ConnectLoopAsync"));
            SetConnectionState(DiscordConnectionState.Error);
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        // Try each pipe index until we find Discord.
        for (var i = 0; i <= MaxPipeIndex; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pipeName = $"discord-ipc-{i}";
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            Trace($"Trying Discord pipe '{pipeName}'.");

            try
            {
                await pipe.ConnectAsync(PipeConnectTimeoutMs, cancellationToken).ConfigureAwait(false);

                // Send handshake.
                var handshakeJson = JsonSerializer.Serialize(new { v = 1, client_id = _clientId });
                await WritePipeMessageAsync(pipe, OpcodeHandshake, handshakeJson, cancellationToken).ConfigureAwait(false);

                // Read handshake response.
                var (opcode, responseJson) = await ReadPipeMessageAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (opcode != OpcodeFrame)
                {
                    pipe.Dispose();
                    continue;
                }

                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                Trace($"Handshake response event={TryGetString(root, "evt") ?? "(unknown)"} pipe={pipeName}");

                // Check for READY event.
                if (TryGetString(root, "evt") != "READY")
                {
                    pipe.Dispose();
                    continue;
                }

                _pipe = pipe;
                Log($"Connected to Discord via pipe {pipeName}");

                // Start the read loop on a background thread.
                _readLoopTask = Task.Run(() => ReadLoopAsync(cancellationToken), cancellationToken);

                // Authenticate.
                var authenticated = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
                if (!authenticated)
                {
                    Log("Authentication failed, disconnecting.");
                    DisconnectPipe();
                    return false;
                }

                // Subscribe to voice settings updates.
                await SubscribeToVoiceSettingsAsync(cancellationToken).ConfigureAwait(false);

                // Get initial voice settings.
                var initialSettings = await GetVoiceSettingsAsync().ConfigureAwait(false);
                if (initialSettings is not null)
                {
                    _currentVoiceSettings = initialSettings;
                    VoiceSettingsChanged?.Invoke(this, initialSettings);
                }

                SetConnectionState(DiscordConnectionState.Connected);
                return true;
            }
            catch (TimeoutException)
            {
                pipe.Dispose();
            }
            catch (IOException)
            {
                pipe.Dispose();
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                Log(
                    "Discord pipe connection failed.",
                    ex,
                    ("Operation", "TryConnectAsync"),
                    ("PipeName", pipeName));
                pipe.Dispose();
            }
        }

        return false;
    }

    private async Task<bool> AuthenticateAsync(CancellationToken cancellationToken)
    {
        // If we have a saved token that isn't expired, try AUTHENTICATE directly.
        if (TryLoadSavedToken(out var savedToken))
        {
            _tokenState = savedToken;
            var success = await TryAuthenticateWithTokenAsync(savedToken.AccessToken, cancellationToken).ConfigureAwait(false);
            if (success)
            {
                return true;
            }

            // Token rejected - clear and re-authorize.
            _tokenState = null;
            _authStateStore.Clear();
        }

        // Need to AUTHORIZE (user consent).
        return await AuthorizeAndAuthenticateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryAuthenticateWithTokenAsync(string accessToken, CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var payload = new
        {
            cmd = "AUTHENTICATE",
            args = new { access_token = accessToken },
            nonce,
        };

        try
        {
            var response = await SendCommandAsync(nonce, payload).ConfigureAwait(false);

            if (response.TryGetProperty("evt", out var evt)
                && evt.ValueKind == JsonValueKind.String
                && evt.GetString() == "ERROR")
            {
                return false;
            }

            // A non-error AUTHENTICATE frame means the token is valid for RPC.
            // Discord may include user data when "identify" is granted, but the
            // widget does not need that payload to consider the session connected.
            if (response.TryGetProperty("data", out var data)
                && data.TryGetProperty("user", out _))
            {
                Log("Authenticated with saved token");
                return true;
            }

            Log("Authenticated with token");
            return true;
        }
        catch (Exception ex)
        {
            Log(
                "AUTHENTICATE failed.",
                ex,
                ("Operation", "TryAuthenticateWithTokenAsync"));
            return false;
        }
    }

    private async Task<bool> AuthorizeAndAuthenticateAsync(CancellationToken cancellationToken)
    {
        SetConnectionState(DiscordConnectionState.WaitingForAuthorization);

        // Send AUTHORIZE - this will show a prompt in the Discord client.
        // Use a longer timeout because the user needs time to click "Authorize".
        var nonce = Guid.NewGuid().ToString("N");
        Log($"Sending Discord AUTHORIZE. clientId={_clientId} scopes={string.Join(",", AuthorizeScopes)}");
        var payload = new
        {
            cmd = "AUTHORIZE",
            args = new
            {
                client_id = _clientId,
                scopes = AuthorizeScopes,
            },
            nonce,
        };

        JsonElement authResponse;
        try
        {
            authResponse = await SendCommandWithTimeoutAsync(nonce, payload, AuthorizeTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(
                "AUTHORIZE failed.",
                ex,
                ("Operation", "AuthorizeAndAuthenticateAsync"),
                ("TimeoutSeconds", AuthorizeTimeout.TotalSeconds));
            SetConnectionState(DiscordConnectionState.Error);
            return false;
        }

        // Check for error response first.
        if (authResponse.TryGetProperty("evt", out var evtElem)
            && evtElem.ValueKind == JsonValueKind.String
            && evtElem.GetString() == "ERROR")
        {
            var errorMsg = "Unknown error";
            if (authResponse.TryGetProperty("data", out var errData)
                && errData.TryGetProperty("message", out var msgElem)
                && msgElem.ValueKind == JsonValueKind.String)
            {
                errorMsg = msgElem.GetString() ?? errorMsg;
            }

            Log($"AUTHORIZE error: {errorMsg}");
            if (errorMsg.Contains("invalid_scope", StringComparison.OrdinalIgnoreCase))
            {
                Log(
                    $"Discord rejected AUTHORIZE scopes '{string.Join(",", AuthorizeScopes)}'. " +
                    "This usually means the Discord application is not approved for RPC access " +
                    "or the Discord user is not on the application's tester list.");
            }

            SetConnectionState(DiscordConnectionState.Error);
            return false;
        }

        // Extract the authorization code.
        string? code = null;
        if (authResponse.TryGetProperty("data", out var data)
            && data.TryGetProperty("code", out var codeElement)
            && codeElement.ValueKind == JsonValueKind.String)
        {
            code = codeElement.GetString();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            Log("AUTHORIZE did not return a code (user may have denied)");
            SetConnectionState(DiscordConnectionState.Error);
            return false;
        }

        Log("AUTHORIZE returned an authorization code.");

        // Try using the code directly as an access_token (works for RPC IPC).
        var success = await TryAuthenticateWithTokenAsync(code, cancellationToken).ConfigureAwait(false);
        if (!success)
        {
            // Fall back to exchanging code for access_token via REST.
            Log("Direct code auth failed, trying token exchange...");
            var accessToken = await ExchangeCodeForTokenAsync(code, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                Log("Token exchange also failed");
                SetConnectionState(DiscordConnectionState.Error);
                return false;
            }

            success = await TryAuthenticateWithTokenAsync(accessToken, cancellationToken).ConfigureAwait(false);
            if (!success)
            {
                Log("AUTHENTICATE with exchanged token failed");
                SetConnectionState(DiscordConnectionState.Error);
                return false;
            }

            code = accessToken; // Use the exchanged token for persistence.
        }

        // Persist the token.
        _tokenState = new DiscordTokenState
        {
            AccessToken = code,
            ClientId = _clientId,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(7),
        };
        _authStateStore.Save(_tokenState);

        return true;
    }

    private bool TryLoadSavedToken(out DiscordTokenState? tokenState)
    {
        tokenState = _tokenState;
        if (tokenState is null || tokenState.IsEmpty)
        {
            tokenState = _authStateStore.Load();
        }

        if (tokenState is null || tokenState.IsEmpty)
        {
            tokenState = null;
            return false;
        }

        if (!string.Equals(tokenState.ClientId, _clientId, StringComparison.Ordinal))
        {
            tokenState = null;
            return false;
        }

        if (tokenState.IsExpired)
        {
            tokenState = null;
            return false;
        }

        return true;
    }

    private async Task<string?> ExchangeCodeForTokenAsync(string code, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_redirectUri))
            {
                Log("Token exchange skipped: redirect URI is not configured.");
                return null;
            }

            using var httpClient = NetworkClientFactory.CreateHttpClient(
                TimeSpan.FromSeconds(15),
                allowAutoRedirect: false,
                acceptHeader: "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://discord.com/api/oauth2/token")
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "authorization_code"),
                    new KeyValuePair<string, string>("code", code),
                    new KeyValuePair<string, string>("redirect_uri", _redirectUri),
                }),
            };

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Log($"Token exchange HTTP {(int)response.StatusCode} for redirect URI '{_redirectUri}': {errorBody}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("access_token", out var tokenElement))
            {
                return tokenElement.GetString();
            }

            return null;
        }
        catch (Exception ex)
        {
            Log(
                "Discord token exchange failed.",
                ex,
                ("Operation", "ExchangeCodeForTokenAsync"),
                ("RedirectUri", _redirectUri),
                ("Endpoint", "https://discord.com/api/oauth2/token"));
            return null;
        }
    }

    private async Task SubscribeToVoiceSettingsAsync(CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var payload = new
        {
            cmd = "SUBSCRIBE",
            evt = "VOICE_SETTINGS_UPDATE",
            args = new { },
            nonce,
        };

        try
        {
            await SendCommandAsync(nonce, payload).ConfigureAwait(false);
            Log("Subscribed to VOICE_SETTINGS_UPDATE");
        }
        catch (Exception ex)
        {
            Log(
                "Failed to subscribe to VOICE_SETTINGS_UPDATE.",
                ex,
                ("Operation", "SubscribeToVoiceSettingsAsync"));
        }
    }

    private Task<JsonElement> SendCommandAsync(string nonce, object payload)
    {
        return SendCommandWithTimeoutAsync(nonce, payload, CommandTimeout);
    }

    private async Task<JsonElement> SendCommandWithTimeoutAsync(string nonce, object payload, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCommands[nonce] = tcs;

        try
        {
            var json = JsonSerializer.Serialize(payload);
            var pipe = _pipe;
            if (pipe is null || !pipe.IsConnected)
            {
                throw new InvalidOperationException("Pipe is not connected");
            }

            await WritePipeMessageAsync(pipe, OpcodeFrame, json, CancellationToken.None).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var registration = timeoutCts.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingCommands.TryRemove(nonce, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipe = _pipe;
                if (pipe is null || !pipe.IsConnected)
                {
                    break;
                }

                var (opcode, json) = await ReadPipeMessageAsync(pipe, cancellationToken).ConfigureAwait(false);

                switch (opcode)
                {
                    case OpcodeFrame:
                        HandleFrame(json);
                        break;
                    case OpcodeClose:
                        Log("Discord sent CLOSE");
                        break;
                    case OpcodePing:
                        await WritePipeMessageAsync(pipe, OpcodePong, json, cancellationToken).ConfigureAwait(false);
                        continue;
                    default:
                        continue;
                }

                if (opcode == OpcodeClose)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (EndOfStreamException)
        {
            Log("Discord pipe closed (EOF)");
        }
        catch (IOException ex)
        {
            Log(
                "Discord pipe IO error.",
                ex,
                ("Operation", "ReadLoopAsync"));
        }
        catch (Exception ex)
        {
            Log(
                "Discord read loop error.",
                ex,
                ("Operation", "ReadLoopAsync"));
        }

        // Connection lost, attempt reconnect.
        DisconnectPipe();
        SetConnectionState(DiscordConnectionState.Disconnected);

        // Cancel all pending commands.
        foreach (var kvp in _pendingCommands)
        {
            kvp.Value.TrySetCanceled();
        }

        _pendingCommands.Clear();

        // Do NOT auto-reconnect. The user must explicitly reconnect
        // via the widget buttons or the Connect button in settings.
    }

    private void HandleFrame(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var evt = TryGetString(root, "evt");
            Trace($"HandleFrame evt={evt ?? "(none)"}");

            // Check if this is a response to a pending command (has nonce).
            if (root.TryGetProperty("nonce", out var nonceElement)
                && nonceElement.ValueKind != JsonValueKind.Null)
            {
                var nonce = nonceElement.ValueKind == JsonValueKind.String
                    ? nonceElement.GetString()
                    : nonceElement.ToString();
                if (!string.IsNullOrWhiteSpace(nonce) && _pendingCommands.TryRemove(nonce, out var tcs))
                {
                    tcs.TrySetResult(root.Clone());
                    return;
                }
            }

            // Check for subscription events.
            if (string.Equals(evt, "VOICE_SETTINGS_UPDATE", StringComparison.Ordinal))
            {
                if (root.TryGetProperty("data", out var data))
                {
                    var settings = ParseVoiceSettingsFromData(data);
                    if (settings is not null)
                    {
                        _currentVoiceSettings = settings;
                        VoiceSettingsChanged?.Invoke(this, settings);
                    }
                }
            }
            else if (string.Equals(evt, "ERROR", StringComparison.Ordinal))
            {
                var errorMessage = "";
                if (root.TryGetProperty("data", out var errorData)
                    && errorData.TryGetProperty("message", out var msgElem))
                {
                    errorMessage = msgElem.GetString() ?? "";
                }

                Log($"RPC ERROR event: {errorMessage}");
            }
        }
        catch (Exception ex)
        {
            Log(
                "Discord frame handling failed.",
                ex,
                ("Operation", "HandleFrame"));
        }
    }

    private void ParseAndPublishVoiceSettings(JsonElement response)
    {
        DiscordVoiceSettings? settings = null;

        if (response.TryGetProperty("data", out var data))
        {
            settings = ParseVoiceSettingsFromData(data);
        }
        else
        {
            settings = ParseVoiceSettingsFromData(response);
        }

        if (settings is not null)
        {
            _currentVoiceSettings = settings;
            VoiceSettingsChanged?.Invoke(this, settings);
        }
    }

    private static DiscordVoiceSettings? ParseVoiceSettingsFromData(JsonElement data)
    {
        var settings = new DiscordVoiceSettings();

        if (data.TryGetProperty("mute", out var muteElement))
        {
            settings.Mute = muteElement.GetBoolean();
        }

        if (data.TryGetProperty("deaf", out var deafElement))
        {
            settings.Deaf = deafElement.GetBoolean();
        }

        return settings;
    }

    private async Task WritePipeMessageAsync(NamedPipeClientStream pipe, int opcode, string json, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), (uint)opcode);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), (uint)payload.Length);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<(int opcode, string json)> ReadPipeMessageAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        var headerBytesRead = 0;
        while (headerBytesRead < 8)
        {
            var read = await pipe.ReadAsync(header.AsMemory(headerBytesRead, 8 - headerBytesRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Discord pipe closed");
            }

            headerBytesRead += read;
        }

        var opcode = (int)BitConverter.ToUInt32(header, 0);
        var length = (int)BitConverter.ToUInt32(header, 4);

        if (length <= 0)
        {
            return (opcode, "{}");
        }

        var payload = new byte[length];
        var payloadBytesRead = 0;
        while (payloadBytesRead < length)
        {
            var read = await pipe.ReadAsync(payload.AsMemory(payloadBytesRead, length - payloadBytesRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Discord pipe closed mid-payload");
            }

            payloadBytesRead += read;
        }

        return (opcode, Encoding.UTF8.GetString(payload));
    }

    private void DisconnectInternal()
    {
        try
        {
            _connectionCts?.Cancel();
        }
        catch
        {
        }

        try
        {
            _connectionCts?.Dispose();
        }
        catch
        {
        }

        _connectionCts = null;
        _connectionTask = null;
        DisconnectPipe();

        foreach (var kvp in _pendingCommands)
        {
            kvp.Value.TrySetCanceled();
        }

        _pendingCommands.Clear();
        SetConnectionState(DiscordConnectionState.Disconnected);
    }

    private void DisconnectPipe()
    {
        var pipe = _pipe;
        _pipe = null;

        if (pipe is null)
        {
            return;
        }

        try
        {
            if (pipe.IsConnected)
            {
                pipe.Close();
            }

            pipe.Dispose();
        }
        catch
        {
        }
    }

    private void SetConnectionState(DiscordConnectionState state)
    {
        if (_connectionState == state)
        {
            return;
        }

        _connectionState = state;
        ConnectionStateChanged?.Invoke(this, state);
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private void Trace(string message)
    {
        Log(message, AppLogLevel.Trace);
    }

    private void Log(string message, AppLogLevel? level = null)
    {
        AppLogTextWriter.Write("Discord", message, level);

        try
        {
            var dir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(_logFilePath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private void Log(string message, Exception exception, params (string Key, object? Value)[] context)
    {
        AppLogTextWriter.Write(
            "Discord",
            message,
            exception,
            context: context.Select(item => new KeyValuePair<string, object?>(item.Key, item.Value)));

        try
        {
            var details = AppLogExceptionFormatter.Format(
                "Discord",
                message,
                exception,
                context: context.Select(item => new KeyValuePair<string, object?>(item.Key, item.Value)));
            var entry = new StringBuilder()
                .Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] ").AppendLine(message);
            using var reader = new StringReader(details);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                entry.Append("    ").AppendLine(line);
            }

            var dir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(_logFilePath, entry.ToString());
        }
        catch
        {
        }
    }
}
