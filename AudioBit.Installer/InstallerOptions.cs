namespace AudioBit.Installer;

public sealed record InstallerOptions(
    bool CreateDesktopShortcut = true,
    bool CreateStartMenuShortcut = true,
    bool PinToStart = false,
    bool PinToTaskbar = false)
{
    public bool CreateAnyShortcut => CreateDesktopShortcut || CreateStartMenuShortcut || PinToStart;

    public bool RequiresPinRefresh => PinToStart || PinToTaskbar;
}
