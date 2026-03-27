using System.IO;

namespace AudioBit.Installer;

internal static class InstallerPaths
{
    public const string AppDisplayName = "AudioBit";
    public const string AppExecutableName = "AudioBit.exe";
    public const string PayloadFileName = "AudioBit-win-x64.zip";
    public const string SetupExecutableName = "AudioBit.Setup.exe";

    public static string GetDefaultInstallPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "AudioBit");
    }

    public static string GetInstalledExecutablePath(string installPath)
    {
        return Path.Combine(installPath, AppExecutableName);
    }

    public static string GetDesktopShortcutPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{AppDisplayName}.lnk");
    }

    public static string GetInstallerLogPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioBit",
            "Installer",
            "setup.log");
    }

    public static string GetStartMenuDirectoryPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            "AudioBit");
    }

    public static string GetStartMenuShortcutPath()
    {
        return Path.Combine(GetStartMenuDirectoryPath(), $"{AppDisplayName}.lnk");
    }

    public static string GetStartMenuUninstallShortcutPath()
    {
        return Path.Combine(GetStartMenuDirectoryPath(), $"Uninstall {AppDisplayName}.lnk");
    }

    public static string GetInstallerCacheDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDisplayName,
            "InstallerCache");
    }

    public static string GetCachedSetupExecutablePath()
    {
        return Path.Combine(GetInstallerCacheDirectory(), SetupExecutableName);
    }

    public static string GetPayloadPath(string baseDirectory)
    {
        return Path.Combine(baseDirectory, "payload", PayloadFileName);
    }
}
