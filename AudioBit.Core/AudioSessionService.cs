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
    private static readonly TimeSpan SilentRetention = TimeSpan.FromMinutes(2);

    private readonly object _syncRoot = new();
    private readonly MMDeviceEnumerator _deviceEnumerator;
    private readonly EndpointNotificationClient _endpointNotificationClient;
    private readonly Dictionary<int, AppAudioModel> _appsByProcessId = new();
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImageSource _defaultIcon;

    private bool _disposed;
    private bool _defaultDeviceChanged = true;
    private string _currentDeviceName = "No playback device";
    private bool _hasPlaybackDevice;
    private float _masterVolume;
    private bool _isMasterMuted;

    public AudioSessionService()
    {
        _defaultIcon = CreateDefaultIcon();
        _deviceEnumerator = new MMDeviceEnumerator();
        _endpointNotificationClient = new EndpointNotificationClient(() => _defaultDeviceChanged = true);

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
                .Select(model => model.ProcessId)
                .ToArray();

            foreach (var processId in expiredIds)
            {
                _appsByProcessId.Remove(processId);
            }

            return _appsByProcessId.Values
                .OrderByDescending(model => model.IsActive)
                .ThenByDescending(model => model.Peak)
                .ThenByDescending(model => model.LastAudioTime)
                .ThenBy(model => model.AppName, StringComparer.OrdinalIgnoreCase)
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

                    if (!groups.TryGetValue(processId, out var group))
                    {
                        var identity = ResolveAppIdentity(processId, SafeRead(() => session.DisplayName, string.Empty));
                        group = new SessionGroup(processId, identity.AppName, identity.Icon);
                        groups.Add(processId, group);
                    }

                    group.Peak = Math.Max(group.Peak, peak);
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

    private sealed class EndpointNotificationClient : IMMNotificationClient
    {
        private readonly Action _onDeviceChanged;

        public EndpointNotificationClient(Action onDeviceChanged)
        {
            _onDeviceChanged = onDeviceChanged;
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            _onDeviceChanged();
        }

        public void OnDeviceAdded(string pwstrDeviceId)
        {
            _onDeviceChanged();
        }

        public void OnDeviceRemoved(string deviceId)
        {
            _onDeviceChanged();
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia)
            {
                _onDeviceChanged();
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            _onDeviceChanged();
        }
    }
}
