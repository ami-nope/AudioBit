namespace AudioBit.App.Models;

public sealed class AppSettingsSnapshot
{
    public bool OpenOnStartup { get; set; }

    public bool HideToTrayOnMinimize { get; set; } = true;

    public bool StartMinimized { get; set; }

    public bool AutoReconnectRemote { get; set; } = true;

    public bool RunAsBackgroundService { get; set; } = true;

    public bool IsAlwaysOnTop { get; set; }

    public bool ShowAlwaysOnTopPin { get; set; } = true;

    public bool? IsLowPerformanceMode { get; set; }

    public CloseButtonBehavior CloseButtonBehavior { get; set; } = CloseButtonBehavior.Exit;

    public string DefaultPlaybackDeviceId { get; set; } = string.Empty;

    public string DefaultMicrophoneDeviceId { get; set; } = string.Empty;

    public string MicMuteHotKey { get; set; } = "Ctrl + Shift + M";

    public int VolumeStepPercent { get; set; } = 5;

    public bool AutoMuteMicOnSoundboard { get; set; }

    public bool DebugMode { get; set; }

    public bool IsDarkTheme { get; set; } = true;

    public string SelectedAgentKey { get; set; } = "mixer-core";

    public string SelectedCalibrationMode { get; set; } = "Adaptive";

    public string SelectedCalibrationOption { get; set; } = "Balanced";

    public string AppliedCalibrationLabel { get; set; } = "Adaptive / Balanced";
}
