using System.Runtime.InteropServices;

namespace AudioBit.Core;

/// <summary>
/// Switches the system-wide default audio endpoint using the undocumented
/// IPolicyConfig COM interface. This is the same mechanism used by Windows
/// Sound Settings to change the default playback / capture device.
/// </summary>
internal sealed class SystemDefaultDeviceSwitcher
{
    public bool SetDefaultEndpoint(string deviceId, AudioDeviceFlow flow)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        try
        {
            var policyConfig = (IPolicyConfig)new PolicyConfigClient();

            // Set for all three roles so every category of audio moves.
            var hr0 = policyConfig.SetDefaultEndpoint(deviceId, ERole.Console);
            var hr1 = policyConfig.SetDefaultEndpoint(deviceId, ERole.Multimedia);
            var hr2 = policyConfig.SetDefaultEndpoint(deviceId, ERole.Communications);

            return hr0 == 0 || hr1 == 0 || hr2 == 0;
        }
        catch
        {
            return false;
        }
    }

    // -----------------------------------------------------------------------
    //  COM interop declarations for IPolicyConfig / PolicyConfigClient
    // -----------------------------------------------------------------------

    private enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            IntPtr ppFormat);

        [PreserveSig]
        int GetDeviceFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [MarshalAs(UnmanagedType.Bool)] bool bDefault,
            IntPtr ppFormat);

        [PreserveSig]
        int ResetDeviceFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);

        [PreserveSig]
        int SetDeviceFormat(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            IntPtr pEndpointFormat,
            IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [MarshalAs(UnmanagedType.Bool)] bool bDefault,
            IntPtr pmftDefaultPeriod,
            IntPtr pmftMinimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            IntPtr pmftPeriod);

        [PreserveSig]
        int GetShareMode(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            IntPtr pMode);

        [PreserveSig]
        int SetShareMode(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            IntPtr mode);

        [PreserveSig]
        int GetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [MarshalAs(UnmanagedType.Bool)] bool bFxStore,
            IntPtr key,
            IntPtr pv);

        [PreserveSig]
        int SetPropertyValue(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [MarshalAs(UnmanagedType.Bool)] bool bFxStore,
            IntPtr key,
            IntPtr pv);

        [PreserveSig]
        int SetDefaultEndpoint(
            [MarshalAs(UnmanagedType.LPWStr)] string wszDeviceId,
            ERole eRole);

        [PreserveSig]
        int SetEndpointVisibility(
            [MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName,
            [MarshalAs(UnmanagedType.Bool)] bool bVisible);
    }
}
