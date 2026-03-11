using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace AudioBit.Core;

internal sealed class AudioPolicyConfigBridge
{
    private const string PolicyConfigRuntimeClass = "Windows.Media.Internal.AudioPolicyConfig";
    private const string MmDeviceIdPrefix = @"\\?\SWD#MMDEVAPI#";
    private const string RenderDeviceInterfaceClass = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string CaptureDeviceInterfaceClass = "{2eef81be-33fa-4800-9670-1cd474972c3f}";

    private static readonly Guid[] SupportedFactoryIids =
    [
        new("ab3d4648-e242-459f-b02f-541c70306324"),
        new("2a59116d-6c4f-45e0-a74f-707e3fef9258"),
    ];

    private static readonly PolicyConfigRole[] PersistedEndpointRoles =
    [
        PolicyConfigRole.Console,
        PolicyConfigRole.Multimedia,
        PolicyConfigRole.Communications,
    ];

    private static readonly object FactoryLock = new();
    private static IAudioPolicyConfigFactory? _cachedFactory;
    private static bool _factoryInitialized;

    public bool TryGetPersistedDefaultAudioEndpoint(int processId, AudioDeviceFlow flow, out string deviceId)
    {
        deviceId = string.Empty;

        if (processId <= 0)
        {
            return false;
        }

        var factory = GetOrCreateFactory();
        if (factory is null)
        {
            return false;
        }

        try
        {
            var dataFlow = flow == AudioDeviceFlow.Render ? PolicyConfigDataFlow.Render : PolicyConfigDataFlow.Capture;
            foreach (var role in PersistedEndpointRoles)
            {
                var result = factory.GetPersistedDefaultAudioEndpoint(
                    processId,
                    dataFlow,
                    role,
                    out var persistedDeviceId);

                if (result != 0)
                {
                    continue;
                }

                var normalizedDeviceId = NormalizePersistedDeviceId(persistedDeviceId, flow);
                if (!string.IsNullOrWhiteSpace(normalizedDeviceId))
                {
                    deviceId = normalizedDeviceId;
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Log($"GET exception: pid={processId} flow={flow} error={ex.Message}");
            InvalidateFactory();
            return false;
        }
    }

    public bool TrySetPersistedDefaultAudioEndpoint(int processId, AudioDeviceFlow flow, string? deviceId)
    {
        if (processId <= 0)
        {
            Log($"SET skipped: invalid processId={processId}");
            return false;
        }

        var factory = GetOrCreateFactory();
        if (factory is null)
        {
            Log($"SET failed: AudioPolicyConfig factory unavailable (pid={processId}, flow={flow})");
            return false;
        }

        try
        {
            var dataFlow = flow == AudioDeviceFlow.Render ? PolicyConfigDataFlow.Render : PolicyConfigDataFlow.Capture;
            foreach (var candidate in EnumeratePersistedDeviceIdCandidates(deviceId, flow))
            {
                Log($"SET trying: pid={processId} flow={flow} candidate='{candidate}'");

                using var endpointString = WinRtString.Create(candidate);
                var roleResults = new List<(PolicyConfigRole Role, uint Hr)>();

                foreach (var role in PersistedEndpointRoles)
                {
                    var hr = factory.SetPersistedDefaultAudioEndpoint(
                        processId,
                        dataFlow,
                        role,
                        endpointString.Handle);

                    roleResults.Add((role, hr));
                }

                var allSucceeded = roleResults.TrueForAll(r => r.Hr == 0);
                var anySucceeded = roleResults.Exists(r => r.Hr == 0);

                foreach (var (role, hr) in roleResults)
                {
                    Log($"SET role={role} hr=0x{hr:X8} pid={processId} candidate='{candidate}'");
                }

                if (anySucceeded)
                {
                    // Verify the write by reading back immediately.
                    var verified = TryGetPersistedDefaultAudioEndpoint(processId, flow, out var readbackId);
                    Log($"SET verify: pid={processId} readback='{readbackId}' verified={verified} allRolesOk={allSucceeded}");
                    return true;
                }
            }

            Log($"SET failed: no candidate succeeded (pid={processId}, flow={flow}, deviceId='{deviceId}')");
            return false;
        }
        catch (Exception ex)
        {
            Log($"SET exception: pid={processId} flow={flow} deviceId='{deviceId}' error={ex.Message}");
            InvalidateFactory();
            return false;
        }
    }

    private static string NormalizePersistedDeviceId(string? persistedDeviceId, AudioDeviceFlow flow)
    {
        if (string.IsNullOrWhiteSpace(persistedDeviceId))
        {
            return string.Empty;
        }

        if (!persistedDeviceId.StartsWith(MmDeviceIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return persistedDeviceId;
        }

        var suffix = "#" + GetInterfaceClass(flow);
        var startIndex = MmDeviceIdPrefix.Length;
        var length = persistedDeviceId.Length - startIndex;

        if (persistedDeviceId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            length -= suffix.Length;
        }

        return length <= 0
            ? string.Empty
            : persistedDeviceId.Substring(startIndex, length);
    }

    private static string ToPersistedDeviceId(string? deviceId, AudioDeviceFlow flow)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return string.Empty;
        }

        if (deviceId.StartsWith(MmDeviceIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return deviceId;
        }

        return $"{MmDeviceIdPrefix}{deviceId}#{GetInterfaceClass(flow)}";
    }

    private static IEnumerable<string> EnumeratePersistedDeviceIdCandidates(string? deviceId, AudioDeviceFlow flow)
    {
        var normalized = string.IsNullOrWhiteSpace(deviceId) ? string.Empty : deviceId.Trim();
        if (normalized.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        if (normalized.StartsWith(MmDeviceIdPrefix, StringComparison.OrdinalIgnoreCase))
        {
            yield return normalized;
            yield break;
        }

        yield return ToPersistedDeviceId(normalized, flow);
        yield return normalized;
    }

    private static string GetInterfaceClass(AudioDeviceFlow flow)
    {
        return flow == AudioDeviceFlow.Render
            ? RenderDeviceInterfaceClass
            : CaptureDeviceInterfaceClass;
    }

    private static IAudioPolicyConfigFactory? GetOrCreateFactory()
    {
        lock (FactoryLock)
        {
            if (_factoryInitialized && _cachedFactory is not null)
            {
                return _cachedFactory;
            }

            foreach (var iid in SupportedFactoryIids)
            {
                try
                {
                    var requestedIid = iid;
                    using var classIdString = WinRtString.Create(PolicyConfigRuntimeClass);
                    CombaseInterop.RoGetActivationFactory(classIdString.Handle, ref requestedIid, out var factoryObject);

                    _cachedFactory = iid == SupportedFactoryIids[0]
                        ? new AudioPolicyConfigFactoryShim((IAudioPolicyConfigFactoryVariantFor21H2)factoryObject)
                        : new AudioPolicyConfigFactoryShim((IAudioPolicyConfigFactoryDownlevel)factoryObject);

                    _factoryInitialized = true;
                    Log($"AudioPolicyConfig factory created (IID={iid})");
                    return _cachedFactory;
                }
                catch (Exception ex)
                {
                    Log($"Factory activation failed for IID={iid}: {ex.Message}");
                }
            }

            _factoryInitialized = true;
            _cachedFactory = null;
            Log("AudioPolicyConfig factory unavailable on this system.");
            return null;
        }
    }

    private static void InvalidateFactory()
    {
        lock (FactoryLock)
        {
            _cachedFactory = null;
            _factoryInitialized = false;
        }
    }

    private enum PolicyConfigDataFlow
    {
        Render = 0,
        Capture = 1,
    }

    private enum PolicyConfigRole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    private interface IAudioPolicyConfigFactory
    {
        uint SetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, IntPtr deviceId);

        uint GetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, out string deviceId);
    }

    private sealed class AudioPolicyConfigFactoryShim : IAudioPolicyConfigFactory
    {
        private readonly IAudioPolicyConfigFactoryVariant _factory;

        public AudioPolicyConfigFactoryShim(IAudioPolicyConfigFactoryVariantFor21H2 factory)
        {
            _factory = new AudioPolicyConfigFactoryVariantFor21H2Adapter(factory);
        }

        public AudioPolicyConfigFactoryShim(IAudioPolicyConfigFactoryDownlevel factory)
        {
            _factory = new AudioPolicyConfigFactoryDownlevelAdapter(factory);
        }

        public uint SetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, IntPtr deviceId)
        {
            return _factory.SetPersistedDefaultAudioEndpoint(processId, flow, role, deviceId);
        }

        public uint GetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, out string deviceId)
        {
            return _factory.GetPersistedDefaultAudioEndpoint(processId, flow, role, out deviceId);
        }
    }

    private interface IAudioPolicyConfigFactoryVariant
    {
        uint SetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, IntPtr deviceId);

        uint GetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, out string deviceId);
    }

    private sealed class AudioPolicyConfigFactoryVariantFor21H2Adapter : IAudioPolicyConfigFactoryVariant
    {
        private readonly IAudioPolicyConfigFactoryVariantFor21H2 _factory;

        public AudioPolicyConfigFactoryVariantFor21H2Adapter(IAudioPolicyConfigFactoryVariantFor21H2 factory)
        {
            _factory = factory;
        }

        public uint SetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, IntPtr deviceId)
        {
            return _factory.SetPersistedDefaultAudioEndpoint(processId, flow, role, deviceId);
        }

        public uint GetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, out string deviceId)
        {
            var hr = _factory.GetPersistedDefaultAudioEndpoint(processId, flow, role, out var hstringPtr);
            deviceId = HStringToString(hstringPtr);
            return hr;
        }
    }

    private sealed class AudioPolicyConfigFactoryDownlevelAdapter : IAudioPolicyConfigFactoryVariant
    {
        private readonly IAudioPolicyConfigFactoryDownlevel _factory;

        public AudioPolicyConfigFactoryDownlevelAdapter(IAudioPolicyConfigFactoryDownlevel factory)
        {
            _factory = factory;
        }

        public uint SetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, IntPtr deviceId)
        {
            return _factory.SetPersistedDefaultAudioEndpoint(processId, flow, role, deviceId);
        }

        public uint GetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, out string deviceId)
        {
            var hr = _factory.GetPersistedDefaultAudioEndpoint(processId, flow, role, out var hstringPtr);
            deviceId = HStringToString(hstringPtr);
            return hr;
        }
    }

    private sealed class NullAudioPolicyConfigFactory : IAudioPolicyConfigFactory
    {
        public static readonly NullAudioPolicyConfigFactory Instance = new();

        public uint SetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, IntPtr deviceId)
        {
            return 1;
        }

        public uint GetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, out string deviceId)
        {
            deviceId = string.Empty;
            return 1;
        }
    }

    // .NET 8 dropped InterfaceIsIInspectable.  Use InterfaceIsIUnknown and add
    // three IInspectable stubs (GetIids, GetRuntimeClassName, GetTrustLevel)
    // before the existing method stubs so the vtable stays aligned.

    [Guid("ab3d4648-e242-459f-b02f-541c70306324")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactoryVariantFor21H2
    {
        // IInspectable
        int GetIids();
        int GetRuntimeClassName();
        int GetTrustLevel();

        // AudioPolicyConfig stubs
        int AddContextVolumeChange();
        int RemoveContextVolumeChanged();
        int AddRingerVibrateStateChanged();
        int RemoveRingerVibrateStateChange();
        int SetVolumeGroupGainForId();
        int GetVolumeGroupGainForId();
        int GetActiveVolumeGroupForEndpointId();
        int GetVolumeGroupsForEndpoint();
        int GetCurrentVolumeContext();
        int SetVolumeGroupMuteForId();
        int GetVolumeGroupMuteForId();
        int SetRingerVibrateState();
        int GetRingerVibrateState();
        int SetPreferredChatApplication();
        int ResetPreferredChatApplication();
        int GetPreferredChatApplication();
        int GetCurrentChatApplications();
        int AddChatContextChanged();
        int RemoveChatContextChanged();

        [PreserveSig]
        uint SetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, IntPtr deviceId);

        [PreserveSig]
        uint GetPersistedDefaultAudioEndpoint(
            int processId,
            PolicyConfigDataFlow flow,
            PolicyConfigRole role,
            out IntPtr deviceId);

        [PreserveSig]
        uint ClearAllPersistedApplicationDefaultEndpoints();
    }

    [Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioPolicyConfigFactoryDownlevel
    {
        // IInspectable
        int GetIids();
        int GetRuntimeClassName();
        int GetTrustLevel();

        // AudioPolicyConfig stubs
        int AddContextVolumeChange();
        int RemoveContextVolumeChanged();
        int AddRingerVibrateStateChanged();
        int RemoveRingerVibrateStateChange();
        int SetVolumeGroupGainForId();
        int GetVolumeGroupGainForId();
        int GetActiveVolumeGroupForEndpointId();
        int GetVolumeGroupsForEndpoint();
        int GetCurrentVolumeContext();
        int SetVolumeGroupMuteForId();
        int GetVolumeGroupMuteForId();
        int SetRingerVibrateState();
        int GetRingerVibrateState();
        int SetPreferredChatApplication();
        int ResetPreferredChatApplication();
        int GetPreferredChatApplication();
        int GetCurrentChatApplications();
        int AddChatContextChanged();
        int RemoveChatContextChanged();

        [PreserveSig]
        uint SetPersistedDefaultAudioEndpoint(int processId, PolicyConfigDataFlow flow, PolicyConfigRole role, IntPtr deviceId);

        [PreserveSig]
        uint GetPersistedDefaultAudioEndpoint(
            int processId,
            PolicyConfigDataFlow flow,
            PolicyConfigRole role,
            out IntPtr deviceId);

        [PreserveSig]
        uint ClearAllPersistedApplicationDefaultEndpoints();
    }

    private static class CombaseInterop
    {
        [DllImport("combase.dll", PreserveSig = false)]
        public static extern void RoGetActivationFactory(
            IntPtr activatableClassId,
            ref Guid iid,
            [MarshalAs(UnmanagedType.IUnknown)] out object factory);
    }

    private static string HStringToString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = WindowsGetStringRawBuffer(hstring, out var length);
        if (buffer == IntPtr.Zero || length == 0)
        {
            return string.Empty;
        }

        return Marshal.PtrToStringUni(buffer, (int)length) ?? string.Empty;
    }

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    private readonly ref struct WinRtString
    {
        public static WinRtString Create(string value)
        {
            return new WinRtString(value ?? string.Empty);
        }

        private WinRtString(string value)
        {
            WindowsCreateString(value, value.Length, out var handle);
            Handle = handle;
        }

        public IntPtr Handle { get; }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                WindowsDeleteString(Handle);
            }
        }

        [DllImport("combase.dll")]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            int length,
            out IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);
    }

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "audiobit-route.log");
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch
        {
            // Don't let logging failures break routing.
        }
    }
}
