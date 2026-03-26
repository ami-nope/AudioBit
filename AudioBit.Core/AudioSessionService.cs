using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using DrawingIcon = System.Drawing.Icon;
using DrawingSystemIcons = System.Drawing.SystemIcons;

namespace AudioBit.Core;

public sealed class AudioSessionService : IDisposable
{
    private static readonly TimeSpan SilentRetention = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DeviceInventoryRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeviceRouteRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DeviceSwitchGracePeriod = TimeSpan.FromMilliseconds(1200);
    private static readonly TimeSpan PendingRouteWriteRetryInterval = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan RouteReadbackGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RouteWriteMinInterval = TimeSpan.FromMilliseconds(650);
    private static readonly TimeSpan VolumeWriteGracePeriod = TimeSpan.FromMilliseconds(500);
    private const int MaxPendingRouteWriteRetries = 6;
    private const int MaxIconCacheEntries = 256;

    private readonly object _syncRoot = new();
    private readonly object _audioPolicyConfigSyncRoot = new();
    private readonly MMDeviceEnumerator _deviceEnumerator;
    private readonly EndpointNotificationClient _endpointNotificationClient;
    private readonly AudioPolicyConfigBridge _audioPolicyConfigBridge;
    private readonly Dictionary<int, AppAudioModel> _appsByProcessId = new();
    private readonly Dictionary<int, DeviceRouteCacheEntry> _deviceRouteCache = new();
    private readonly Dictionary<string, AppRoutePreference> _routePreferencesByAppKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pinnedAppKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, SessionGroup> _liveGroupsBuffer = new();
    private readonly HashSet<int> _visibleProcessIdsBuffer = new();
    private readonly List<int> _expiredProcessIdsBuffer = new();
    private readonly HashSet<int> _routeTargetProcessIdsBuffer = new();
    private readonly Dictionary<int, DateTime> _volumeWriteTimestamps = new();
    private readonly Dictionary<int, DateTime> _muteWriteTimestamps = new();
    private readonly ImageSource _defaultIcon;

    private bool _disposed;
    private bool _defaultDeviceChanged = true;
    private bool _deviceInventoryChanged = true;
    private DateTime _defaultDeviceChangeUtc = DateTime.UtcNow;
    private DateTime _lastDeviceInventoryRefreshUtc = DateTime.MinValue;
    private string _currentPlaybackDeviceId = string.Empty;
    private string _currentDeviceName = "No playback device";
    private string _currentCaptureDeviceId = string.Empty;
    private string _currentCaptureDeviceName = "No input device";
    private bool _hasPlaybackDevice;
    private bool _hasCaptureDevice;
    private bool _isDefaultCaptureMuted;
    private float _masterVolume;
    private bool _isMasterMuted;
    private IReadOnlyList<AudioDeviceOptionModel> _renderDeviceOptions = Array.Empty<AudioDeviceOptionModel>();
    private IReadOnlyList<AudioDeviceOptionModel> _captureDeviceOptions = Array.Empty<AudioDeviceOptionModel>();

    public AudioSessionService()
    {
        _defaultIcon = CreateDefaultIcon();
        _audioPolicyConfigBridge = new AudioPolicyConfigBridge();
        _deviceEnumerator = new MMDeviceEnumerator();
        _endpointNotificationClient = new EndpointNotificationClient(
            onDeviceInventoryChanged: () => _deviceInventoryChanged = true,
            onDefaultPlaybackDeviceChanged: OnDefaultPlaybackDeviceChanged);

        try
        {
            _deviceEnumerator.RegisterEndpointNotificationCallback(_endpointNotificationClient);
        }
        catch
        {
            // Device change notifications are optional; refresh still works by polling.
        }
    }

    public string CurrentDeviceName
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentDeviceName;
            }
        }
    }

    public bool HasPlaybackDevice
    {
        get
        {
            lock (_syncRoot)
            {
                return _hasPlaybackDevice;
            }
        }
    }

    public string CurrentCaptureDeviceName
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentCaptureDeviceName;
            }
        }
    }

    public string CurrentPlaybackDeviceId
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentPlaybackDeviceId;
            }
        }
    }

    public string CurrentCaptureDeviceId
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentCaptureDeviceId;
            }
        }
    }

    public bool HasCaptureDevice
    {
        get
        {
            lock (_syncRoot)
            {
                return _hasCaptureDevice;
            }
        }
    }

    public bool IsDefaultCaptureMuted
    {
        get
        {
            lock (_syncRoot)
            {
                return _isDefaultCaptureMuted;
            }
        }
    }

    public float MasterVolume
    {
        get
        {
            lock (_syncRoot)
            {
                return _masterVolume;
            }
        }
    }

    public IReadOnlyList<AudioDeviceOptionModel> RenderDeviceOptions
    {
        get
        {
            lock (_syncRoot)
            {
                return _renderDeviceOptions;
            }
        }
    }

    public IReadOnlyList<AudioDeviceOptionModel> CaptureDeviceOptions
    {
        get
        {
            lock (_syncRoot)
            {
                return _captureDeviceOptions;
            }
        }
    }

    public bool IsMasterMuted
    {
        get
        {
            lock (_syncRoot)
            {
                return _isMasterMuted;
            }
        }
    }

    public IReadOnlyList<AppAudioModel> Refresh()
    {
        ThrowIfDisposed();

        // Phase 1: Collect live session data from COM *outside* the lock.
        var liveGroups = new Dictionary<int, SessionGroup>();
        bool hasPlaybackEndpoint;
        string playbackDeviceId;
        string playbackDeviceName;
        float masterVolume;
        bool masterMuted;
        bool hasPlayback;

        try
        {
            hasPlaybackEndpoint = CollectLiveGroups(liveGroups);
        }
        catch
        {
            hasPlaybackEndpoint = false;
        }

        // Snapshot device state read during CollectLiveGroups.
        lock (_syncRoot)
        {
            playbackDeviceId = _currentPlaybackDeviceId;
            playbackDeviceName = _currentDeviceName;
            masterVolume = _masterVolume;
            masterMuted = _isMasterMuted;
            hasPlayback = _hasPlaybackDevice;
        }

        // Phase 1.5: Refresh device inventory outside the lock (COM-heavy).
        RefreshDeviceInventory(DateTime.UtcNow);

        // Phase 2: Take the lock only to merge collected data into internal state.
        lock (_syncRoot)
        {
            var now = DateTime.UtcNow;
            var pendingDefaultSwitch = _defaultDeviceChanged;
            var previousPlaybackDeviceId = _currentPlaybackDeviceId;

            if (pendingDefaultSwitch)
            {
                if (hasPlaybackEndpoint)
                {
                    if (!string.Equals(previousPlaybackDeviceId, _currentPlaybackDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        _appsByProcessId.Clear();
                        _deviceRouteCache.Clear();
                        _volumeWriteTimestamps.Clear();
                        _muteWriteTimestamps.Clear();
                    }

                    _defaultDeviceChanged = false;
                }
                else if (now - _defaultDeviceChangeUtc >= DeviceSwitchGracePeriod)
                {
                    _appsByProcessId.Clear();
                    _deviceRouteCache.Clear();
                    _volumeWriteTimestamps.Clear();
                    _muteWriteTimestamps.Clear();
                    _defaultDeviceChanged = false;
                }
            }

            _visibleProcessIdsBuffer.Clear();
            foreach (var processId in liveGroups.Keys)
            {
                _visibleProcessIdsBuffer.Add(processId);
            }

            foreach (var group in liveGroups.Values)
            {
                var appKey = AppAudioModel.CreateIdentityKey(group.AppName);
                var isPinned = !string.IsNullOrWhiteSpace(appKey) && _pinnedAppKeys.Contains(appKey);
                if (!_appsByProcessId.TryGetValue(group.ProcessId, out var model))
                {
                    if (group.Peak <= AppAudioModel.SilenceThreshold && !isPinned)
                    {
                        continue;
                    }

                    model = new AppAudioModel
                    {
                        ProcessId = group.ProcessId,
                        AppName = group.AppName,
                        Icon = group.Icon,
                        Volume = group.AverageVolume,
                        IsMuted = group.IsMuted,
                        LastAudioTime = now,
                        IsPinned = isPinned,
                    };

                    _appsByProcessId[group.ProcessId] = model;
                }

                model.AppName = group.AppName;
                model.Icon = group.Icon;
                model.IsPinned = isPinned;

                // Skip overwriting volume/mute if user just wrote them recently.
                var skipVolumeOverwrite = _volumeWriteTimestamps.TryGetValue(group.ProcessId, out var volTs)
                    && now - volTs < VolumeWriteGracePeriod;
                var skipMuteOverwrite = _muteWriteTimestamps.TryGetValue(group.ProcessId, out var muteTs)
                    && now - muteTs < VolumeWriteGracePeriod;

                if (!skipVolumeOverwrite)
                {
                    model.Volume = group.AverageVolume;
                }

                model.Peak = group.Peak;

                if (!skipMuteOverwrite)
                {
                    model.IsMuted = group.IsMuted;
                }

                if (group.Peak > AppAudioModel.SilenceThreshold)
                {
                    model.LastAudioTime = now;
                }
            }

            RefreshDeviceRoutes(_visibleProcessIdsBuffer, now);

            foreach (var model in _appsByProcessId.Values)
            {
                if (!_visibleProcessIdsBuffer.Contains(model.ProcessId))
                {
                    model.Peak = 0.0f;
                }
            }

            _expiredProcessIdsBuffer.Clear();
            foreach (var model in _appsByProcessId.Values)
            {
                if (model.Peak > AppAudioModel.SilenceThreshold)
                {
                    continue;
                }

                if (now - model.LastAudioTime <= SilentRetention)
                {
                    continue;
                }

                if (model.IsMuted && _visibleProcessIdsBuffer.Contains(model.ProcessId))
                {
                    continue;
                }

                if (model.IsPinned && _visibleProcessIdsBuffer.Contains(model.ProcessId))
                {
                    continue;
                }

                _expiredProcessIdsBuffer.Add(model.ProcessId);
            }

            foreach (var processId in _expiredProcessIdsBuffer)
            {
                _appsByProcessId.Remove(processId);
                _deviceRouteCache.Remove(processId);
                _volumeWriteTimestamps.Remove(processId);
                _muteWriteTimestamps.Remove(processId);
            }

            var snapshot = new List<AppAudioModel>(_appsByProcessId.Count);
            foreach (var model in _appsByProcessId.Values)
            {
                snapshot.Add(model.Clone());
            }

            snapshot.Sort(AppAudioModelSortComparer.Instance);
            return snapshot;
        }
    }

    public void SetVolume(int processId, float volume)
    {
        ThrowIfDisposed();

        var clampedVolume = Math.Clamp(volume, 0.0f, 1.0f);

        ForMatchingSession(processId, session =>
        {
            using var simpleAudioVolume = session.SimpleAudioVolume;
            simpleAudioVolume.Volume = clampedVolume;
        });

        lock (_syncRoot)
        {
            _volumeWriteTimestamps[processId] = DateTime.UtcNow;
            if (_appsByProcessId.TryGetValue(processId, out var model))
            {
                model.Volume = clampedVolume;
                if (clampedVolume <= AppAudioModel.SilenceThreshold)
                {
                    model.Peak = 0.0f;
                }
            }
        }
    }

    public void SetMute(int processId, bool isMuted)
    {
        ThrowIfDisposed();

        ForMatchingSession(processId, session =>
        {
            using var simpleAudioVolume = session.SimpleAudioVolume;
            simpleAudioVolume.Mute = isMuted;
        });

        lock (_syncRoot)
        {
            _muteWriteTimestamps[processId] = DateTime.UtcNow;
            if (_appsByProcessId.TryGetValue(processId, out var model))
            {
                model.IsMuted = isMuted;
                if (isMuted)
                {
                    model.Peak = 0.0f;
                }
            }
        }
    }

    public void SetPreferredPlaybackDevice(int processId, string? deviceId)
    {
        SetPreferredDevice(processId, AudioDeviceFlow.Render, deviceId);
    }

    public void SetPreferredCaptureDevice(int processId, string? deviceId)
    {
        SetPreferredDevice(processId, AudioDeviceFlow.Capture, deviceId);
    }

    public void SetPinnedAppKeys(IEnumerable<string> appKeys)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(appKeys);

        lock (_syncRoot)
        {
            _pinnedAppKeys.Clear();
            foreach (var appKey in appKeys)
            {
                var normalized = AppAudioModel.CreateIdentityKey(appKey);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    _pinnedAppKeys.Add(normalized);
                }
            }

            foreach (var model in _appsByProcessId.Values)
            {
                model.IsPinned = !string.IsNullOrWhiteSpace(model.AppKey) && _pinnedAppKeys.Contains(model.AppKey);
            }
        }
    }

    public void SetAllMuted(bool isMuted)
    {
        ThrowIfDisposed();

        ForEachActiveSession((session, _) =>
        {
            using var simpleAudioVolume = session.SimpleAudioVolume;
            simpleAudioVolume.Mute = isMuted;
        });

        lock (_syncRoot)
        {
            foreach (var model in _appsByProcessId.Values)
            {
                model.IsMuted = isMuted;
                if (isMuted)
                {
                    model.Peak = 0.0f;
                }
            }
        }
    }

    public void SetMasterVolume(float volume)
    {
        ThrowIfDisposed();

        var clampedVolume = Math.Clamp(volume, 0.0f, 1.0f);
        MMDevice? device = null;

        try
        {
            device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = clampedVolume;

            lock (_syncRoot)
            {
                _hasPlaybackDevice = true;
                _currentPlaybackDeviceId = SafeRead(() => device.ID, string.Empty);
                _currentDeviceName = device.FriendlyName;
                _masterVolume = clampedVolume;
                _isMasterMuted = device.AudioEndpointVolume.Mute;
            }
        }
        catch
        {
            lock (_syncRoot)
            {
                _hasPlaybackDevice = false;
                _currentPlaybackDeviceId = string.Empty;
                _currentDeviceName = "No playback device";
                _masterVolume = 0.0f;
                _isMasterMuted = false;
            }
        }
        finally
        {
            device?.Dispose();
        }
    }

    public void SetMasterMute(bool isMuted)
    {
        ThrowIfDisposed();

        MMDevice? device = null;

        try
        {
            device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.Mute = isMuted;

            lock (_syncRoot)
            {
                _hasPlaybackDevice = true;
                _currentPlaybackDeviceId = SafeRead(() => device.ID, string.Empty);
                _currentDeviceName = device.FriendlyName;
                _masterVolume = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                _isMasterMuted = isMuted;
            }
        }
        catch
        {
            lock (_syncRoot)
            {
                _hasPlaybackDevice = false;
                _currentPlaybackDeviceId = string.Empty;
                _currentDeviceName = "No playback device";
                _masterVolume = 0.0f;
                _isMasterMuted = false;
            }
        }
        finally
        {
            device?.Dispose();
        }
    }

    public void SetDefaultCaptureMute(bool isMuted)
    {
        ThrowIfDisposed();

        MMDevice? device = null;

        try
        {
            device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            device.AudioEndpointVolume.Mute = isMuted;

            lock (_syncRoot)
            {
                _hasCaptureDevice = true;
                _currentCaptureDeviceId = SafeRead(() => device.ID, string.Empty);
                _currentCaptureDeviceName = device.FriendlyName;
                _isDefaultCaptureMuted = isMuted;
            }
        }
        catch
        {
            lock (_syncRoot)
            {
                _hasCaptureDevice = false;
                _currentCaptureDeviceId = string.Empty;
                _currentCaptureDeviceName = "No input device";
                _isDefaultCaptureMuted = false;
            }
        }
        finally
        {
            device?.Dispose();
        }
    }

    public void ToggleDefaultCaptureMute()
    {
        ThrowIfDisposed();

        MMDevice? device = null;

        try
        {
            device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            var nextMuted = !SafeRead(() => device.AudioEndpointVolume.Mute, false);
            device.AudioEndpointVolume.Mute = nextMuted;

            lock (_syncRoot)
            {
                _hasCaptureDevice = true;
                _currentCaptureDeviceId = SafeRead(() => device.ID, string.Empty);
                _currentCaptureDeviceName = device.FriendlyName;
                _isDefaultCaptureMuted = nextMuted;
            }
        }
        catch
        {
            lock (_syncRoot)
            {
                _hasCaptureDevice = false;
                _currentCaptureDeviceId = string.Empty;
                _currentCaptureDeviceName = "No input device";
                _isDefaultCaptureMuted = false;
            }
        }
        finally
        {
            device?.Dispose();
        }
    }

    /// <summary>
    /// Change the system-wide default audio endpoint for playback or capture.
    /// </summary>
    public bool SetSystemDefaultDevice(string deviceId, AudioDeviceFlow flow)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        try
        {
            var policyConfig = new SystemDefaultDeviceSwitcher();
            var success = policyConfig.SetDefaultEndpoint(deviceId, flow);
            if (success)
            {
                OnDefaultPlaybackDeviceChanged();
            }

            return success;
        }
        catch (Exception ex)
        {
            RouteLog($"SetSystemDefaultDevice failed: flow={flow} deviceId='{deviceId}' error={ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _deviceEnumerator.UnregisterEndpointNotificationCallback(_endpointNotificationClient);
        }
        catch
        {
            // Ignore unregister failures during shutdown.
        }

        _deviceEnumerator.Dispose();
    }

    private bool CollectLiveGroups(Dictionary<int, SessionGroup> groups)
    {
        MMDevice? defaultDevice = null;
        var hasPlaybackDevice = false;
        string defaultDeviceId = string.Empty;

        try
        {
            defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            hasPlaybackDevice = true;
            _hasPlaybackDevice = true;
            defaultDeviceId = SafeRead(() => defaultDevice.ID, string.Empty);
            _currentPlaybackDeviceId = defaultDeviceId;
            _currentDeviceName = defaultDevice.FriendlyName;
            _masterVolume = SafeRead(() => defaultDevice.AudioEndpointVolume.MasterVolumeLevelScalar, 0.0f);
            _isMasterMuted = SafeRead(() => defaultDevice.AudioEndpointVolume.Mute, false);

            CollectSessionsFromDevice(defaultDevice, groups, isDefaultDevice: true);
        }
        catch (Exception ex)
        {
            RouteLog($"CollectLiveGroups default device error: {ex.GetType().Name}: {ex.Message}");
            _hasPlaybackDevice = false;
            _currentPlaybackDeviceId = string.Empty;
            _currentDeviceName = "No playback device";
            _masterVolume = 0.0f;
            _isMasterMuted = false;
        }
        finally
        {
            defaultDevice?.Dispose();
        }

        // Also enumerate sessions on non-default render devices so that apps
        // routed to a different output remain visible in the mixer.
        try
        {
            var allDevices = _deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (MMDevice device in allDevices)
            {
                try
                {
                    var deviceId = SafeRead(() => device.ID, string.Empty);
                    if (string.Equals(deviceId, defaultDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    CollectSessionsFromDevice(device, groups, isDefaultDevice: false);
                }
                catch (Exception ex)
                {
                    RouteLog($"CollectLiveGroups non-default device error: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch
        {
            // Non-default device enumeration is best effort.
        }

        return hasPlaybackDevice;
    }

    private void CollectSessionsFromDevice(MMDevice device, Dictionary<int, SessionGroup> groups, bool isDefaultDevice)
    {
        var sessions = device.AudioSessionManager.Sessions;

        for (var index = 0; index < sessions.Count; index++)
        {
            AudioSessionControl? session = null;

            try
            {
                session = sessions[index];
                if (session is null)
                {
                    continue;
                }

                var processId = SafeGetProcessId(session);

                var audioMeter = session.AudioMeterInformation;
                using var simpleAudioVolume = session.SimpleAudioVolume;

                var peak = audioMeter.MasterPeakValue;
                var volume = simpleAudioVolume.Volume;
                var isMuted = simpleAudioVolume.Mute;

                // On non-default devices, SimpleAudioVolume may report 0 even
                // when the session is actively producing audio.  Use the raw
                // peak in that case so routed apps stay visible.
                var effectiveVolume = volume;
                if (!isDefaultDevice && volume <= AppAudioModel.SilenceThreshold && peak > AppAudioModel.SilenceThreshold)
                {
                    effectiveVolume = 1.0f;
                }

                var effectivePeak = isMuted || (isDefaultDevice && _isMasterMuted) || effectiveVolume <= AppAudioModel.SilenceThreshold
                    ? 0.0f
                    : Math.Clamp(peak * effectiveVolume, 0.0f, 1.0f);

                if (effectivePeak <= AppAudioModel.SilenceThreshold * 0.75f)
                {
                    effectivePeak = 0.0f;
                }

                if (!groups.TryGetValue(processId, out var group))
                {
                    var identity = ResolveAppIdentity(processId, SafeRead(() => session.DisplayName, string.Empty));
                    group = new SessionGroup(processId, identity.AppName, identity.Icon);
                    groups.Add(processId, group);
                }

                group.Peak = Math.Max(group.Peak, effectivePeak);
                group.VolumeSum += effectiveVolume;
                group.SessionCount++;
                group.AllMuted &= isMuted;
            }
            catch
            {
                // Sessions are volatile and can disappear between enumeration and access.
            }
            finally
            {
                session?.Dispose();
            }
        }
    }

    private void ForEachActiveSession(Action<AudioSessionControl, int> sessionAction)
    {
        ForEachActiveSessionOnDevice(DataFlow.Render, sessionAction);
    }

    /// <summary>
    /// Optimized variant: only invokes the action on sessions belonging to a specific processId.
    /// Avoids iterating every session on every device when only one process needs updating.
    /// </summary>
    private void ForMatchingSession(int targetProcessId, Action<AudioSessionControl> sessionAction)
    {
        if (targetProcessId <= 0)
        {
            return;
        }

        ForEachActiveSessionOnDevice(DataFlow.Render, (session, sessionProcessId) =>
        {
            if (sessionProcessId == targetProcessId)
            {
                sessionAction(session);
            }
        });
    }

    private void ForEachActiveSessionOnDevice(DataFlow dataFlow, Action<AudioSessionControl, int> sessionAction)
    {
        // Apply to sessions on the default device.
        MMDevice? defaultDevice = null;
        string defaultDeviceId = string.Empty;

        try
        {
            defaultDevice = _deviceEnumerator.GetDefaultAudioEndpoint(dataFlow, Role.Multimedia);
            defaultDeviceId = SafeRead(() => defaultDevice.ID, string.Empty);
            RunSessionAction(defaultDevice, sessionAction);
        }
        catch
        {
            // No default endpoint available.
        }
        finally
        {
            defaultDevice?.Dispose();
        }

        // Also apply to sessions on non-default devices for routed apps.
        try
        {
            var allDevices = _deviceEnumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);
            foreach (MMDevice device in allDevices)
            {
                try
                {
                    var deviceId = SafeRead(() => device.ID, string.Empty);
                    if (string.Equals(deviceId, defaultDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    RunSessionAction(device, sessionAction);
                }
                catch
                {
                    // Non-default device session access is best effort.
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch
        {
            // Non-default device enumeration is best effort.
        }
    }

    private static void RunSessionAction(MMDevice device, Action<AudioSessionControl, int> sessionAction)
    {
        var sessions = device.AudioSessionManager.Sessions;

        for (var index = 0; index < sessions.Count; index++)
        {
            AudioSessionControl? session = null;

            try
            {
                session = sessions[index];
                if (session is null)
                {
                    continue;
                }

                sessionAction(session, SafeGetProcessId(session));
            }
            catch
            {
                // Ignore sessions that disappear while mutating the device state.
            }
            finally
            {
                session?.Dispose();
            }
        }
    }

    private void SetPreferredDevice(int processId, AudioDeviceFlow flow, string? deviceId)
    {
        ThrowIfDisposed();

        var normalizedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? string.Empty : deviceId;
        List<int> targetProcessIds;

        lock (_syncRoot)
        {
            _routeTargetProcessIdsBuffer.Clear();
            if (processId > 0)
            {
                _routeTargetProcessIdsBuffer.Add(processId);
            }

            var appRouteKey = ResolveAppRouteKey(processId);
            if (appRouteKey.Length > 0)
            {
                if (!_routePreferencesByAppKey.TryGetValue(appRouteKey, out var preference))
                {
                    preference = new AppRoutePreference();
                    _routePreferencesByAppKey[appRouteKey] = preference;
                }

                if (flow == AudioDeviceFlow.Render)
                {
                    preference.HasRenderPreference = true;
                    preference.RenderDeviceId = normalizedDeviceId;
                }
                else
                {
                    preference.HasCapturePreference = true;
                    preference.CaptureDeviceId = normalizedDeviceId;
                }

                foreach (var model in _appsByProcessId.Values)
                {
                    if (string.Equals(NormalizeAppRouteKey(model.AppName), appRouteKey, StringComparison.OrdinalIgnoreCase))
                    {
                        _routeTargetProcessIdsBuffer.Add(model.ProcessId);
                    }
                }
            }

            if (_routeTargetProcessIdsBuffer.Count == 0 && processId > 0)
            {
                _routeTargetProcessIdsBuffer.Add(processId);
            }

            var now = DateTime.UtcNow;
            foreach (var targetProcessId in _routeTargetProcessIdsBuffer)
            {
                var route = GetOrCreateRouteCacheEntry(targetProcessId);
                ApplyUserPreferredDevice(route, targetProcessId, flow, normalizedDeviceId, now);
            }

            targetProcessIds = [.. _routeTargetProcessIdsBuffer];
        }

        foreach (var targetProcessId in targetProcessIds)
        {
            QueueRouteWrite(targetProcessId, flow, normalizedDeviceId);
        }
    }

    private void QueueRouteWrite(int processId, AudioDeviceFlow flow, string deviceId)
    {
        if (processId <= 0 || _disposed || !TryMarkRouteWriteAsQueued(processId, flow, deviceId))
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static state =>
            {
                state.Service.ExecuteQueuedRouteWrite(state.ProcessId, state.Flow, state.DeviceId);
            },
            new RouteWriteWorkItem(this, processId, flow, deviceId),
            preferLocal: false);
    }

    private bool TryMarkRouteWriteAsQueued(int processId, AudioDeviceFlow flow, string deviceId)
    {
        lock (_syncRoot)
        {
            if (!_deviceRouteCache.TryGetValue(processId, out var route))
            {
                return false;
            }

            if (flow == AudioDeviceFlow.Render)
            {
                if (!string.Equals(route.RenderDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (route.IsRenderWriteQueued)
                {
                    return false;
                }

                route.IsRenderWriteQueued = true;
                return true;
            }

            if (!string.Equals(route.CaptureDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (route.IsCaptureWriteQueued)
            {
                return false;
            }

            route.IsCaptureWriteQueued = true;
            return true;
        }
    }

    private void ClearRouteWriteQueuedFlag(int processId, AudioDeviceFlow flow, string deviceId)
    {
        lock (_syncRoot)
        {
            if (!_deviceRouteCache.TryGetValue(processId, out var route))
            {
                return;
            }

            if (flow == AudioDeviceFlow.Render)
            {
                if (string.Equals(route.RenderDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    route.IsRenderWriteQueued = false;
                }
            }
            else if (string.Equals(route.CaptureDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                route.IsCaptureWriteQueued = false;
            }
        }
    }

    private void QueueRouteWriteAfterDelay(int processId, AudioDeviceFlow flow, string deviceId, TimeSpan delay)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay).ConfigureAwait(false);
                    }
                }
                catch
                {
                    return;
                }

                QueueRouteWrite(processId, flow, deviceId);
            });
    }

    private void ExecuteQueuedRouteWrite(int processId, AudioDeviceFlow flow, string deviceId)
    {
        try
        {
            if (_disposed || processId <= 0)
            {
                return;
            }

            RouteLog($"ExecuteQueuedRouteWrite: pid={processId} flow={flow} deviceId='{deviceId}'");
            var now = DateTime.UtcNow;
            var skipWriteForCooldown = false;
            var cooldownRetryDelay = TimeSpan.Zero;

            lock (_syncRoot)
            {
                if (!_deviceRouteCache.TryGetValue(processId, out var route))
                {
                    return;
                }

                if (flow == AudioDeviceFlow.Render)
                {
                    if (!string.Equals(route.RenderDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (route.LastRenderWriteAttemptUtc != DateTime.MinValue)
                    {
                        var elapsed = now - route.LastRenderWriteAttemptUtc;
                        if (elapsed < RouteWriteMinInterval)
                        {
                            skipWriteForCooldown = true;
                            cooldownRetryDelay = RouteWriteMinInterval - elapsed;
                        }
                    }
                }
                else
                {
                    if (!string.Equals(route.CaptureDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (route.LastCaptureWriteAttemptUtc != DateTime.MinValue)
                    {
                        var elapsed = now - route.LastCaptureWriteAttemptUtc;
                        if (elapsed < RouteWriteMinInterval)
                        {
                            skipWriteForCooldown = true;
                            cooldownRetryDelay = RouteWriteMinInterval - elapsed;
                        }
                    }
                }
            }

            if (skipWriteForCooldown)
            {
                RouteLog($"ExecuteQueuedRouteWrite skipped: pid={processId} flow={flow} reason=cooldown");
                QueueRouteWriteAfterDelay(processId, flow, deviceId, cooldownRetryDelay);
                return;
            }

            var writeSucceeded = TrySetPersistedDefaultAudioEndpoint(processId, flow, deviceId);
            RouteLog($"ExecuteQueuedRouteWrite result: pid={processId} succeeded={writeSucceeded}");
            now = DateTime.UtcNow;

            lock (_syncRoot)
            {
                if (!_deviceRouteCache.TryGetValue(processId, out var route))
                {
                    return;
                }

                if (flow == AudioDeviceFlow.Render)
                {
                    if (!string.Equals(route.RenderDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    route.LastRenderWriteAttemptUtc = now;
                    route.LastUpdatedUtc = now;

                    if (writeSucceeded)
                    {
                        route.IsRenderWritePending = false;
                        route.RenderWriteFailureCount = 0;
                    }
                    else
                    {
                        route.RenderWriteFailureCount = Math.Min(route.RenderWriteFailureCount + 1, MaxPendingRouteWriteRetries);
                        route.IsRenderWritePending = route.RenderWriteFailureCount < MaxPendingRouteWriteRetries;
                    }
                }
                else
                {
                    if (!string.Equals(route.CaptureDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    route.LastCaptureWriteAttemptUtc = now;
                    route.LastUpdatedUtc = now;

                    if (writeSucceeded)
                    {
                        route.IsCaptureWritePending = false;
                        route.CaptureWriteFailureCount = 0;
                    }
                    else
                    {
                        route.CaptureWriteFailureCount = Math.Min(route.CaptureWriteFailureCount + 1, MaxPendingRouteWriteRetries);
                        route.IsCaptureWritePending = route.CaptureWriteFailureCount < MaxPendingRouteWriteRetries;
                    }
                }
            }
        }
        finally
        {
            ClearRouteWriteQueuedFlag(processId, flow, deviceId);
        }
    }

    private void ApplyUserPreferredDevice(
        DeviceRouteCacheEntry route,
        int processId,
        AudioDeviceFlow flow,
        string deviceId,
        DateTime now)
    {
        route.LastUpdatedUtc = now;

        if (flow == AudioDeviceFlow.Render)
        {
            route.RenderDeviceId = deviceId;
            route.LastRenderUserSetUtc = now;
            route.LastRenderWriteAttemptUtc = DateTime.MinValue;
            route.RenderWriteFailureCount = 0;
            route.IsRenderWritePending = true;
            route.IsRenderWriteQueued = false;

            if (_appsByProcessId.TryGetValue(processId, out var model))
            {
                model.PreferredRenderDeviceId = deviceId;
            }
        }
        else
        {
            route.CaptureDeviceId = deviceId;
            route.LastCaptureUserSetUtc = now;
            route.LastCaptureWriteAttemptUtc = DateTime.MinValue;
            route.CaptureWriteFailureCount = 0;
            route.IsCaptureWritePending = true;
            route.IsCaptureWriteQueued = false;

            if (_appsByProcessId.TryGetValue(processId, out var model))
            {
                model.PreferredCaptureDeviceId = deviceId;
            }
        }
    }

    private void ApplyStoredAppRoutePreferences(int processId, DeviceRouteCacheEntry route, DateTime now)
    {
        var appRouteKey = ResolveAppRouteKey(processId);
        if (appRouteKey.Length == 0 || !_routePreferencesByAppKey.TryGetValue(appRouteKey, out var preference))
        {
            return;
        }

        if (preference.HasRenderPreference
            && !string.Equals(route.RenderDeviceId, preference.RenderDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            route.RenderDeviceId = preference.RenderDeviceId;
            route.LastRenderUserSetUtc = now;
            route.LastRenderWriteAttemptUtc = DateTime.MinValue;
            route.RenderWriteFailureCount = 0;
            route.IsRenderWritePending = true;
            route.IsRenderWriteQueued = false;
        }

        if (preference.HasCapturePreference
            && !string.Equals(route.CaptureDeviceId, preference.CaptureDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            route.CaptureDeviceId = preference.CaptureDeviceId;
            route.LastCaptureUserSetUtc = now;
            route.LastCaptureWriteAttemptUtc = DateTime.MinValue;
            route.CaptureWriteFailureCount = 0;
            route.IsCaptureWritePending = true;
            route.IsCaptureWriteQueued = false;
        }
    }

    private string ResolveAppRouteKey(int processId)
    {
        if (processId <= 0 || !_appsByProcessId.TryGetValue(processId, out var model))
        {
            return string.Empty;
        }

        return AppAudioModel.CreateIdentityKey(model.AppName);
    }

    private static string NormalizeAppRouteKey(string? appName)
    {
        return AppAudioModel.CreateIdentityKey(appName);
    }

    private static TimeSpan GetRouteWriteRetryDelay(int failureCount)
    {
        var exponent = Math.Clamp(failureCount - 1, 0, 3);
        var multiplier = 1 << exponent;
        return TimeSpan.FromMilliseconds(PendingRouteWriteRetryInterval.TotalMilliseconds * multiplier);
    }

    private DeviceRouteCacheEntry GetOrCreateRouteCacheEntry(int processId)
    {
        if (_deviceRouteCache.TryGetValue(processId, out var route))
        {
            return route;
        }

        route = new DeviceRouteCacheEntry();
        _deviceRouteCache[processId] = route;
        return route;
    }

    private bool TrySetPersistedDefaultAudioEndpoint(int processId, AudioDeviceFlow flow, string? deviceId)
    {
        lock (_audioPolicyConfigSyncRoot)
        {
            return _audioPolicyConfigBridge.TrySetPersistedDefaultAudioEndpoint(processId, flow, deviceId);
        }
    }

    private bool TryGetPersistedDefaultAudioEndpoint(int processId, AudioDeviceFlow flow, out string deviceId)
    {
        lock (_audioPolicyConfigSyncRoot)
        {
            return _audioPolicyConfigBridge.TryGetPersistedDefaultAudioEndpoint(processId, flow, out deviceId);
        }
    }

    private void RefreshDeviceInventory(DateTime now)
    {
        if (!_deviceInventoryChanged && now - _lastDeviceInventoryRefreshUtc < DeviceInventoryRefreshInterval)
        {
            return;
        }

        _lastDeviceInventoryRefreshUtc = now;
        _deviceInventoryChanged = false;

        MMDevice? defaultPlaybackDevice = null;
        MMDevice? defaultCaptureDevice = null;

        try
        {
            defaultPlaybackDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _currentPlaybackDeviceId = SafeRead(() => defaultPlaybackDevice.ID, string.Empty);
            _currentDeviceName = SafeRead(() => defaultPlaybackDevice.FriendlyName, "No playback device");
            _hasPlaybackDevice = true;
        }
        catch
        {
            _currentPlaybackDeviceId = string.Empty;
            _currentDeviceName = "No playback device";
            _hasPlaybackDevice = false;
        }

        try
        {
            defaultCaptureDevice = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            _currentCaptureDeviceId = SafeRead(() => defaultCaptureDevice.ID, string.Empty);
            _currentCaptureDeviceName = SafeRead(() => defaultCaptureDevice.FriendlyName, "No input device");
            _hasCaptureDevice = true;
            _isDefaultCaptureMuted = SafeRead(() => defaultCaptureDevice.AudioEndpointVolume.Mute, false);
        }
        catch
        {
            _currentCaptureDeviceId = string.Empty;
            _currentCaptureDeviceName = "No input device";
            _hasCaptureDevice = false;
            _isDefaultCaptureMuted = false;
        }
        finally
        {
            defaultPlaybackDevice?.Dispose();
            defaultCaptureDevice?.Dispose();
        }

        _renderDeviceOptions = CreateDeviceOptions(
            DataFlow.Render,
            _currentDeviceName,
            _currentPlaybackDeviceId,
            AudioDeviceFlow.Render);

        _captureDeviceOptions = CreateDeviceOptions(
            DataFlow.Capture,
            _currentCaptureDeviceName,
            _currentCaptureDeviceId,
            AudioDeviceFlow.Capture);
    }

    private void RefreshDeviceRoutes(HashSet<int> visibleProcessIds, DateTime now)
    {
        foreach (var processId in visibleProcessIds)
        {
            var route = GetOrCreateRouteCacheEntry(processId);
            ApplyStoredAppRoutePreferences(processId, route, now);

            var shouldRetryRenderWrite = route.IsRenderWritePending
                && route.RenderWriteFailureCount < MaxPendingRouteWriteRetries
                && now - route.LastRenderWriteAttemptUtc >= GetRouteWriteRetryDelay(route.RenderWriteFailureCount);
            if (shouldRetryRenderWrite)
            {
                QueueRouteWrite(processId, AudioDeviceFlow.Render, route.RenderDeviceId);
            }

            var shouldRetryCaptureWrite = route.IsCaptureWritePending
                && route.CaptureWriteFailureCount < MaxPendingRouteWriteRetries
                && now - route.LastCaptureWriteAttemptUtc >= GetRouteWriteRetryDelay(route.CaptureWriteFailureCount);
            if (shouldRetryCaptureWrite)
            {
                QueueRouteWrite(processId, AudioDeviceFlow.Capture, route.CaptureDeviceId);
            }

            var shouldRefreshReadback = !route.IsRenderWritePending
                && !route.IsCaptureWritePending
                && now - route.LastUpdatedUtc >= DeviceRouteRefreshInterval;
            if (shouldRefreshReadback)
            {
                var pid = processId;
                var rt = route;
                var capturedPid = pid;
                var capturedRoute = rt;
                _ = Task.Run(() =>
                {
                    ReadDeviceRouteCacheEntry(capturedPid, capturedRoute, DateTime.UtcNow);
                    lock (_syncRoot)
                    {
                        if (_appsByProcessId.TryGetValue(capturedPid, out var m))
                        {
                            m.PreferredRenderDeviceId = capturedRoute.RenderDeviceId;
                            m.PreferredCaptureDeviceId = capturedRoute.CaptureDeviceId;
                        }
                    }
                });
            }

            if (_appsByProcessId.TryGetValue(processId, out var model))
            {
                model.PreferredRenderDeviceId = route.RenderDeviceId;
                model.PreferredCaptureDeviceId = route.CaptureDeviceId;
            }
        }
    }

    private void ReadDeviceRouteCacheEntry(int processId, DeviceRouteCacheEntry route, DateTime now)
    {
        // Skip readback entirely while any write is still in-flight or pending retry.
        // This prevents the readback from overwriting a user-initiated route change
        // before the write has had a chance to propagate through the OS.
        if (route.IsRenderWritePending || route.IsRenderWriteQueued
            || route.IsCaptureWritePending || route.IsCaptureWriteQueued)
        {
            return;
        }

        if (TryGetPersistedDefaultAudioEndpoint(processId, AudioDeviceFlow.Render, out var renderDeviceId))
        {
            var suppressEmptyReadback = string.IsNullOrEmpty(renderDeviceId)
                && !string.IsNullOrEmpty(route.RenderDeviceId)
                && now - route.LastRenderUserSetUtc < RouteReadbackGracePeriod;

            if (!suppressEmptyReadback)
            {
                route.RenderDeviceId = renderDeviceId;
                route.IsRenderWritePending = false;
                route.IsRenderWriteQueued = false;
                route.RenderWriteFailureCount = 0;
            }
        }

        if (TryGetPersistedDefaultAudioEndpoint(processId, AudioDeviceFlow.Capture, out var captureDeviceId))
        {
            var suppressEmptyReadback = string.IsNullOrEmpty(captureDeviceId)
                && !string.IsNullOrEmpty(route.CaptureDeviceId)
                && now - route.LastCaptureUserSetUtc < RouteReadbackGracePeriod;

            if (!suppressEmptyReadback)
            {
                route.CaptureDeviceId = captureDeviceId;
                route.IsCaptureWritePending = false;
                route.IsCaptureWriteQueued = false;
                route.CaptureWriteFailureCount = 0;
            }
        }

        route.LastUpdatedUtc = now;
    }

    private IReadOnlyList<AudioDeviceOptionModel> CreateDeviceOptions(
        DataFlow dataFlow,
        string defaultDeviceName,
        string defaultDeviceId,
        AudioDeviceFlow flow)
    {
        var options = new List<AudioDeviceOptionModel>
        {
            new(
                id: string.Empty,
                displayName: $"System default - {defaultDeviceName}",
                flow: flow,
                isSystemDefault: true),
        };

        MMDeviceCollection? devices = null;

        try
        {
            devices = _deviceEnumerator.EnumerateAudioEndPoints(dataFlow, DeviceState.Active);
            var activeDevices = new List<(string Id, string Name, bool IsDefault)>();

            foreach (MMDevice device in devices)
            {
                try
                {
                    activeDevices.Add((
                        SafeRead(() => device.ID, string.Empty),
                        SafeRead(() => device.FriendlyName, "Unknown device"),
                        string.Equals(SafeRead(() => device.ID, string.Empty), defaultDeviceId, StringComparison.OrdinalIgnoreCase)));
                }
                finally
                {
                    device.Dispose();
                }
            }

            foreach (var device in activeDevices
                         .OrderByDescending(item => item.IsDefault)
                         .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                options.Add(new AudioDeviceOptionModel(device.Id, device.Name, flow));
            }
        }
        catch
        {
            // Device enumeration is best effort; keep the system default option.
        }

        return options;
    }

    private (string AppName, ImageSource Icon) ResolveAppIdentity(int processId, string? displayName)
    {
        if (processId <= 0)
        {
            return ("System Sounds", _defaultIcon);
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var executablePath = SafeRead(() => process.MainModule?.FileName, null);
            var processName = SafeRead(() => process.ProcessName, string.Empty);
            var fileDescription = string.IsNullOrWhiteSpace(executablePath)
                ? string.Empty
                : SafeRead(() => FileVersionInfo.GetVersionInfo(executablePath).FileDescription, string.Empty);

            var appName = FirstNonEmpty(
                fileDescription,
                displayName,
                processName,
                string.IsNullOrWhiteSpace(executablePath) ? string.Empty : Path.GetFileNameWithoutExtension(executablePath),
                $"Process {processId}");

            var icon = ResolveIcon(executablePath);
            return (appName, icon);
        }
        catch
        {
            return (FirstNonEmpty(displayName, $"Process {processId}"), _defaultIcon);
        }
    }

    private ImageSource ResolveIcon(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            return _defaultIcon;
        }

        if (_iconCache.TryGetValue(executablePath, out var cachedIcon))
        {
            return cachedIcon;
        }

        if (_iconCache.Count >= MaxIconCacheEntries)
        {
            _iconCache.Clear();
        }

        var icon = TryExtractIcon(executablePath) ?? _defaultIcon;
        _iconCache[executablePath] = icon;
        return icon;
    }

    private static int SafeGetProcessId(AudioSessionControl session)
    {
        return SafeRead(() => (int)session.GetProcessID, 0);
    }

    private static T SafeRead<T>(Func<T> reader, T fallback)
    {
        try
        {
            return reader();
        }
        catch
        {
            return fallback;
        }
    }

    private static ImageSource? TryExtractIcon(string executablePath)
    {
        try
        {
            using var icon = DrawingIcon.ExtractAssociatedIcon(executablePath);
            return icon is null ? null : CreateImageSource(icon);
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource CreateDefaultIcon()
    {
        using var icon = (DrawingIcon)DrawingSystemIcons.Application.Clone();
        return CreateImageSource(icon);
    }

    private static ImageSource CreateImageSource(DrawingIcon icon)
    {
        var imageSource = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(32, 32));

        imageSource.Freeze();
        return imageSource;
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return "Unknown";
    }

    private static int GetMixerPriority(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            return 3;
        }

        if (ContainsToken(appName, "discord"))
        {
            return 0;
        }

        if (ContainsToken(appName, "spotify"))
        {
            return 1;
        }

        if (ContainsToken(appName, "brave") || ContainsToken(appName, "chrome"))
        {
            return 2;
        }

        return 3;
    }

    private static bool ContainsToken(string source, string token)
    {
        return source.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private void OnDefaultPlaybackDeviceChanged()
    {
        lock (_syncRoot)
        {
            _defaultDeviceChanged = true;
            _defaultDeviceChangeUtc = DateTime.UtcNow;
            _deviceInventoryChanged = true;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void RouteLog(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            var logPath = Path.Combine(AppContext.BaseDirectory, "audiobit-route.log");
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch
        {
            // Don't let logging failures break routing.
        }
    }

    private sealed class SessionGroup
    {
        public SessionGroup(int processId, string appName, ImageSource icon)
        {
            ProcessId = processId;
            AppName = appName;
            Icon = icon;
        }

        public int ProcessId { get; }

        public string AppName { get; }

        public ImageSource Icon { get; }

        public float Peak { get; set; }

        public float VolumeSum { get; set; }

        public int SessionCount { get; set; }

        public bool AllMuted { get; set; } = true;

        public bool IsMuted => SessionCount > 0 && AllMuted;

        public float AverageVolume => SessionCount == 0 ? 1.0f : VolumeSum / SessionCount;
    }

    private sealed class DeviceRouteCacheEntry
    {
        public string RenderDeviceId { get; set; } = string.Empty;

        public string CaptureDeviceId { get; set; } = string.Empty;

        public bool IsRenderWritePending { get; set; }

        public bool IsCaptureWritePending { get; set; }

        public bool IsRenderWriteQueued { get; set; }

        public bool IsCaptureWriteQueued { get; set; }

        public DateTime LastRenderWriteAttemptUtc { get; set; } = DateTime.MinValue;

        public DateTime LastCaptureWriteAttemptUtc { get; set; } = DateTime.MinValue;

        public int RenderWriteFailureCount { get; set; }

        public int CaptureWriteFailureCount { get; set; }

        public DateTime LastRenderUserSetUtc { get; set; } = DateTime.MinValue;

        public DateTime LastCaptureUserSetUtc { get; set; } = DateTime.MinValue;

        public DateTime LastUpdatedUtc { get; set; }
    }

    private sealed class AppRoutePreference
    {
        public bool HasRenderPreference { get; set; }

        public string RenderDeviceId { get; set; } = string.Empty;

        public bool HasCapturePreference { get; set; }

        public string CaptureDeviceId { get; set; } = string.Empty;
    }

    private readonly record struct RouteWriteWorkItem(
        AudioSessionService Service,
        int ProcessId,
        AudioDeviceFlow Flow,
        string DeviceId);

    private sealed class EndpointNotificationClient : IMMNotificationClient
    {
        private readonly Action _onDeviceInventoryChanged;
        private readonly Action _onDefaultPlaybackDeviceChanged;

        public EndpointNotificationClient(Action onDeviceInventoryChanged, Action onDefaultPlaybackDeviceChanged)
        {
            _onDeviceInventoryChanged = onDeviceInventoryChanged;
            _onDefaultPlaybackDeviceChanged = onDefaultPlaybackDeviceChanged;
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            _onDeviceInventoryChanged();
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
            _onDeviceInventoryChanged();
        }

        public void OnDeviceRemoved(string deviceId)
        {
            _onDeviceInventoryChanged();
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            _onDeviceInventoryChanged();

            if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                _onDefaultPlaybackDeviceChanged();
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            _onDeviceInventoryChanged();
        }
    }

    private sealed class AppAudioModelSortComparer : IComparer<AppAudioModel>
    {
        public static readonly AppAudioModelSortComparer Instance = new();

        public int Compare(AppAudioModel? x, AppAudioModel? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var priorityComparison = GetMixerPriority(x.AppName).CompareTo(GetMixerPriority(y.AppName));
            if (priorityComparison != 0)
            {
                return priorityComparison;
            }

            var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(x.AppName, y.AppName);
            if (nameComparison != 0)
            {
                return nameComparison;
            }

            return x.ProcessId.CompareTo(y.ProcessId);
        }
    }
}
