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

    public bool TryGetPersistedDefaultAudioEndpoint(int processId, AudioDeviceFlow flow, out string deviceId)
    {
        deviceId = string.Empty;

        if (processId <= 0 || !TryGetFactory(out var factory))
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
        catch
        {
            return false;
        }
    }

    public bool TrySetPersistedDefaultAudioEndpoint(int processId, AudioDeviceFlow flow, string? deviceId)
    {
        if (processId <= 0 || !TryGetFactory(out var factory))
        {
            return false;
        }

        using var endpointString = WinRtString.Create(ToPersistedDeviceId(deviceId, flow));

        try
        {
            var dataFlow = flow == AudioDeviceFlow.Render ? PolicyConfigDataFlow.Render : PolicyConfigDataFlow.Capture;
            var anySucceeded = false;

            foreach (var role in PersistedEndpointRoles)
            {
                if (factory.SetPersistedDefaultAudioEndpoint(
                        processId,
                        dataFlow,
                        role,
                        endpointString.Handle) == 0)
                {
                    anySucceeded = true;
                }
            }

            return anySucceeded;
        }
        catch
        {
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

    private static string GetInterfaceClass(AudioDeviceFlow flow)
    {
        return flow == AudioDeviceFlow.Render
            ? RenderDeviceInterfaceClass
            : CaptureDeviceInterfaceClass;
    }

    private static bool TryGetFactory(out IAudioPolicyConfigFactory factory)
    {
        foreach (var iid in SupportedFactoryIids)
        {
            try
            {
                object factoryObject;
                var requestedIid = iid;
                CombaseInterop.RoGetActivationFactory(PolicyConfigRuntimeClass, ref requestedIid, out factoryObject);

                factory = iid == SupportedFactoryIids[0]
                    ? new AudioPolicyConfigFactoryShim((IAudioPolicyConfigFactoryVariantFor21H2)factoryObject)
                    : new AudioPolicyConfigFactoryShim((IAudioPolicyConfigFactoryDownlevel)factoryObject);

                return true;
            }
            catch
            {
                // Try the next known factory layout.
            }
        }

        factory = NullAudioPolicyConfigFactory.Instance;
        return false;
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
            return _factory.GetPersistedDefaultAudioEndpoint(processId, flow, role, out deviceId);
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
            return _factory.GetPersistedDefaultAudioEndpoint(processId, flow, role, out deviceId);
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

    [Guid("ab3d4648-e242-459f-b02f-541c70306324")]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IAudioPolicyConfigFactoryVariantFor21H2
    {
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
            [Out, MarshalAs(UnmanagedType.HString)] out string deviceId);

        [PreserveSig]
        uint ClearAllPersistedApplicationDefaultEndpoints();
    }

    [Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258")]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IAudioPolicyConfigFactoryDownlevel
    {
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
            [Out, MarshalAs(UnmanagedType.HString)] out string deviceId);

        [PreserveSig]
        uint ClearAllPersistedApplicationDefaultEndpoints();
    }

    private static class CombaseInterop
    {
        [DllImport("combase.dll", PreserveSig = false)]
        public static extern void RoGetActivationFactory(
            [MarshalAs(UnmanagedType.HString)] string activatableClassId,
            ref Guid iid,
            [MarshalAs(UnmanagedType.IInspectable)] out object factory);
    }

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
}
