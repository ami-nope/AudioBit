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

    private readonly object _syncRoot = new();
    private readonly MMDeviceEnumerator _deviceEnumerator;
    private readonly EndpointNotificationClient _endpointNotificationClient;
    private readonly AudioPolicyConfigBridge _audioPolicyConfigBridge;
    private readonly Dictionary<int, AppAudioModel> _appsByProcessId = new();
    private readonly Dictionary<int, DeviceRouteCacheEntry> _deviceRouteCache = new();
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImageSource _defaultIcon;

    private bool _disposed;
    private bool _defaultDeviceChanged = true;
    private bool _deviceInventoryChanged = true;
    private DateTime _lastDeviceInventoryRefreshUtc = DateTime.MinValue;
    private string _currentPlaybackDeviceId = string.Empty;
    private string _currentDeviceName = "No playback device";
    private string _currentCaptureDeviceId = string.Empty;
    private string _currentCaptureDeviceName = "No input device";
    private bool _hasPlaybackDevice;
    private bool _hasCaptureDevice;
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
            onDefaultPlaybackDeviceChanged: () => _defaultDeviceChanged = true);

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

        lock (_syncRoot)
        {
            var now = DateTime.UtcNow;

            if (_defaultDeviceChanged)
            {
                _appsByProcessId.Clear();
            }

            var liveGroups = CollectLiveGroups();
            var visibleProcessIds = new HashSet<int>(liveGroups.Keys);

            RefreshDeviceInventory(now);

            foreach (var group in liveGroups.Values)
            {
                if (!_appsByProcessId.TryGetValue(group.ProcessId, out var model))
                {
                    if (group.Peak <= AppAudioModel.SilenceThreshold)
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
                    };

                    _appsByProcessId[group.ProcessId] = model;
                }

                model.AppName = group.AppName;
                model.Icon = group.Icon;
                model.Volume = group.AverageVolume;
                model.Peak = group.Peak;
                model.IsMuted = group.IsMuted;

                if (group.Peak > AppAudioModel.SilenceThreshold)
                {
                    model.LastAudioTime = now;
                }
            }

            RefreshDeviceRoutes(visibleProcessIds, now);

            foreach (var model in _appsByProcessId.Values)
            {
                if (!visibleProcessIds.Contains(model.ProcessId))
                {
                    model.Peak = 0.0f;
                }
            }

            var expiredIds = _appsByProcessId.Values
                .Where(model => model.Peak <= AppAudioModel.SilenceThreshold)
                .Where(model => now - model.LastAudioTime > SilentRetention)
                .Where(model => !model.IsMuted || !visibleProcessIds.Contains(model.ProcessId))
                .Select(model => model.ProcessId)
                .ToArray();

            foreach (var processId in expiredIds)
            {
                _appsByProcessId.Remove(processId);
                _deviceRouteCache.Remove(processId);
            }

            return _appsByProcessId.Values
                .OrderBy(model => GetMixerPriority(model.AppName))
                .ThenBy(model => model.AppName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(model => model.ProcessId)
                .Select(model => model.Clone())
                .ToList();
        }
    }

    public void SetVolume(int processId, float volume)
    {
        ThrowIfDisposed();

        var clampedVolume = Math.Clamp(volume, 0.0f, 1.0f);

        ForEachActiveSession((session, sessionProcessId) =>
        {
            if (sessionProcessId != processId)
            {
                return;
            }

            using var simpleAudioVolume = session.SimpleAudioVolume;
            simpleAudioVolume.Volume = clampedVolume;
        });

        lock (_syncRoot)
        {
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

        ForEachActiveSession((session, sessionProcessId) =>
        {
            if (sessionProcessId != processId)
            {
                return;
            }

            using var simpleAudioVolume = session.SimpleAudioVolume;
            simpleAudioVolume.Mute = isMuted;
        });

        lock (_syncRoot)
        {
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
            }
        }
        catch
        {
            lock (_syncRoot)
            {
                _hasCaptureDevice = false;
                _currentCaptureDeviceId = string.Empty;
                _currentCaptureDeviceName = "No input device";
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
            }
        }
        catch
        {
            lock (_syncRoot)
            {
                _hasCaptureDevice = false;
                _currentCaptureDeviceId = string.Empty;
                _currentCaptureDeviceName = "No input device";
            }
        }
        finally
        {
            device?.Dispose();
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

    private Dictionary<int, SessionGroup> CollectLiveGroups()
    {
        var groups = new Dictionary<int, SessionGroup>();
        MMDevice? device = null;

        try
        {
            device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _hasPlaybackDevice = true;
            _currentPlaybackDeviceId = SafeRead(() => device.ID, string.Empty);
            _currentDeviceName = device.FriendlyName;
            _masterVolume = SafeRead(() => device.AudioEndpointVolume.MasterVolumeLevelScalar, 0.0f);
            _isMasterMuted = SafeRead(() => device.AudioEndpointVolume.Mute, false);
            _defaultDeviceChanged = false;

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
                    var effectivePeak = isMuted || _isMasterMuted || volume <= AppAudioModel.SilenceThreshold
                        ? 0.0f
                        : Math.Clamp(peak * volume, 0.0f, 1.0f);

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
                    group.VolumeSum += volume;
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
        catch
        {
            _hasPlaybackDevice = false;
            _currentPlaybackDeviceId = string.Empty;
            _currentDeviceName = "No playback device";
            _masterVolume = 0.0f;
            _isMasterMuted = false;
        }
        finally
        {
            device?.Dispose();
        }

        return groups;
    }

    private void ForEachActiveSession(Action<AudioSessionControl, int> sessionAction)
    {
        MMDevice? device = null;

        try
        {
            device = _deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
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
        catch
        {
            // No default endpoint available.
        }
        finally
        {
            device?.Dispose();
        }
    }

    private void SetPreferredDevice(int processId, AudioDeviceFlow flow, string? deviceId)
    {
        ThrowIfDisposed();

        var normalizedDeviceId = string.IsNullOrWhiteSpace(deviceId) ? string.Empty : deviceId;
        if (!_audioPolicyConfigBridge.TrySetPersistedDefaultAudioEndpoint(processId, flow, normalizedDeviceId))
        {
            return;
        }

        lock (_syncRoot)
        {
            if (!_deviceRouteCache.TryGetValue(processId, out var route))
            {
                route = new DeviceRouteCacheEntry();
                _deviceRouteCache[processId] = route;
            }

            route.LastUpdatedUtc = DateTime.UtcNow;

            if (flow == AudioDeviceFlow.Render)
            {
                route.RenderDeviceId = normalizedDeviceId;

                if (_appsByProcessId.TryGetValue(processId, out var model))
                {
                    model.PreferredRenderDeviceId = normalizedDeviceId;
                }
            }
            else
            {
                route.CaptureDeviceId = normalizedDeviceId;

                if (_appsByProcessId.TryGetValue(processId, out var model))
                {
                    model.PreferredCaptureDeviceId = normalizedDeviceId;
                }
            }
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
        }
        catch
        {
            _currentCaptureDeviceId = string.Empty;
            _currentCaptureDeviceName = "No input device";
            _hasCaptureDevice = false;
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
            if (!_deviceRouteCache.TryGetValue(processId, out var route)
                || now - route.LastUpdatedUtc >= DeviceRouteRefreshInterval)
            {
                route = ReadDeviceRouteCacheEntry(processId, now);
                _deviceRouteCache[processId] = route;
            }

            if (_appsByProcessId.TryGetValue(processId, out var model))
            {
                model.PreferredRenderDeviceId = route.RenderDeviceId;
                model.PreferredCaptureDeviceId = route.CaptureDeviceId;
            }
        }
    }

    private DeviceRouteCacheEntry ReadDeviceRouteCacheEntry(int processId, DateTime now)
    {
        var route = new DeviceRouteCacheEntry
        {
            LastUpdatedUtc = now,
        };

        if (_audioPolicyConfigBridge.TryGetPersistedDefaultAudioEndpoint(processId, AudioDeviceFlow.Render, out var renderDeviceId))
        {
            route.RenderDeviceId = renderDeviceId;
        }

        if (_audioPolicyConfigBridge.TryGetPersistedDefaultAudioEndpoint(processId, AudioDeviceFlow.Capture, out var captureDeviceId))
        {
            route.CaptureDeviceId = captureDeviceId;
        }

        return route;
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

        public DateTime LastUpdatedUtc { get; set; }
    }

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
}
