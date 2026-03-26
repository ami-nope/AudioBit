using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using AudioBit.App.Infrastructure;
using AudioBit.App.Models;
using AudioBit.Core;

namespace AudioBit.App.Services;

internal sealed class RemoteClientService : IDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SessionProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SessionProbeRetryDelay = TimeSpan.FromMilliseconds(200);
    private const int PrimaryProbeAttempts = 3;
    private const int SecondaryProbeAttempts = 2;
    private const int SessionIdLength = 10;
    private const int PairCodeLength = 6;
    private const string SessionIdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    private const string DefaultRelayHttp = "https://audiobit-relay-production.up.railway.app/";
    private const string DefaultRelayWs = "wss://audiobit-relay-production.up.railway.app/ws";
    private static readonly TimeSpan MeterInterval = TimeSpan.FromSeconds(1.0 / 60.0);
    private static readonly TimeSpan StateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StateKeepAliveInterval = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
    };
    private static readonly TimeSpan GeoIpLookupTimeout = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan GeoIpRetryDelay = TimeSpan.FromMinutes(10);
    private static readonly HttpClient GeoIpClient = CreateGeoIpClient();
    private static readonly object LogFileGate = new();

    private readonly object _syncRoot = new();
    private readonly AudioSessionService _audioSessionService;
    private readonly RemoteSessionManager _sessionManager;
    private readonly RelayConnection _relayConnection;
    private readonly RemoteCommandDispatcher _commandDispatcher;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly IReadOnlyList<RelayTarget> _relayTargets;
    private int _activeRelayTargetIndex;
    private bool _forceSessionRefresh;
    private bool _forceNewSessionRequest;
    private RemoteSessionRequest? _sessionRequest;

    private Task? _connectionLoopTask;
    private Task? _stateLoopTask;
    private Task? _meterLoopTask;
    private bool _disposed;
    private bool _autoReconnectEnabled = true;
    private bool _helloCompleted;
    private RemoteSessionInfo _sessionInfo = RemoteSessionInfo.Empty;
    private RemoteStateModel _latestState = new();
    private List<object[]> _latestLevels = [];
    private string _stateSignature = string.Empty;
    private bool _stateDirty;
    private DateTime _lastStateSentUtc = DateTime.MinValue;
    private long _revision;
    private Dictionary<string, int> _appLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _geoIpCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<string?>> _geoIpLookups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _geoIpLastAttemptUtc = new(StringComparer.OrdinalIgnoreCase);
    private string _connectedDeviceId = string.Empty;
    private string _connectedDeviceName = string.Empty;
    private string _connectedDeviceLocation = string.Empty;
    private string _connectedConnectionType = string.Empty;
    private string _connectedDeviceIpAddress = string.Empty;
    private string _connectedDeviceUserAgent = string.Empty;
    private int? _connectedDeviceLatencyMs;
    private DateTimeOffset _connectedDeviceLatencyUpdatedAtUtc = DateTimeOffset.MinValue;
    private string _activeRelayRouteLabel = string.Empty;
    private int? _activeRelayProbeLatencyMs;
    private int? _existingDeviceCount;
    private int? _connectedDeviceCount;

    public RemoteClientService(AudioSessionService audioSessionService)
    {
        _audioSessionService = audioSessionService;

        var fallbackHttp = GetConfiguredUri("AUDIOBIT_RELAY_HTTP", DefaultRelayHttp);
        var fallbackWs = GetConfiguredUri("AUDIOBIT_RELAY_WS", DefaultRelayWs);
        var primaryHttp = GetConfiguredUriOrNull("AUDIOBIT_RELAY_HTTP_PRIMARY");
        var primaryWs = GetConfiguredUriOrNull("AUDIOBIT_RELAY_WS_PRIMARY");
        _relayTargets = BuildRelayTargets(primaryHttp, primaryWs, fallbackHttp, fallbackWs);
        _activeRelayTargetIndex = 0;
        var initialTarget = _relayTargets[_activeRelayTargetIndex];
        _activeRelayRouteLabel = initialTarget.Name;
        _activeRelayProbeLatencyMs = null;

        _sessionManager = new RemoteSessionManager(initialTarget.HttpBaseUri, Log);
        _relayConnection = new RelayConnection(initialTarget.WsEndpoint, Log);
        _commandDispatcher = new RemoteCommandDispatcher(_audioSessionService, Log);

        _relayConnection.Connected += OnRelayConnected;
        _relayConnection.Disconnected += OnRelayDisconnected;
        _relayConnection.MessageReceived += OnRelayMessageReceived;

        Log($"Relay order: {string.Join(" -> ", _relayTargets.Select(target => $"{target.Name} [{target.WsEndpoint}]"))}");
    }

    public event Action<RemoteSessionInfo>? SessionInfoChanged;

    public event Action<string>? LogMessage;

    public bool IsConnected => _relayConnection.IsConnected && _helloCompleted;

    public RemoteSessionInfo SessionInfo
    {
        get
        {
            lock (_syncRoot)
            {
                return _sessionInfo;
            }
        }
    }

    public void Start()
    {
        ThrowIfDisposed();

        if (_connectionLoopTask is not null)
        {
            return;
        }

        _connectionLoopTask = Task.Run(() => RunConnectionLoopAsync(_lifetimeCts.Token));
        _stateLoopTask = Task.Run(() => RunStateLoopAsync(_lifetimeCts.Token));
        _meterLoopTask = Task.Run(() => RunMeterLoopAsync(_lifetimeCts.Token));
    }

    public void SetAutoReconnect(bool enabled)
    {
        _autoReconnectEnabled = enabled;
    }

    public async Task RefreshPairingSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
        try
        {
            await _relayConnection.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            _helloCompleted = false;
            lock (_syncRoot)
            {
                _forceNewSessionRequest = true;
            }
            await EnsureSessionAsync(forceCreate: true, linkedToken.Token).ConfigureAwait(false);
        }
        finally
        {
            linkedToken.Dispose();
        }
    }

    public async Task RemoveConnectedDeviceAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (!IsConnected)
        {
            return;
        }

        string sessionId;
        string deviceId;
        lock (_syncRoot)
        {
            sessionId = _sessionInfo.SessionId;
            deviceId = _connectedDeviceId;
        }

        await SendAsync(
                new
                {
                    t = "remove_device",
                    sid = sessionId,
                    device_id = deviceId,
                },
                cancellationToken)
            .ConfigureAwait(false);

        UpdateSessionInfo(
            status: "Remove request sent",
            isRelayConnected: true,
            isRemoteConnected: false,
            clearConnectedDevice: true);
    }

    public void UpdateAudioSnapshot(
        IReadOnlyList<AppAudioModel> apps,
        float masterVolume,
        bool masterMuted,
        bool micMuted,
        string defaultOutputDevice,
        string defaultInputDevice,
        IReadOnlyList<AudioDeviceOptionModel> outputDevices,
        IReadOnlyList<AudioDeviceOptionModel> inputDevices)
    {
        ThrowIfDisposed();

        RemoteSessionInfo sessionInfo;
        long revision;
        lock (_syncRoot)
        {
            sessionInfo = _sessionInfo;
            revision = _revision;
        }

        var nextState = new RemoteStateModel
        {
            SessionId = sessionInfo.SessionId,
            Revision = revision,
            MasterVolume = Math.Clamp((int)Math.Round(masterVolume * 100.0f), 0, 100),
            MasterMuted = masterMuted,
            MicMuted = micMuted,
            DefaultOutputDevice = defaultOutputDevice ?? string.Empty,
            DefaultInputDevice = defaultInputDevice ?? string.Empty,
        };

        foreach (var device in outputDevices)
        {
            nextState.OutputDevices.Add(new RemoteDeviceModel
            {
                Id = device.Id,
                Name = device.DisplayName,
            });
        }

        foreach (var device in inputDevices)
        {
            nextState.InputDevices.Add(new RemoteDeviceModel
            {
                Id = device.Id,
                Name = device.DisplayName,
            });
        }

        var nextLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nextLevels = new List<object[]>(apps.Count);

        for (var index = 0; index < apps.Count; index++)
        {
            var app = apps[index];
            var appId = $"p{app.ProcessId}";
            var normalizedName = NormalizeAppLookupKey(app.AppName);
            var stableAppId = normalizedName.Length == 0 ? string.Empty : $"a_{normalizedName}";
            var peak = Math.Clamp((int)Math.Round(app.Peak * 1000.0f), 0, 1000);

            nextState.Apps.Add(new RemoteAppStateModel
            {
                Id = appId,
                Name = app.AppName,
                ProcessId = app.ProcessId,
                Volume = Math.Clamp((int)Math.Round(app.Volume * 100.0f), 0, 100),
                Muted = app.IsMuted,
                OutputDevice = app.PreferredRenderDeviceId,
                InputDevice = app.PreferredCaptureDeviceId,
                Peak = peak,
            });

            nextLevels.Add([appId, peak]);
            nextLookup[appId] = app.ProcessId;
            nextLookup[app.ProcessId.ToString()] = app.ProcessId;
            if (!string.IsNullOrEmpty(normalizedName) && !nextLookup.ContainsKey(normalizedName))
            {
                nextLookup[normalizedName] = app.ProcessId;
            }

            if (!string.IsNullOrEmpty(stableAppId) && !nextLookup.ContainsKey(stableAppId))
            {
                nextLookup[stableAppId] = app.ProcessId;
            }
        }

        var signature = BuildStateSignature(nextState);

        lock (_syncRoot)
        {
            _latestState = nextState;
            _latestLevels = nextLevels;
            _appLookup = nextLookup;

            if (!string.Equals(_stateSignature, signature, StringComparison.Ordinal))
            {
                _stateSignature = signature;
                _revision++;
                _latestState.Revision = _revision;
                _stateDirty = true;
            }
            else
            {
                _latestState.Revision = _revision;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCts.Cancel();

        try
        {
            var tasks = new[] { _connectionLoopTask, _stateLoopTask, _meterLoopTask }
                .Where(task => task is not null)
                .Select(task => task!)
                .ToArray();

            if (tasks.Length > 0)
            {
                Task.WaitAll(tasks, TimeSpan.FromSeconds(2));
            }
        }
        catch
        {
            // Ignore shutdown races.
        }

        _lifetimeCts.Dispose();
        _relayConnection.Dispose();
        _sessionManager.Dispose();
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var shouldFailover = false;
            var failoverReason = string.Empty;

            try
            {
                if (!_autoReconnectEnabled)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                bool forceSessionCreate;
                lock (_syncRoot)
                {
                    forceSessionCreate = _forceSessionRefresh;
                }

                ApplyActiveRelayTarget();
                var session = await EnsureSessionAsync(forceCreate: forceSessionCreate, cancellationToken).ConfigureAwait(false);
                if (forceSessionCreate)
                {
                    lock (_syncRoot)
                    {
                        _forceSessionRefresh = false;
                    }
                }

                await _relayConnection.ConnectAsync(cancellationToken).ConfigureAwait(false);
                await SendHelloAsync(session.SessionId, cancellationToken).ConfigureAwait(false);
                await _relayConnection.ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);

                if (!cancellationToken.IsCancellationRequested && !_relayConnection.IsConnected)
                {
                    shouldFailover = true;
                    failoverReason = "relay socket closed";
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Connection loop error: {ex.Message}");
                shouldFailover = true;
                failoverReason = ex.Message;
            }

            if (!cancellationToken.IsCancellationRequested && _autoReconnectEnabled)
            {
                if (shouldFailover)
                {
                    SwitchRelayTargetAfterFailure(failoverReason);
                }

                Log("Reconnect attempt in 2 seconds...");
                await Task.Delay(ReconnectDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunStateLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(StateInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (!IsConnected)
                {
                    continue;
                }

                RemoteStateModel? stateToSend = null;
                lock (_syncRoot)
                {
                    var shouldSend = _stateDirty || DateTime.UtcNow - _lastStateSentUtc >= StateKeepAliveInterval;
                    if (!shouldSend)
                    {
                        continue;
                    }

                    stateToSend = CloneState(_latestState);
                    stateToSend.SessionId = _sessionInfo.SessionId;
                    stateToSend.Revision = _revision;
                    _stateDirty = false;
                    _lastStateSentUtc = DateTime.UtcNow;
                }

                await SendAsync(stateToSend, cancellationToken).ConfigureAwait(false);
                Log("state sent");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"State loop send failed: {ex.Message}");
            }
        }
    }

    private async Task RunMeterLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MeterInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (!IsConnected)
                {
                    continue;
                }

                RemoteLevelUpdateModel? meter = null;
                lock (_syncRoot)
                {
                    meter = new RemoteLevelUpdateModel
                    {
                        SessionId = _sessionInfo.SessionId,
                        Revision = _revision,
                        Apps = [.. _latestLevels],
                    };
                }

                await SendAsync(meter, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log($"Meter loop send failed: {ex.Message}");
            }
        }
    }

    private void ApplyActiveRelayTarget()
    {
        RelayTarget target;
        lock (_syncRoot)
        {
            target = _relayTargets[_activeRelayTargetIndex];
            _activeRelayRouteLabel = target.Name;
        }

        _relayConnection.SetEndpoint(target.WsEndpoint);
        _sessionManager.SetHttpBaseUri(target.HttpBaseUri);
    }

    private void SwitchRelayTargetAfterFailure(string reason)
    {
        if (_relayTargets.Count <= 1)
        {
            return;
        }

        RelayTarget nextTarget;
        lock (_syncRoot)
        {
            _activeRelayTargetIndex = (_activeRelayTargetIndex + 1) % _relayTargets.Count;
            nextTarget = _relayTargets[_activeRelayTargetIndex];
            _activeRelayRouteLabel = nextTarget.Name;
            _activeRelayProbeLatencyMs = null;
            _forceSessionRefresh = true;
        }

        _relayConnection.SetEndpoint(nextTarget.WsEndpoint);
        _sessionManager.SetHttpBaseUri(nextTarget.HttpBaseUri);
        Log($"Switched relay target to {nextTarget.Name} after failure ({reason}).");
    }

    private async void OnRelayMessageReceived(string message)
    {
        try
        {
            await HandleRelayMessageAsync(message, _lifetimeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore during shutdown
        }
        catch (Exception ex)
        {
            Log($"Message handling failed: {ex.Message}");
        }
    }

    private void OnRelayConnected()
    {
        UpdateSessionInfo(
            status: "Connected to relay (waiting for hello)",
            isRelayConnected: false,
            isRemoteConnected: false,
            clearConnectedDevice: true);
    }

    private void OnRelayDisconnected(string reason)
    {
        _helloCompleted = false;
        UpdateSessionInfo(
            status: $"Disconnected ({reason})",
            isRelayConnected: false,
            isRemoteConnected: false,
            clearConnectedDevice: true);
    }

    private async Task HandleRelayMessageAsync(string message, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        var type = ReadString(root, "t");
        if (string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        switch (type)
        {
            case "hello_ok":
            {
                var payload = GetPayload(root);
                var role = ReadString(root, "role");
                if (string.IsNullOrWhiteSpace(role))
                {
                    role = ReadString(payload, "role");
                }

                if (string.Equals(role, "pc", StringComparison.OrdinalIgnoreCase))
                {
                    _helloCompleted = true;
                    UpdateSessionInfo(status: "Ready for pairing", isRelayConnected: true);
                    lock (_syncRoot)
                    {
                        _stateDirty = true;
                    }
                }
                else if (string.Equals(role, "remote", StringComparison.OrdinalIgnoreCase))
                {
                    TryReadDeviceMetadata(root, payload, out var metadata);
                    UpdateSessionInfo(
                        status: "Remote device connected",
                        isRemoteConnected: true,
                        deviceMetadata: metadata);
                    _ = TryEnrichDeviceLocationAsync(metadata);
                }
                else
                {
                    _helloCompleted = false;
                    UpdateSessionInfo(
                        status: "Handshake failed",
                        isRelayConnected: false,
                        isRemoteConnected: false,
                        clearConnectedDevice: true);
                }

                break;
            }

            case "cmd":
            {
                var payload = root.TryGetProperty("d", out var envelopePayload)
                    ? envelopePayload
                    : root;
                UpdateSessionInfo(status: "Remote control active", isRemoteConnected: true);
                var operation = ReadString(payload, "op");
                var result = await _commandDispatcher
                    .DispatchAsync(payload, ResolveProcessId, cancellationToken)
                    .ConfigureAwait(false);
                Log($"Command result: op={operation} ok={result.Ok} err={result.Error}");

                var commandId = ReadString(root, "cid");
                await SendCommandResultAsync(commandId, result, cancellationToken).ConfigureAwait(false);
                lock (_syncRoot)
                {
                    _stateDirty = true;
                }

                break;
            }

            case "session_status":
            {
                var payload = GetPayload(root);
                bool? relayConnected = null;
                bool? remoteConnected = null;
                var clearConnectedDevice = false;
                int? connectedDeviceCount = null;
                var reason = ReadString(payload, "reason");
                var reasonConnected = reason.Contains("remote_connected", StringComparison.OrdinalIgnoreCase);
                var reasonDisconnected = reason.Contains("remote_disconnected", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("device_removed", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("session_terminated", StringComparison.OrdinalIgnoreCase);
                var reasonPcDown = reason.Contains("pc_disconnected", StringComparison.OrdinalIgnoreCase)
                    || reason.Contains("session_terminated", StringComparison.OrdinalIgnoreCase);

                var pcOnline = ReadBool(payload, "pc_online");
                if (pcOnline.HasValue)
                {
                    // `pc_online` is primarily useful to remotes. For the PC app, treat it as a hint only,
                    // so transient/ambiguous broadcasts do not invalidate a live websocket session.
                    relayConnected = pcOnline.Value;
                    if (!pcOnline.Value && reasonPcDown)
                    {
                        _helloCompleted = false;
                        remoteConnected = false;
                        clearConnectedDevice = true;
                    }
                }

                var remoteCount = ReadInt(payload, "remote_count")
                    ?? ReadInt(payload, "remotes")
                    ?? ReadInt(payload, "remote_online")
                    ?? ReadInt(payload, "clients");
                if (remoteCount.HasValue)
                {
                    if (remoteCount.Value > 0)
                    {
                        remoteConnected = true;
                        connectedDeviceCount = Math.Max(0, remoteCount.Value);
                    }
                    else if (reasonDisconnected)
                    {
                        remoteConnected = false;
                        clearConnectedDevice = true;
                        connectedDeviceCount = 0;
                    }
                }

                if (reasonConnected)
                {
                    remoteConnected = true;
                }
                else if (reasonDisconnected)
                {
                    remoteConnected = false;
                    clearConnectedDevice = true;
                }

                TryReadDeviceMetadata(root, payload, out var metadata);
                if (!metadata.IsEmpty)
                {
                    // Metadata implies an attached device even when count fields are stale.
                    remoteConnected = true;
                }

                var status = string.IsNullOrWhiteSpace(reason)
                    ? "Session status updated"
                    : reason.Replace('_', ' ');
                UpdateSessionInfo(
                    status,
                    relayConnected,
                    remoteConnected,
                    deviceMetadata: metadata,
                    clearConnectedDevice: clearConnectedDevice,
                    connectedDeviceCount: connectedDeviceCount);
                _ = TryEnrichDeviceLocationAsync(metadata);
                break;
            }

            case "device_connected":
            {
                var payload = GetPayload(root);
                TryReadDeviceMetadata(root, payload, out var metadata);
                var existingDeviceCount = ReadInt(payload, "existing_device_count")
                    ?? ReadInt(root, "existing_device_count");
                var connectedDeviceCount = ReadInt(payload, "connected_device_count")
                    ?? ReadInt(root, "connected_device_count");
                if (!connectedDeviceCount.HasValue && existingDeviceCount.HasValue)
                {
                    connectedDeviceCount = Math.Max(0, existingDeviceCount.Value + 1);
                }

                if (!connectedDeviceCount.HasValue)
                {
                    lock (_syncRoot)
                    {
                        connectedDeviceCount = _connectedDeviceCount.HasValue
                            ? Math.Max(1, _connectedDeviceCount.Value + 1)
                            : 1;
                    }
                }

                UpdateSessionInfo(
                    status: "Remote device connected",
                    isRemoteConnected: true,
                    deviceMetadata: metadata,
                    existingDeviceCount: existingDeviceCount,
                    connectedDeviceCount: connectedDeviceCount);
                _ = TryEnrichDeviceLocationAsync(metadata);
                break;
            }

            case "device_disconnected":
            case "device_removed":
            {
                var payload = GetPayload(root);
                var connectedDeviceCount = ReadInt(payload, "connected_device_count")
                    ?? ReadInt(root, "connected_device_count");
                if (!connectedDeviceCount.HasValue)
                {
                    lock (_syncRoot)
                    {
                        if (_connectedDeviceCount.HasValue)
                        {
                            connectedDeviceCount = Math.Max(0, _connectedDeviceCount.Value - 1);
                        }
                    }
                }

                var remainingDevices = connectedDeviceCount.GetValueOrDefault();
                UpdateSessionInfo(
                    status: "Remote device disconnected",
                    isRemoteConnected: connectedDeviceCount.HasValue ? remainingDevices > 0 : false,
                    clearConnectedDevice: remainingDevices == 0,
                    connectedDeviceCount: connectedDeviceCount);
                break;
            }

            case "device_latency":
            case "latency":
            case "latency_update":
            {
                var payload = GetPayload(root);
                var latencyMs = ReadLatencyMs(payload, root);
                if (latencyMs.HasValue)
                {
                    UpdateSessionInfo(
                        status: string.Empty,
                        deviceLatencyMs: Math.Max(0, latencyMs.Value));
                }

                if (TryReadDeviceMetadata(root, payload, out var metadata)
                    && !metadata.IsEmpty)
                {
                    UpdateSessionInfo(
                        status: string.Empty,
                        deviceMetadata: metadata);
                    _ = TryEnrichDeviceLocationAsync(metadata);
                }

                break;
            }

            case "resync":
                lock (_syncRoot)
                {
                    _stateDirty = true;
                }
                break;

            case "ping":
            {
                var payload = GetPayload(root);
                var ts = ReadLong(payload, "ts") ?? ReadLong(root, "ts");
                if (ts.HasValue)
                {
                    await SendAsync(new { t = "pong", ts = ts.Value }, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendAsync(new { t = "pong" }, cancellationToken).ConfigureAwait(false);
                }

                break;
            }
        }
    }

    private async Task SendHelloAsync(string sessionId, CancellationToken cancellationToken)
    {
        await SendAsync(new { t = "hello_pc", sid = sessionId }, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendCommandResultAsync(string commandId, RemoteCommandResult result, CancellationToken cancellationToken)
    {
        if (result.Ok)
        {
            await SendAsync(new { t = "cmd_result", cid = commandId, ok = 1 }, cancellationToken).ConfigureAwait(false);
            return;
        }

        await SendAsync(new { t = "cmd_result", cid = commandId, ok = 0, err = result.Error }, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task SendAsync<T>(T message, CancellationToken cancellationToken)
    {
        if (!_relayConnection.IsConnected)
        {
            return;
        }

        var json = JsonSerializer.Serialize(message, JsonOptions);
        await _relayConnection.SendJsonAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteSessionInfo> EnsureSessionAsync(bool forceCreate, CancellationToken cancellationToken)
    {
        RemoteSessionInfo current;
        bool forceNewRequest;
        lock (_syncRoot)
        {
            current = _sessionInfo;
            forceNewRequest = _forceNewSessionRequest;
            _forceNewSessionRequest = false;
        }

        var shouldCreate = forceCreate
            || string.IsNullOrWhiteSpace(current.SessionId);

        if (!shouldCreate)
        {
            return current;
        }

        var sessionRequest = GetSessionRequest(forceNewRequest);
        int? relayProbeLatencyMs = null;
        RemoteSessionInfo created;
        if (_relayTargets.Count > 1)
        {
            created = await CreateBestSessionAsync(sessionRequest, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var stopwatch = Stopwatch.StartNew();
            created = await _sessionManager.CreateSessionAsync(cancellationToken, sessionRequest).ConfigureAwait(false);
            stopwatch.Stop();
            relayProbeLatencyMs = Math.Max(1, (int)Math.Round(stopwatch.Elapsed.TotalMilliseconds));
        }

        if (!string.IsNullOrWhiteSpace(sessionRequest.SessionId)
            && !string.Equals(sessionRequest.SessionId, created.SessionId, StringComparison.Ordinal))
        {
            Log($"Relay returned sid '{created.SessionId}' for requested '{sessionRequest.SessionId}'.");
        }

        if (!string.IsNullOrWhiteSpace(sessionRequest.PairCode)
            && !string.Equals(sessionRequest.PairCode, created.PairCode, StringComparison.Ordinal))
        {
            Log($"Relay returned pair code '{created.PairCode}' for requested '{sessionRequest.PairCode}'.");
        }

        CaptureSessionRequest(created);

        if (_relayTargets.Count == 1)
        {
            RelayTarget activeTarget;
            lock (_syncRoot)
            {
                activeTarget = _relayTargets[_activeRelayTargetIndex];
                _activeRelayRouteLabel = activeTarget.Name;
                _activeRelayProbeLatencyMs = relayProbeLatencyMs;
            }

            created = new RemoteSessionInfo
            {
                SessionId = created.SessionId,
                PairCode = created.PairCode,
                ExpiresAtUtc = created.ExpiresAtUtc,
                QrUrl = created.QrUrl,
                IsRelayConnected = created.IsRelayConnected,
                IsConnected = created.IsConnected,
                Status = created.Status,
                DeviceId = created.DeviceId,
                DeviceName = created.DeviceName,
                DeviceLocation = created.DeviceLocation,
                ConnectionType = created.ConnectionType,
                IpAddress = created.IpAddress,
                UserAgent = created.UserAgent,
                DeviceLatencyMs = created.DeviceLatencyMs,
                DeviceLatencyUpdatedAtUtc = created.DeviceLatencyUpdatedAtUtc,
                ExistingDeviceCount = created.ExistingDeviceCount,
                ConnectedDeviceCount = created.ConnectedDeviceCount,
                RelayRouteLabel = activeTarget.Name,
                RelayProbeLatencyMs = relayProbeLatencyMs,
            };
        }

        lock (_syncRoot)
        {
            ClearConnectedDeviceState_NoLock();
            _existingDeviceCount = null;
            _connectedDeviceCount = 0;
            _sessionInfo = created;
            _stateDirty = true;
        }

        RaiseSessionInfoChanged(created);
        return created;
    }

    private RemoteSessionRequest GetSessionRequest(bool forceNew)
    {
        lock (_syncRoot)
        {
            if (!forceNew && _sessionRequest.HasValue && !_sessionRequest.Value.IsEmpty)
            {
                return _sessionRequest.Value;
            }

            var request = new RemoteSessionRequest(GenerateSessionId(), GeneratePairCode());
            _sessionRequest = request;
            return request;
        }
    }

    private void CaptureSessionRequest(RemoteSessionInfo session)
    {
        if (string.IsNullOrWhiteSpace(session.SessionId) && string.IsNullOrWhiteSpace(session.PairCode))
        {
            return;
        }

        lock (_syncRoot)
        {
            _sessionRequest = new RemoteSessionRequest(session.SessionId, session.PairCode);
        }
    }

    private static string GenerateSessionId()
    {
        var buffer = new char[SessionIdLength];
        for (var i = 0; i < buffer.Length; i++)
        {
            var index = RandomNumberGenerator.GetInt32(SessionIdAlphabet.Length);
            buffer[i] = SessionIdAlphabet[index];
        }

        return new string(buffer);
    }

    private static string GeneratePairCode()
    {
        var value = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return value.ToString($"D{PairCodeLength}", CultureInfo.InvariantCulture);
    }

    private async Task<RemoteSessionInfo> CreateBestSessionAsync(
        RemoteSessionRequest sessionRequest,
        CancellationToken cancellationToken)
    {
        var probeTasks = new Task<SessionCandidate>[_relayTargets.Count];
        for (var index = 0; index < _relayTargets.Count; index++)
        {
            var target = _relayTargets[index];
            probeTasks[index] = CreateSessionCandidateAsync(target, sessionRequest, index, cancellationToken);
        }

        var candidates = await Task.WhenAll(probeTasks).ConfigureAwait(false);
        var bestCandidate = candidates
            .Where(candidate => candidate.Session is not null)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.ProbeLatencyMs)
            .ThenByDescending(candidate => candidate.ObservedAtUtc)
            .ThenBy(candidate => candidate.Index)
            .FirstOrDefault();

        if (bestCandidate.Session is null)
        {
            var firstError = candidates
                .Select(candidate => candidate.Error)
                .FirstOrDefault(error => error is not null);
            throw firstError ?? new InvalidOperationException("Failed to create relay session on all configured relays.");
        }

        lock (_syncRoot)
        {
            _activeRelayTargetIndex = bestCandidate.Index;
            _activeRelayRouteLabel = bestCandidate.Target.Name;
            _activeRelayProbeLatencyMs = bestCandidate.ProbeLatencyMs;
        }

        _relayConnection.SetEndpoint(bestCandidate.Target.WsEndpoint);
        _sessionManager.SetHttpBaseUri(bestCandidate.Target.HttpBaseUri);
        Log(
            $"Selected relay '{bestCandidate.Target.Name}' for session {bestCandidate.Session.SessionId} "
            + $"(score={bestCandidate.Score}, probe={bestCandidate.ProbeLatencyMs}ms, attempt={bestCandidate.SuccessfulAttempt}/{bestCandidate.TotalAttempts}).");
        return new RemoteSessionInfo
        {
            SessionId = bestCandidate.Session.SessionId,
            PairCode = bestCandidate.Session.PairCode,
            ExpiresAtUtc = bestCandidate.Session.ExpiresAtUtc,
            QrUrl = bestCandidate.Session.QrUrl,
            IsRelayConnected = bestCandidate.Session.IsRelayConnected,
            IsConnected = bestCandidate.Session.IsConnected,
            DeviceId = bestCandidate.Session.DeviceId,
            DeviceName = bestCandidate.Session.DeviceName,
            DeviceLocation = bestCandidate.Session.DeviceLocation,
            ConnectionType = bestCandidate.Session.ConnectionType,
            IpAddress = bestCandidate.Session.IpAddress,
            UserAgent = bestCandidate.Session.UserAgent,
            DeviceLatencyMs = bestCandidate.Session.DeviceLatencyMs,
            DeviceLatencyUpdatedAtUtc = bestCandidate.Session.DeviceLatencyUpdatedAtUtc,
            ExistingDeviceCount = bestCandidate.Session.ExistingDeviceCount,
            ConnectedDeviceCount = bestCandidate.Session.ConnectedDeviceCount,
            RelayRouteLabel = bestCandidate.Target.Name,
            RelayProbeLatencyMs = bestCandidate.ProbeLatencyMs,
            Status = $"Pairing session created via {bestCandidate.Target.Name} ({bestCandidate.ProbeLatencyMs} ms)",
        };
    }

    private async Task<SessionCandidate> CreateSessionCandidateAsync(
        RelayTarget target,
        RemoteSessionRequest sessionRequest,
        int index,
        CancellationToken cancellationToken)
    {
        var totalAttempts = GetProbeAttemptCount(target);
        Exception? lastError = null;
        SessionCandidate? bestCandidate = null;

        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(SessionProbeTimeout);

            try
            {
                var stopwatch = Stopwatch.StartNew();
                var session = await _sessionManager.CreateSessionAsync(target.HttpBaseUri, probeCts.Token, sessionRequest)
                    .ConfigureAwait(false);
                stopwatch.Stop();

                var observedAtUtc = DateTimeOffset.UtcNow;
                var probeLatencyMs = Math.Max(1, (int)Math.Round(stopwatch.Elapsed.TotalMilliseconds));
                var score = ScoreSession(session, probeLatencyMs, observedAtUtc);
                var candidate = new SessionCandidate(
                    index,
                    target,
                    session,
                    score,
                    null,
                    probeLatencyMs,
                    observedAtUtc,
                    attempt,
                    totalAttempts);

                if (!bestCandidate.HasValue || candidate.Score > bestCandidate.Value.Score)
                {
                    bestCandidate = candidate;
                }

                Log(
                    $"Relay probe succeeded ({target.Name}) attempt {attempt}/{totalAttempts}: "
                    + $"sid={session.SessionId}, probe={probeLatencyMs}ms, score={score}.");

                if (!sessionRequest.IsEmpty)
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                Log($"Relay session probe failed ({target.Name}) attempt {attempt}/{totalAttempts}: {ex.Message}");
            }

            if (attempt < totalAttempts)
            {
                try
                {
                    await Task.Delay(SessionProbeRetryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        if (bestCandidate.HasValue)
        {
            return bestCandidate.Value;
        }

        return new SessionCandidate(
            index,
            target,
            null,
            int.MinValue,
            lastError ?? new InvalidOperationException($"No successful relay probes for {target.Name}."),
            int.MaxValue,
            DateTimeOffset.MinValue,
            0,
            totalAttempts);
    }

    private static int ScoreSession(RemoteSessionInfo session, int probeLatencyMs, DateTimeOffset observedAtUtc)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(session.SessionId))
        {
            score += 100;
        }

        if (!string.IsNullOrWhiteSpace(session.PairCode))
        {
            score += 20;
        }

        if (!string.IsNullOrWhiteSpace(session.QrUrl))
        {
            score += 10;
        }

        if (session.ExpiresAtUtc != DateTimeOffset.MinValue)
        {
            score += 5;

            var freshnessMinutes = Math.Max(0, (session.ExpiresAtUtc - observedAtUtc).TotalMinutes);
            score += Math.Min(15, (int)Math.Round(freshnessMinutes / 2.0));
        }

        score += probeLatencyMs switch
        {
            < 50 => 40,
            < 100 => 28,
            < 180 => 18,
            < 280 => 10,
            _ => 2,
        };

        if (probeLatencyMs > 0)
        {
            score -= Math.Min(10, probeLatencyMs / 120);
        }

        return score;
    }

    private int? ResolveProcessId(string appToken)
    {
        var normalized = NormalizeToken(appToken);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        lock (_syncRoot)
        {
            return _appLookup.TryGetValue(normalized, out var processId)
                ? processId
                : null;
        }
    }

    private void UpdateSessionInfo(
        string status,
        bool? isRelayConnected = null,
        bool? isRemoteConnected = null,
        DeviceMetadata? deviceMetadata = null,
        int? deviceLatencyMs = null,
        bool clearConnectedDevice = false,
        int? existingDeviceCount = null,
        int? connectedDeviceCount = null)
    {
        RemoteSessionInfo next;
        lock (_syncRoot)
        {
            var relayConnected = isRelayConnected ?? _sessionInfo.IsRelayConnected;
            var remoteConnected = isRemoteConnected ?? _sessionInfo.IsConnected;
            var statusText = string.IsNullOrWhiteSpace(status) ? _sessionInfo.Status : status;
            var relayRouteLabel = string.IsNullOrWhiteSpace(_activeRelayRouteLabel)
                ? _sessionInfo.RelayRouteLabel
                : _activeRelayRouteLabel;
            var relayProbeLatencyMs = _activeRelayProbeLatencyMs;
            if (!relayProbeLatencyMs.HasValue
                && string.Equals(relayRouteLabel, _sessionInfo.RelayRouteLabel, StringComparison.OrdinalIgnoreCase))
            {
                relayProbeLatencyMs = _sessionInfo.RelayProbeLatencyMs;
            }

            if (clearConnectedDevice)
            {
                ClearConnectedDeviceState_NoLock();
            }

            if (deviceMetadata.HasValue && !deviceMetadata.Value.IsEmpty)
            {
                var metadata = deviceMetadata.Value;
                _connectedDeviceId = SelectMetadataValue(metadata.DeviceId, _connectedDeviceId);
                _connectedDeviceName = SelectMetadataValue(metadata.DeviceName, _connectedDeviceName);
                _connectedDeviceLocation = SelectMetadataValue(metadata.DeviceLocation, _connectedDeviceLocation);
                _connectedConnectionType = SelectMetadataValue(metadata.ConnectionType, _connectedConnectionType);
                _connectedDeviceIpAddress = SelectMetadataValue(metadata.IpAddress, _connectedDeviceIpAddress);
                _connectedDeviceUserAgent = SelectMetadataValue(metadata.UserAgent, _connectedDeviceUserAgent);
            }

            if (deviceLatencyMs.HasValue)
            {
                _connectedDeviceLatencyMs = Math.Max(0, deviceLatencyMs.Value);
                _connectedDeviceLatencyUpdatedAtUtc = DateTimeOffset.UtcNow;
            }

            if (existingDeviceCount.HasValue)
            {
                _existingDeviceCount = Math.Max(0, existingDeviceCount.Value);
            }

            if (connectedDeviceCount.HasValue)
            {
                _connectedDeviceCount = Math.Max(0, connectedDeviceCount.Value);
                if (_connectedDeviceCount.Value == 0)
                {
                    _existingDeviceCount = null;
                }
            }
            else if (clearConnectedDevice && remoteConnected == false)
            {
                _connectedDeviceCount = 0;
                _existingDeviceCount = null;
            }

            next = new RemoteSessionInfo
            {
                SessionId = _sessionInfo.SessionId,
                PairCode = _sessionInfo.PairCode,
                ExpiresAtUtc = _sessionInfo.ExpiresAtUtc,
                QrUrl = _sessionInfo.QrUrl,
                Status = statusText,
                IsRelayConnected = relayConnected,
                IsConnected = remoteConnected,
                DeviceId = _connectedDeviceId,
                DeviceName = _connectedDeviceName,
                DeviceLocation = _connectedDeviceLocation,
                ConnectionType = _connectedConnectionType,
                IpAddress = _connectedDeviceIpAddress,
                UserAgent = _connectedDeviceUserAgent,
                DeviceLatencyMs = _connectedDeviceLatencyMs,
                DeviceLatencyUpdatedAtUtc = _connectedDeviceLatencyUpdatedAtUtc,
                ExistingDeviceCount = _existingDeviceCount,
                ConnectedDeviceCount = _connectedDeviceCount,
                RelayRouteLabel = relayRouteLabel,
                RelayProbeLatencyMs = relayProbeLatencyMs,
            };
            _sessionInfo = next;
        }

        RaiseSessionInfoChanged(next);
    }

    private void RaiseSessionInfoChanged(RemoteSessionInfo info)
    {
        SessionInfoChanged?.Invoke(info);
    }

    private void ClearConnectedDeviceState_NoLock()
    {
        _connectedDeviceId = string.Empty;
        _connectedDeviceName = string.Empty;
        _connectedDeviceLocation = string.Empty;
        _connectedConnectionType = string.Empty;
        _connectedDeviceIpAddress = string.Empty;
        _connectedDeviceUserAgent = string.Empty;
        _connectedDeviceLatencyMs = null;
        _connectedDeviceLatencyUpdatedAtUtc = DateTimeOffset.MinValue;
    }

    private static string SelectMetadataValue(string incoming, string existing)
    {
        return string.IsNullOrWhiteSpace(incoming) ? existing : incoming.Trim();
    }

    private void Log(string message)
    {
        var logLine = $"[{DateTime.Now:HH:mm:ss.fff}] [RemoteClient] {message}";
        Debug.WriteLine(logLine);
        LogMessage?.Invoke(logLine);

        try
        {
            lock (LogFileGate)
            {
                Directory.CreateDirectory(AudioBitPaths.LogsDirectoryPath);
                File.AppendAllText(Path.Combine(AudioBitPaths.LogsDirectoryPath, "remote-client.log"), logLine + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never break control flow.
        }
    }

    private static string BuildStateSignature(RemoteStateModel state)
    {
        var builder = new System.Text.StringBuilder(1024);
        builder.Append(state.MasterVolume).Append('|')
            .Append(state.MasterMuted ? '1' : '0').Append('|')
            .Append(state.MicMuted ? '1' : '0').Append('|')
            .Append(state.DefaultOutputDevice).Append('|')
            .Append(state.DefaultInputDevice).Append('|');

        foreach (var device in state.OutputDevices)
        {
            builder.Append(device.Id).Append(':').Append(device.Name).Append('|');
        }

        builder.Append('#');

        foreach (var device in state.InputDevices)
        {
            builder.Append(device.Id).Append(':').Append(device.Name).Append('|');
        }

        builder.Append('#');

        foreach (var app in state.Apps)
        {
            builder.Append(app.Id).Append(':')
                .Append(app.Name).Append(':')
                .Append(app.ProcessId).Append(':')
                .Append(app.Volume).Append(':')
                .Append(app.Muted ? '1' : '0').Append(':')
                .Append(app.OutputDevice).Append(':')
                .Append(app.InputDevice).Append('|');
        }

        return builder.ToString();
    }

    private static HttpClient CreateGeoIpClient()
    {
        var client = new HttpClient
        {
            Timeout = GeoIpLookupTimeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AudioBit");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static RemoteStateModel CloneState(RemoteStateModel source)
    {
        return new RemoteStateModel
        {
            SessionId = source.SessionId,
            Revision = source.Revision,
            MasterVolume = source.MasterVolume,
            MasterMuted = source.MasterMuted,
            MicMuted = source.MicMuted,
            DefaultOutputDevice = source.DefaultOutputDevice,
            DefaultInputDevice = source.DefaultInputDevice,
            OutputDevices = source.OutputDevices.Select(device => new RemoteDeviceModel
            {
                Id = device.Id,
                Name = device.Name,
            }).ToList(),
            InputDevices = source.InputDevices.Select(device => new RemoteDeviceModel
            {
                Id = device.Id,
                Name = device.Name,
            }).ToList(),
            Apps = source.Apps.Select(app => new RemoteAppStateModel
            {
                Id = app.Id,
                Name = app.Name,
                ProcessId = app.ProcessId,
                Volume = app.Volume,
                Muted = app.Muted,
                OutputDevice = app.OutputDevice,
                InputDevice = app.InputDevice,
                Peak = app.Peak,
            }).ToList(),
        };
    }

    private static string NormalizeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return string.Empty;
        }

        var value = token.Trim();
        if (value.StartsWith("p", StringComparison.OrdinalIgnoreCase))
        {
            return value.ToLowerInvariant();
        }

        if (value.StartsWith("a_", StringComparison.OrdinalIgnoreCase))
        {
            var body = NormalizeAppLookupKey(value[2..]);
            return body.Length == 0 ? string.Empty : $"a_{body}";
        }

        return NormalizeAppLookupKey(value);
    }

    private static string NormalizeAppLookupKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        var buffer = new char[trimmed.Length];
        var length = 0;
        var pendingSeparator = false;

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSeparator && length > 0)
                {
                    buffer[length++] = '_';
                }

                buffer[length++] = char.ToLowerInvariant(ch);
                pendingSeparator = false;
            }
            else if (length > 0)
            {
                pendingSeparator = true;
            }
        }

        return length == 0 ? string.Empty : new string(buffer, 0, length);
    }

    private static bool TryReadDeviceMetadata(JsonElement root, JsonElement payload, out DeviceMetadata metadata)
    {
        if (TryReadDeviceMetadataFromContainer(payload, out metadata))
        {
            return true;
        }

        return TryReadDeviceMetadataFromContainer(root, out metadata);
    }

    private static bool TryReadDeviceMetadataFromContainer(JsonElement container, out DeviceMetadata metadata)
    {
        if (container.ValueKind != JsonValueKind.Object)
        {
            metadata = default;
            return false;
        }

        if (container.TryGetProperty("device", out var device)
            && device.ValueKind == JsonValueKind.Object
            && TryReadDeviceMetadataFromObject(device, out metadata))
        {
            return true;
        }

        if (container.TryGetProperty("meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object
            && TryReadDeviceMetadataFromObject(meta, out metadata))
        {
            return true;
        }

        if (container.TryGetProperty("remote", out var remote)
            && remote.ValueKind == JsonValueKind.Object
            && TryReadDeviceMetadataFromObject(remote, out metadata))
        {
            return true;
        }

        if (container.TryGetProperty("client", out var client)
            && client.ValueKind == JsonValueKind.Object
            && TryReadDeviceMetadataFromObject(client, out metadata))
        {
            return true;
        }

        return TryReadDeviceMetadataFromObject(container, out metadata);
    }

    private static bool TryReadDeviceMetadataFromObject(JsonElement source, out DeviceMetadata metadata)
    {
        metadata = new DeviceMetadata(
            ReadFirstString(source, "device_id", "id", "rid", "remote_id"),
            ReadFirstString(source, "device_name", "remote_name", "name"),
            ReadFirstString(source, "device_location", "location"),
            ReadFirstString(source, "connection_type", "transport", "via", "protocol"),
            ReadFirstString(source, "ip", "ip_address", "remote_ip"),
            ReadFirstString(source, "user_agent", "ua"));

        return !metadata.IsEmpty;
    }

    private static string ReadFirstString(JsonElement root, params string[] names)
    {
        for (var i = 0; i < names.Length; i++)
        {
            if (!root.TryGetProperty(names[i], out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }

                continue;
            }

            if (property.ValueKind == JsonValueKind.Number
                || property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.ToString();
            }
        }

        return string.Empty;
    }

    private static string ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static JsonElement GetPayload(JsonElement root)
    {
        return root.TryGetProperty("d", out var payload) && payload.ValueKind == JsonValueKind.Object
            ? payload
            : root;
    }

    private static bool? ReadBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when property.TryGetInt32(out var value) => value != 0,
            JsonValueKind.String when bool.TryParse(property.GetString(), out var parsed) => parsed,
            JsonValueKind.String when int.TryParse(property.GetString(), out var parsedNumeric) => parsedNumeric != 0,
            _ => null,
        };
    }

    private static int? ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        if (property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out var numericDouble)
            && !double.IsNaN(numericDouble)
            && !double.IsInfinity(numericDouble))
        {
            return (int)Math.Round(numericDouble, MidpointRounding.AwayFromZero);
        }

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        if (property.ValueKind == JsonValueKind.String
            && double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble)
            && !double.IsNaN(parsedDouble)
            && !double.IsInfinity(parsedDouble))
        {
            return (int)Math.Round(parsedDouble, MidpointRounding.AwayFromZero);
        }

        return null;
    }

    private static int? ReadLatencyMs(JsonElement payload, JsonElement root)
    {
        static int? ReadLatencyFromTimestamps(JsonElement source)
        {
            var measuredAt = ReadLong(source, "measured_at")
                ?? ReadLong(source, "measuredAt")
                ?? ReadLong(source, "measured");
            var ts = ReadLong(source, "ts")
                ?? ReadLong(source, "sent_at")
                ?? ReadLong(source, "sentAt");
            if (!measuredAt.HasValue || !ts.HasValue)
            {
                return null;
            }

            var delta = measuredAt.Value - ts.Value;
            if (delta < 0)
            {
                return null;
            }

            return delta > int.MaxValue ? int.MaxValue : (int)delta;
        }

        static int? ReadFrom(JsonElement source)
        {
            var direct = ReadFirstInt(
                source,
                "rtt",
                "rtt_ms",
                "rttMs",
                "latency",
                "latency_ms",
                "latencyMs",
                "ping",
                "ping_ms",
                "pingMs",
                "ms");
            if (direct.HasValue)
            {
                return direct;
            }

            if (source.TryGetProperty("latency", out var latencyObject) && latencyObject.ValueKind == JsonValueKind.Object)
            {
                var nested = ReadFirstInt(latencyObject, "rtt", "rtt_ms", "rttMs", "ms", "value");
                if (nested.HasValue)
                {
                    return nested;
                }

                var computed = ReadLatencyFromTimestamps(latencyObject);
                if (computed.HasValue)
                {
                    return computed;
                }
            }

            if (source.TryGetProperty("ping", out var pingObject) && pingObject.ValueKind == JsonValueKind.Object)
            {
                var nested = ReadFirstInt(pingObject, "rtt", "rtt_ms", "rttMs", "ms", "value");
                if (nested.HasValue)
                {
                    return nested;
                }

                var computed = ReadLatencyFromTimestamps(pingObject);
                if (computed.HasValue)
                {
                    return computed;
                }
            }

            return ReadLatencyFromTimestamps(source);
        }

        return ReadFrom(payload) ?? ReadFrom(root);
    }

    private static int? ReadFirstInt(JsonElement root, params string[] names)
    {
        for (var i = 0; i < names.Length; i++)
        {
            var value = ReadInt(root, names[i]);
            if (value.HasValue)
            {
                return value;
            }
        }

        return null;
    }

    private static long? ReadLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var numeric))
        {
            return numeric;
        }

        if (property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private async Task TryEnrichDeviceLocationAsync(DeviceMetadata metadata)
    {
        if (metadata.IsEmpty || !string.IsNullOrWhiteSpace(metadata.DeviceLocation))
        {
            return;
        }

        if (!TryNormalizeIpAddress(metadata.IpAddress, out var address, out var normalizedIp))
        {
            return;
        }

        if (IsPrivateIpAddress(address))
        {
            return;
        }

        try
        {
            var location = await GetGeoLocationAsync(normalizedIp).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(location))
            {
                return;
            }

            var currentIp = string.Empty;
            lock (_syncRoot)
            {
                currentIp = _connectedDeviceIpAddress;
            }

            if (!string.IsNullOrWhiteSpace(currentIp)
                && !IsSameIpAddress(currentIp, normalizedIp))
            {
                return;
            }

            UpdateSessionInfo(
                status: string.Empty,
                deviceMetadata: new DeviceMetadata(
                    string.Empty,
                    string.Empty,
                    location,
                    string.Empty,
                    normalizedIp,
                    string.Empty));
        }
        catch (Exception ex)
        {
            Log($"GeoIP lookup failed for {normalizedIp}: {ex.Message}");
        }
    }

    private Task<string?> GetGeoLocationAsync(string ipAddress)
    {
        lock (_syncRoot)
        {
            if (_geoIpCache.TryGetValue(ipAddress, out var cached))
            {
                return Task.FromResult<string?>(cached);
            }

            if (_geoIpLookups.TryGetValue(ipAddress, out var existing))
            {
                return existing;
            }

            if (_geoIpLastAttemptUtc.TryGetValue(ipAddress, out var lastAttempt)
                && DateTimeOffset.UtcNow - lastAttempt < GeoIpRetryDelay)
            {
                return Task.FromResult<string?>(null);
            }

            _geoIpLastAttemptUtc[ipAddress] = DateTimeOffset.UtcNow;

            var task = FetchGeoLocationAsync(ipAddress);
            _geoIpLookups[ipAddress] = task;
            return task;
        }
    }

    private async Task<string?> FetchGeoLocationAsync(string ipAddress)
    {
        string? location = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            cts.CancelAfter(GeoIpLookupTimeout);

            var requestUri = new Uri($"https://ipapi.co/{ipAddress}/json/");
            using var response = await GeoIpClient.GetAsync(requestUri, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var errorProperty)
                && errorProperty.ValueKind == JsonValueKind.True)
            {
                return null;
            }

            var city = ReadString(root, "city");
            var region = ReadString(root, "region");
            var country = ReadString(root, "country_name");
            if (string.IsNullOrWhiteSpace(country))
            {
                country = ReadString(root, "country");
            }

            location = FormatLocation(city, region, country);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log($"GeoIP lookup failed for {ipAddress}: {ex.Message}");
            return null;
        }
        finally
        {
            lock (_syncRoot)
            {
                _geoIpLookups.Remove(ipAddress);
                if (!string.IsNullOrWhiteSpace(location))
                {
                    _geoIpCache[ipAddress] = location;
                }
            }
        }

        return location;
    }

    private static string FormatLocation(string city, string region, string country)
    {
        var parts = new List<string>(3);
        AddLocationPart(parts, city);
        AddLocationPart(parts, region);
        AddLocationPart(parts, country);
        return parts.Count == 0 ? string.Empty : string.Join(", ", parts);
    }

    private static void AddLocationPart(List<string> parts, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (parts.Any(part => string.Equals(part, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        parts.Add(trimmed);
    }

    private static bool IsSameIpAddress(string left, string right)
    {
        if (TryNormalizeIpAddress(left, out _, out var leftNormalized)
            && TryNormalizeIpAddress(right, out _, out var rightNormalized))
        {
            return string.Equals(leftNormalized, rightNormalized, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeIpAddress(string raw, out IPAddress address, out string normalized)
    {
        address = IPAddress.None;
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (IPAddress.TryParse(trimmed, out var parsedAddress) && parsedAddress is not null)
        {
            address = parsedAddress;
            normalized = address.ToString();
            return true;
        }

        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            var end = trimmed.IndexOf(']');
            if (end > 1)
            {
                var host = trimmed.Substring(1, end - 1);
                if (IPAddress.TryParse(host, out parsedAddress) && parsedAddress is not null)
                {
                    address = parsedAddress;
                    normalized = address.ToString();
                    return true;
                }
            }
        }

        var lastColon = trimmed.LastIndexOf(':');
        if (lastColon > 0 && trimmed.IndexOf(':') == lastColon)
        {
            var host = trimmed.Substring(0, lastColon);
            if (IPAddress.TryParse(host, out parsedAddress) && parsedAddress is not null)
            {
                address = parsedAddress;
                normalized = address.ToString();
                return true;
            }
        }

        return false;
    }

    private static bool IsPrivateIpAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }

    private static IReadOnlyList<RelayTarget> BuildRelayTargets(
        Uri? primaryHttp,
        Uri? primaryWs,
        Uri fallbackHttp,
        Uri fallbackWs)
    {
        var targets = new List<RelayTarget>(2);
        var resolvedSecondaryHttp = primaryHttp ?? DeriveHttpBaseFromWs(primaryWs);
        var resolvedSecondaryWs = primaryWs ?? DeriveWsFromHttp(primaryHttp);

        // Keep Railway/default endpoint as primary, with optional configured endpoint as secondary.
        AddUniqueRelayTarget(targets, new RelayTarget("primary", fallbackHttp, fallbackWs, true));

        if (resolvedSecondaryHttp is not null && resolvedSecondaryWs is not null)
        {
            AddUniqueRelayTarget(targets, new RelayTarget("secondary", resolvedSecondaryHttp, resolvedSecondaryWs, false));
        }

        return targets;
    }

    private static void AddUniqueRelayTarget(List<RelayTarget> targets, RelayTarget candidate)
    {
        var normalizedHttp = NormalizeRelayUri(candidate.HttpBaseUri);
        var normalizedWs = NormalizeRelayUri(candidate.WsEndpoint);
        for (var index = 0; index < targets.Count; index++)
        {
            var existing = targets[index];
            if (string.Equals(normalizedHttp, NormalizeRelayUri(existing.HttpBaseUri), StringComparison.OrdinalIgnoreCase)
                && string.Equals(normalizedWs, NormalizeRelayUri(existing.WsEndpoint), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        targets.Add(candidate);
    }

    private static string NormalizeRelayUri(Uri value)
    {
        return value.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped)
            .TrimEnd('/');
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

    private static Uri? GetConfiguredUriOrNull(string envVar)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        return Uri.TryCreate(value, UriKind.Absolute, out var configured)
            ? configured
            : null;
    }

    private static Uri GetConfiguredUri(string envVar, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(envVar);
        if (Uri.TryCreate(value, UriKind.Absolute, out var configured))
        {
            return configured;
        }

        return new Uri(fallback, UriKind.Absolute);
    }

    private static int GetProbeAttemptCount(RelayTarget target)
    {
        if (target.IsPrimary
            || target.HttpBaseUri.Host.Contains("render", StringComparison.OrdinalIgnoreCase)
            || target.WsEndpoint.Host.Contains("render", StringComparison.OrdinalIgnoreCase))
        {
            return PrimaryProbeAttempts;
        }

        return SecondaryProbeAttempts;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private readonly record struct DeviceMetadata(
        string DeviceId,
        string DeviceName,
        string DeviceLocation,
        string ConnectionType,
        string IpAddress,
        string UserAgent)
    {
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(DeviceId)
            && string.IsNullOrWhiteSpace(DeviceName)
            && string.IsNullOrWhiteSpace(DeviceLocation)
            && string.IsNullOrWhiteSpace(ConnectionType)
            && string.IsNullOrWhiteSpace(IpAddress)
            && string.IsNullOrWhiteSpace(UserAgent);
    }

    private readonly record struct RelayTarget(string Name, Uri HttpBaseUri, Uri WsEndpoint, bool IsPrimary);

    private readonly record struct SessionCandidate(
        int Index,
        RelayTarget Target,
        RemoteSessionInfo? Session,
        int Score,
        Exception? Error,
        int ProbeLatencyMs,
        DateTimeOffset ObservedAtUtc,
        int SuccessfulAttempt,
        int TotalAttempts);
}
