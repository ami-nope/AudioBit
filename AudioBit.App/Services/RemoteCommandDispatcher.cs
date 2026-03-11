using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using AudioBit.Core;

namespace AudioBit.App.Services;

internal sealed class RemoteCommandDispatcher
{
    private readonly AudioSessionService _audioSessionService;
    private readonly Action<string> _log;
    private static readonly TimeSpan DefaultEndpointDebounce = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan DefaultEndpointMinInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan DefaultSwitchSuppressionWindow = TimeSpan.FromSeconds(4);
    private readonly object _routeCommandGate = new();
    private DateTime _lastAppOutputRouteUtc = DateTime.MinValue;
    private DateTime _lastAppInputRouteUtc = DateTime.MinValue;
    private string _lastAppOutputDeviceId = string.Empty;
    private string _lastAppInputDeviceId = string.Empty;

    public RemoteCommandDispatcher(AudioSessionService audioSessionService, Action<string> log)
    {
        _audioSessionService = audioSessionService;
        _log = log;
    }

    public Task<RemoteCommandResult> DispatchAsync(
        JsonElement payload,
        Func<string, int?> resolveAppProcessId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var op = ReadString(payload, "op");
        if (string.IsNullOrWhiteSpace(op))
        {
            return Task.FromResult(RemoteCommandResult.Failure("invalid_arg"));
        }

        _log($"Remote command received: {op}");

        try
        {
            switch (op)
            {
                case "set_master_volume":
                    if (!TryReadInt(payload, "v", out var masterVolume))
                    {
                        return Task.FromResult(RemoteCommandResult.Failure("invalid_arg"));
                    }

                    _audioSessionService.SetMasterVolume(Math.Clamp(masterVolume, 0, 100) / 100.0f);
                    return Task.FromResult(RemoteCommandResult.Success);

                case "mute_master":
                    if (!TryReadBool(payload, "mu", out var muteMaster))
                    {
                        return Task.FromResult(RemoteCommandResult.Failure("invalid_arg"));
                    }

                    _audioSessionService.SetMasterMute(muteMaster);
                    return Task.FromResult(RemoteCommandResult.Success);

                case "mute_mic":
                    if (!TryReadBool(payload, "mu", out var muteMic))
                    {
                        return Task.FromResult(RemoteCommandResult.Failure("invalid_arg"));
                    }

                    _audioSessionService.SetDefaultCaptureMute(muteMic);
                    return Task.FromResult(RemoteCommandResult.Success);

                case "set_app_volume":
                    return Task.FromResult(HandleSetAppVolume(payload, resolveAppProcessId));

                case "mute_app":
                    return Task.FromResult(HandleMuteApp(payload, resolveAppProcessId));

                case "set_app_output_device":
                    return Task.FromResult(HandleSetAppDevice(payload, resolveAppProcessId, isOutput: true));

                case "set_app_input_device":
                    return Task.FromResult(HandleSetAppDevice(payload, resolveAppProcessId, isOutput: false));

                case "set_output_device":
                    return Task.FromResult(HandleSetDefaultDevice(payload, resolveAppProcessId, isOutput: true));

                case "set_input_device":
                    return Task.FromResult(HandleSetDefaultDevice(payload, resolveAppProcessId, isOutput: false));

                case "play_soundboard_clip":
                    return Task.FromResult(RemoteCommandResult.Failure("not_supported"));

                default:
                    return Task.FromResult(RemoteCommandResult.Failure("unknown_command"));
            }
        }
        catch (Exception ex)
        {
            _log($"Remote command failure ({op}): {ex.Message}");
            return Task.FromResult(RemoteCommandResult.Failure("internal_error"));
        }
    }

    private RemoteCommandResult HandleSetAppVolume(JsonElement payload, Func<string, int?> resolveAppProcessId)
    {
        var app = ReadString(payload, "app");
        if (!TryResolveAppProcessId(app, resolveAppProcessId, out var processId)
            || !TryReadInt(payload, "v", out var volume))
        {
            return RemoteCommandResult.Failure("invalid_arg");
        }

        _audioSessionService.SetVolume(processId, Math.Clamp(volume, 0, 100) / 100.0f);
        return RemoteCommandResult.Success;
    }

    private RemoteCommandResult HandleMuteApp(JsonElement payload, Func<string, int?> resolveAppProcessId)
    {
        var app = ReadString(payload, "app");
        if (!TryResolveAppProcessId(app, resolveAppProcessId, out var processId)
            || !TryReadBool(payload, "mu", out var isMuted))
        {
            return RemoteCommandResult.Failure("invalid_arg");
        }

        _audioSessionService.SetMute(processId, isMuted);
        return RemoteCommandResult.Success;
    }

    private RemoteCommandResult HandleSetAppDevice(
        JsonElement payload,
        Func<string, int?> resolveAppProcessId,
        bool isOutput)
    {
        var app = ReadString(payload, "app");
        if (!TryResolveAppProcessId(app, resolveAppProcessId, out var processId))
        {
            return RemoteCommandResult.Failure("app_not_found");
        }

        var propertyName = isOutput ? "out" : "in";
        var deviceId = ReadString(payload, propertyName);
        if (!IsKnownDevice(deviceId, isOutput))
        {
            return RemoteCommandResult.Failure("invalid_device");
        }

        if (isOutput)
        {
            _audioSessionService.SetPreferredPlaybackDevice(processId, deviceId);
        }
        else
        {
            _audioSessionService.SetPreferredCaptureDevice(processId, deviceId);
        }

        MarkAppRouteCommand(deviceId, isOutput);
        return RemoteCommandResult.Success;
    }

    private RemoteCommandResult HandleSetDefaultDevice(
        JsonElement payload,
        Func<string, int?> resolveAppProcessId,
        bool isOutput)
    {
        // Some remote builds can emit set_output_device/set_input_device with an app token.
        // Route those as per-app commands so global defaults are not changed accidentally.
        var appToken = ReadString(payload, "app");
        if (!string.IsNullOrWhiteSpace(appToken))
        {
            return HandleSetAppDevice(payload, resolveAppProcessId, isOutput);
        }

        var propertyName = isOutput ? "out" : "in";
        var deviceId = ReadString(payload, propertyName);
        if (!IsKnownDevice(deviceId, isOutput, allowSystemDefault: false))
        {
            return RemoteCommandResult.Failure("invalid_device");
        }

        if (ShouldSuppressDefaultSwitch(deviceId, isOutput))
        {
            _log($"Suppressed global {(isOutput ? "output" : "input")} switch after recent app route: device='{deviceId}'");
            return RemoteCommandResult.Success;
        }

        var success = DefaultAudioEndpointSwitcher.QueueDefaultEndpointSwitch(deviceId, isOutput, _log);
        return success
            ? RemoteCommandResult.Success
            : RemoteCommandResult.Failure("invalid_device");
    }

    private bool IsKnownDevice(string? deviceId, bool isOutput, bool allowSystemDefault = true)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return allowSystemDefault;
        }

        var availableDevices = isOutput
            ? _audioSessionService.RenderDeviceOptions
            : _audioSessionService.CaptureDeviceOptions;
        return availableDevices.Any(device => string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    private void MarkAppRouteCommand(string? deviceId, bool isOutput)
    {
        var normalizedDeviceId = string.IsNullOrWhiteSpace(deviceId)
            ? string.Empty
            : deviceId.Trim();

        lock (_routeCommandGate)
        {
            if (isOutput)
            {
                _lastAppOutputRouteUtc = DateTime.UtcNow;
                _lastAppOutputDeviceId = normalizedDeviceId;
                return;
            }

            _lastAppInputRouteUtc = DateTime.UtcNow;
            _lastAppInputDeviceId = normalizedDeviceId;
        }
    }

    private bool ShouldSuppressDefaultSwitch(string? deviceId, bool isOutput)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        lock (_routeCommandGate)
        {
            var now = DateTime.UtcNow;
            var lastRouteUtc = isOutput ? _lastAppOutputRouteUtc : _lastAppInputRouteUtc;
            var lastRouteDeviceId = isOutput ? _lastAppOutputDeviceId : _lastAppInputDeviceId;
            if (now - lastRouteUtc > DefaultSwitchSuppressionWindow)
            {
                return false;
            }

            return string.Equals(lastRouteDeviceId, deviceId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool TryResolveAppProcessId(string appToken, Func<string, int?> resolveAppProcessId, out int processId)
    {
        processId = 0;
        if (string.IsNullOrWhiteSpace(appToken))
        {
            return false;
        }

        if (int.TryParse(appToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out processId)
            && processId > 0)
        {
            return true;
        }

        var resolved = resolveAppProcessId(appToken);
        if (!resolved.HasValue || resolved.Value <= 0)
        {
            return false;
        }

        processId = resolved.Value;
        return true;
    }

    private static string ReadString(JsonElement payload, string propertyName)
    {
        return payload.TryGetProperty(propertyName, out var property)
            ? property.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool TryReadInt(JsonElement payload, string propertyName, out int value)
    {
        value = 0;
        if (!payload.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetInt32(out value);
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        return false;
    }

    private static bool TryReadBool(JsonElement payload, string propertyName, out bool value)
    {
        value = false;
        if (!payload.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        switch (property.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.Number when property.TryGetInt32(out var numeric):
                value = numeric != 0;
                return true;
            case JsonValueKind.String:
            {
                var raw = property.GetString();
                if (string.Equals(raw, "1", StringComparison.Ordinal))
                {
                    value = true;
                    return true;
                }

                if (string.Equals(raw, "0", StringComparison.Ordinal))
                {
                    value = false;
                    return true;
                }

                if (bool.TryParse(raw, out var parsed))
                {
                    value = parsed;
                    return true;
                }

                return false;
            }
            default:
                return false;
        }
    }

    private static class DefaultAudioEndpointSwitcher
    {
        private const int MaxConsecutiveSwitchFailures = 8;
        private static readonly object SyncRoot = new();
        private static string _pendingOutputDeviceId = string.Empty;
        private static string _pendingInputDeviceId = string.Empty;
        private static DateTime _lastOutputSwitchUtc = DateTime.MinValue;
        private static DateTime _lastInputSwitchUtc = DateTime.MinValue;
        private static int _outputSwitchFailureCount;
        private static int _inputSwitchFailureCount;
        private static int _isOutputWorkerRunning;
        private static int _isInputWorkerRunning;

        public static bool QueueDefaultEndpointSwitch(string deviceId, bool isOutput, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (isOutput)
                {
                    _pendingOutputDeviceId = deviceId;
                    _outputSwitchFailureCount = 0;
                }
                else
                {
                    _pendingInputDeviceId = deviceId;
                    _inputSwitchFailureCount = 0;
                }
            }

            if (isOutput)
            {
                if (Interlocked.CompareExchange(ref _isOutputWorkerRunning, 1, 0) == 0)
                {
                    _ = Task.Run(() => RunSwitchQueueAsync(isOutput: true, log));
                }
            }
            else if (Interlocked.CompareExchange(ref _isInputWorkerRunning, 1, 0) == 0)
            {
                _ = Task.Run(() => RunSwitchQueueAsync(isOutput: false, log));
            }

            return true;
        }

        private static async Task RunSwitchQueueAsync(bool isOutput, Action<string> log)
        {
            try
            {
                while (true)
                {
                    string deviceId;
                    TimeSpan delay;

                    lock (SyncRoot)
                    {
                        deviceId = isOutput ? _pendingOutputDeviceId : _pendingInputDeviceId;
                        if (string.IsNullOrWhiteSpace(deviceId))
                        {
                            break;
                        }

                        var lastSwitchUtc = isOutput ? _lastOutputSwitchUtc : _lastInputSwitchUtc;
                        var elapsed = lastSwitchUtc == DateTime.MinValue
                            ? TimeSpan.MaxValue
                            : DateTime.UtcNow - lastSwitchUtc;
                        var intervalDelay = elapsed < DefaultEndpointMinInterval
                            ? DefaultEndpointMinInterval - elapsed
                            : TimeSpan.Zero;
                        delay = intervalDelay > DefaultEndpointDebounce ? intervalDelay : DefaultEndpointDebounce;
                    }

                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                    }

                    lock (SyncRoot)
                    {
                        deviceId = isOutput ? _pendingOutputDeviceId : _pendingInputDeviceId;
                    }

                    if (string.IsNullOrWhiteSpace(deviceId))
                    {
                        continue;
                    }

                    var success = TrySetDefaultEndpointCore(deviceId);
                    lock (SyncRoot)
                    {
                        if (isOutput)
                        {
                            _lastOutputSwitchUtc = DateTime.UtcNow;
                            if (success)
                            {
                                _outputSwitchFailureCount = 0;
                                if (string.Equals(_pendingOutputDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                                {
                                    _pendingOutputDeviceId = string.Empty;
                                }
                            }
                            else
                            {
                                _outputSwitchFailureCount = Math.Min(_outputSwitchFailureCount + 1, MaxConsecutiveSwitchFailures);
                                if (_outputSwitchFailureCount >= MaxConsecutiveSwitchFailures
                                    && string.Equals(_pendingOutputDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                                {
                                    _pendingOutputDeviceId = string.Empty;
                                    _outputSwitchFailureCount = 0;
                                }
                            }
                        }
                        else
                        {
                            _lastInputSwitchUtc = DateTime.UtcNow;
                            if (success)
                            {
                                _inputSwitchFailureCount = 0;
                                if (string.Equals(_pendingInputDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                                {
                                    _pendingInputDeviceId = string.Empty;
                                }
                            }
                            else
                            {
                                _inputSwitchFailureCount = Math.Min(_inputSwitchFailureCount + 1, MaxConsecutiveSwitchFailures);
                                if (_inputSwitchFailureCount >= MaxConsecutiveSwitchFailures
                                    && string.Equals(_pendingInputDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                                {
                                    _pendingInputDeviceId = string.Empty;
                                    _inputSwitchFailureCount = 0;
                                }
                            }
                        }
                    }

                    log($"Default {(isOutput ? "output" : "input")} switch applied: success={success} device='{deviceId}'");
                }
            }
            finally
            {
                if (isOutput)
                {
                    Interlocked.Exchange(ref _isOutputWorkerRunning, 0);
                }
                else
                {
                    Interlocked.Exchange(ref _isInputWorkerRunning, 0);
                }

                bool hasPending;
                lock (SyncRoot)
                {
                    hasPending = isOutput
                        ? !string.IsNullOrWhiteSpace(_pendingOutputDeviceId)
                        : !string.IsNullOrWhiteSpace(_pendingInputDeviceId);
                }

                if (hasPending && isOutput)
                {
                    if (Interlocked.CompareExchange(ref _isOutputWorkerRunning, 1, 0) == 0)
                    {
                        _ = Task.Run(() => RunSwitchQueueAsync(isOutput: true, log));
                    }
                }
                else if (hasPending && Interlocked.CompareExchange(ref _isInputWorkerRunning, 1, 0) == 0)
                {
                    _ = Task.Run(() => RunSwitchQueueAsync(isOutput: false, log));
                }
            }
        }

        private static bool TrySetDefaultEndpointCore(string deviceId)
        {
            IPolicyConfig? policyConfig = null;
            try
            {
                object policyConfigObject = new PolicyConfigClient();
                policyConfig = (IPolicyConfig)policyConfigObject;
                return policyConfig.SetDefaultEndpoint(deviceId, ERole.Console) == 0
                    && policyConfig.SetDefaultEndpoint(deviceId, ERole.Multimedia) == 0
                    && policyConfig.SetDefaultEndpoint(deviceId, ERole.Communications) == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (policyConfig is not null)
                {
                    Marshal.ReleaseComObject(policyConfig);
                }
            }
        }

        private enum ERole
        {
            Console = 0,
            Multimedia = 1,
            Communications = 2,
        }

        [ComImport]
        [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private sealed class PolicyConfigClient;

        [ComImport]
        [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            int GetMixFormat(string deviceName, IntPtr formatPointer);
            int GetDeviceFormat(string deviceName, int mode, IntPtr formatPointer);
            int ResetDeviceFormat(string deviceName);
            int SetDeviceFormat(string deviceName, IntPtr endpointFormat, IntPtr mixFormat);
            int GetProcessingPeriod(string deviceName, int mode, IntPtr defaultPeriod, IntPtr minimumPeriod);
            int SetProcessingPeriod(string deviceName, IntPtr period);
            int GetShareMode(string deviceName, IntPtr mode);
            int SetShareMode(string deviceName, IntPtr mode);
            int GetPropertyValue(string deviceName, IntPtr propertyKey, IntPtr propertyValue);
            int SetPropertyValue(string deviceName, IntPtr propertyKey, IntPtr propertyValue);
            int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ERole role);
            int SetEndpointVisibility(string deviceName, int visible);
        }
    }
}

internal readonly record struct RemoteCommandResult(bool Ok, string Error)
{
    public static readonly RemoteCommandResult Success = new(true, string.Empty);

    public static RemoteCommandResult Failure(string error) => new(false, error);
}
