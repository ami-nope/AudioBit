using System.IO.Compression;
using AudioBit.Installer;
using Xunit;

namespace AudioBit.Installer.Tests;

public sealed class InstallerEngineTests
{
    [Fact]
    public async Task InstallAsync_ExtractsPayloadAndReplacesExistingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "AudioBitInstallerTests", Guid.NewGuid().ToString("N"));
        var payloadZip = Path.Combine(root, "AudioBit-win-x64.zip");
        var installDir = Path.Combine(root, "install");
        var progressEvents = new List<InstallerProgress>();

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, "obsolete.txt"), "old");

        CreatePayloadZip(payloadZip);

        try
        {
            var engine = new InstallerEngine(payloadZip);

            await engine.InstallAsync(
                installDir,
                new Progress<InstallerProgress>(progressEvents.Add),
                options: new InstallerOptions(CreateDesktopShortcut: false, CreateStartMenuShortcut: false),
                closeRunningInstances: false,
                cacheUninstaller: false,
                registerWithWindows: false);

            Assert.True(File.Exists(Path.Combine(installDir, "AudioBit.exe")));
            Assert.True(File.Exists(Path.Combine(installDir, "Update.exe")));
            Assert.True(File.Exists(Path.Combine(installDir, "current", "AudioBit.App.exe")));
            Assert.True(File.Exists(Path.Combine(installDir, "current", "sq.version")));
            Assert.True(File.Exists(Path.Combine(installDir, "current", "data", "settings.json")));
            Assert.False(File.Exists(Path.Combine(installDir, "obsolete.txt")));
            Assert.Contains(progressEvents, progress => progress.Phase == "EXTRACTING");
            Assert.DoesNotContain(progressEvents, progress => progress.Phase == "FAILED");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task UninstallAsync_RemovesInstalledFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "AudioBitInstallerTests", Guid.NewGuid().ToString("N"));
        var payloadZip = Path.Combine(root, "AudioBit-win-x64.zip");
        var installDir = Path.Combine(root, "install");
        var progressEvents = new List<InstallerProgress>();

        Directory.CreateDirectory(root);
        CreatePayloadZip(payloadZip);

        try
        {
            var engine = new InstallerEngine(payloadZip);

            await engine.InstallAsync(
                installDir,
                new Progress<InstallerProgress>(_ => { }),
                options: new InstallerOptions(CreateDesktopShortcut: false, CreateStartMenuShortcut: false),
                closeRunningInstances: false,
                cacheUninstaller: false,
                registerWithWindows: false);

            await engine.UninstallAsync(
                installDir,
                new Progress<InstallerProgress>(progressEvents.Add),
                closeRunningInstances: false,
                removeWindowsRegistration: false);

            Assert.False(Directory.Exists(installDir));
            Assert.Contains(progressEvents, progress => progress.Phase == "FILES");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void InstallerOptions_PinToStart_RequiresShortcutRefresh()
    {
        var options = new InstallerOptions(
            CreateDesktopShortcut: false,
            CreateStartMenuShortcut: false,
            PinToStart: true,
            PinToTaskbar: false);

        Assert.True(options.CreateAnyShortcut);
        Assert.True(options.RequiresPinRefresh);
    }

    [Fact]
    public void InstallerOptions_TaskbarPin_DoesNotRequireShortcutCreation()
    {
        var options = new InstallerOptions(
            CreateDesktopShortcut: false,
            CreateStartMenuShortcut: false,
            PinToStart: false,
            PinToTaskbar: true);

        Assert.False(options.CreateAnyShortcut);
        Assert.True(options.RequiresPinRefresh);
    }

    private static void CreatePayloadZip(string payloadZip)
    {
        using var archive = ZipFile.Open(payloadZip, ZipArchiveMode.Create);

        var launcherEntry = archive.CreateEntry("AudioBit.exe");
        using (var writer = new StreamWriter(launcherEntry.Open()))
        {
            writer.Write("launcher");
        }

        var updateEntry = archive.CreateEntry("Update.exe");
        using (var writer = new StreamWriter(updateEntry.Open()))
        {
            writer.Write("updater");
        }

        var markerEntry = archive.CreateEntry(".portable");
        using (var writer = new StreamWriter(markerEntry.Open()))
        {
            writer.Write(string.Empty);
        }

        var appEntry = archive.CreateEntry("current/AudioBit.App.exe");
        using (var writer = new StreamWriter(appEntry.Open()))
        {
            writer.Write("stub executable");
        }

        var versionEntry = archive.CreateEntry("current/sq.version");
        using (var writer = new StreamWriter(versionEntry.Open()))
        {
            writer.Write("1.0.0");
        }

        var settingsEntry = archive.CreateEntry("current/data/settings.json");
        using var settingsWriter = new StreamWriter(settingsEntry.Open());
        settingsWriter.Write("{}");
    }
}
