using System.IO;
using System.Reflection;
using AudioBit.Core;
using Microsoft.Win32;

namespace AudioBit.Installer;

internal static class InstallerRegistrationService
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AudioBit";
    private static readonly Lazy<ExternalLinksConfiguration> ExternalLinks = new(() =>
        ExternalLinksConfigurationLoader.Load(Path.Combine(AppContext.BaseDirectory, "external-links.json")));

    public static void RegisterInstall(string installPath, string uninstallExecutablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        if (key is null)
        {
            throw new InvalidOperationException("Windows uninstall registration could not be created.");
        }

        var installedExecutable = InstallerPaths.GetInstalledExecutablePath(installPath);
        var uninstallString = $"\"{uninstallExecutablePath}\" --uninstall --install-path \"{installPath}\"";
        var quietUninstallString = $"\"{uninstallExecutablePath}\" --uninstall-silent --install-path \"{installPath}\"";
        var estimatedSizeKb = Directory.Exists(installPath)
            ? Math.Min(int.MaxValue, GetDirectorySize(installPath) / 1024)
            : 0;
        var displayVersion = AppVersionInfo.GetDisplayVersion(Assembly.GetExecutingAssembly());

        key.SetValue("DisplayName", InstallerPaths.AppDisplayName);
        key.SetValue("DisplayVersion", displayVersion);
        key.SetValue("Publisher", "Amiya");
        key.SetValue("InstallLocation", installPath);
        key.SetValue("DisplayIcon", installedExecutable);
        key.SetValue("UninstallString", uninstallString);
        key.SetValue("QuietUninstallString", quietUninstallString);
        key.SetValue("URLInfoAbout", ExternalLinks.Value.AboutUri.AbsoluteUri);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", estimatedSizeKb, RegistryValueKind.DWord);
    }

    public static void RemoveInstallRegistration()
    {
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
    }

    private static long GetDirectorySize(string directoryPath)
    {
        return Directory
            .EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
            .Select(filePath => new FileInfo(filePath).Length)
            .Sum();
    }
}
