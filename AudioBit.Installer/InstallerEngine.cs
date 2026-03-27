using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace AudioBit.Installer;

public sealed class InstallerEngine
{
    private readonly string _payloadPath;

    public InstallerEngine(string payloadPath)
    {
        _payloadPath = payloadPath;
    }

    public string GetPayloadSummaryText()
    {
        if (!File.Exists(_payloadPath))
        {
            return "Bundled payload missing";
        }

        var fileInfo = new FileInfo(_payloadPath);
        return $"Bundled payload: {FormatSize(fileInfo.Length)}";
    }

    public async Task InstallAsync(
        string installPath,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken = default,
        InstallerOptions? options = null,
        bool closeRunningInstances = true,
        bool cacheUninstaller = true,
        bool registerWithWindows = true)
    {
        progress.Report(new InstallerProgress("PREPARING", "Preparing the AudioBit setup run.", 3));
        InstallerLogger.Log($"Install requested. Target='{installPath}', Payload='{_payloadPath}'.");
        options ??= new InstallerOptions();

        ValidateInstallPath(installPath);

        if (!File.Exists(_payloadPath))
        {
            throw new FileNotFoundException("The bundled app package is missing.", _payloadPath);
        }

        await Task.Run(
            () => InstallCore(
                installPath,
                progress,
                cancellationToken,
                options,
                closeRunningInstances,
                cacheUninstaller,
                registerWithWindows),
            cancellationToken);
    }

    public async Task UninstallAsync(
        string installPath,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken = default,
        bool closeRunningInstances = true,
        bool removeWindowsRegistration = true)
    {
        progress.Report(new InstallerProgress("PREPARING", "Preparing to remove AudioBit.", 3));
        InstallerLogger.Log($"Uninstall requested. Target='{installPath}'.");

        ValidateInstallPath(installPath);

        await Task.Run(
            () => UninstallCore(
                installPath,
                progress,
                cancellationToken,
                closeRunningInstances,
                removeWindowsRegistration),
            cancellationToken);
    }

    private void CacheInstallerRuntime()
    {
        var sourceDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var cacheDirectory = Path.GetFullPath(InstallerPaths.GetInstallerCacheDirectory());

        if (string.Equals(sourceDirectory.TrimEnd('\\'), cacheDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SafeDeleteDirectory(cacheDirectory);
        Directory.CreateDirectory(cacheDirectory);

        foreach (var filePath in Directory.GetFiles(sourceDirectory))
        {
            var fileName = Path.GetFileName(filePath);
            File.Copy(filePath, Path.Combine(cacheDirectory, fileName), true);
        }

        foreach (var directoryPath in Directory.GetDirectories(sourceDirectory))
        {
            var directoryName = Path.GetFileName(directoryPath);
            if (string.Equals(directoryName, "payload", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            CopyDirectory(directoryPath, Path.Combine(cacheDirectory, directoryName));
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var filePath in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(filePath, Path.Combine(destinationDirectory, Path.GetFileName(filePath)), true);
        }

        foreach (var childDirectory in Directory.GetDirectories(sourceDirectory))
        {
            CopyDirectory(childDirectory, Path.Combine(destinationDirectory, Path.GetFileName(childDirectory)));
        }
    }

    private void CloseRunningAudioBitProcesses(IProgress<InstallerProgress> progress)
    {
        var processNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AudioBit",
            "AudioBit.App"
        };
        var processes = Process.GetProcesses()
            .Where(process => processNames.Contains(process.ProcessName))
            .ToArray();
        if (processes.Length == 0)
        {
            return;
        }

        progress.Report(new InstallerProgress("STOPPING APP", "Closing the running AudioBit instance.", 6));
        InstallerLogger.Log($"Found {processes.Length} running AudioBit process(es).");

        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                process.CloseMainWindow();
                if (!process.WaitForExit(3000))
                {
                    process.Kill(true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                InstallerLogger.Log($"Process shutdown failed for PID {process.Id}: {ex}");
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory, string description, string? arguments = null)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable.");

        object? shell = null;
        object? shortcut = null;

        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
            if (shortcut is null)
            {
                throw new InvalidOperationException("Windows could not create the AudioBit shortcut.");
            }

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, [targetPath]);
            shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, [workingDirectory]);
            shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, [description]);
            shortcutType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, [$"{targetPath},0"]);
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, [arguments]);
            }
            shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private void CreateShortcuts(string installRoot, InstallerOptions options)
    {
        var executablePath = InstallerPaths.GetInstalledExecutablePath(installRoot);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Installed app executable was not found.", executablePath);
        }

        var desktopShortcut = InstallerPaths.GetDesktopShortcutPath();
        var startMenuDirectory = InstallerPaths.GetStartMenuDirectoryPath();
        var startMenuShortcut = InstallerPaths.GetStartMenuShortcutPath();
        var startMenuUninstallShortcut = InstallerPaths.GetStartMenuUninstallShortcutPath();
        var cachedSetupExecutable = InstallerPaths.GetCachedSetupExecutablePath();
        var shouldCreateStartMenuEntries = options.CreateStartMenuShortcut || options.PinToStart;

        if (options.CreateDesktopShortcut)
        {
            CreateShortcut(desktopShortcut, executablePath, installRoot, InstallerPaths.AppDisplayName);
        }
        else
        {
            DeleteFileIfExists(desktopShortcut);
        }

        if (shouldCreateStartMenuEntries)
        {
            Directory.CreateDirectory(startMenuDirectory);
            CreateShortcut(startMenuShortcut, executablePath, installRoot, InstallerPaths.AppDisplayName);

            if (File.Exists(cachedSetupExecutable))
            {
                CreateShortcut(
                    startMenuUninstallShortcut,
                    cachedSetupExecutable,
                    InstallerPaths.GetInstallerCacheDirectory(),
                    $"Uninstall {InstallerPaths.AppDisplayName}",
                    $"--uninstall --install-path \"{installRoot}\"");
            }
        }
        else
        {
            DeleteFileIfExists(startMenuShortcut);
            DeleteFileIfExists(startMenuUninstallShortcut);
            DeleteDirectoryIfEmpty(startMenuDirectory);
        }
    }

    private static void DeleteDirectoryIfEmpty(string directoryPath)
    {
        if (Directory.Exists(directoryPath) && !Directory.EnumerateFileSystemEntries(directoryPath).Any())
        {
            Directory.Delete(directoryPath);
        }
    }

    private static void DeleteFileIfExists(string filePath)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private void ExtractPayloadToDirectory(string zipPath, string destinationDirectory, IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);

        using var archive = ZipFile.OpenRead(zipPath);
        var totalBytes = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).Sum(entry => entry.Length);
        long processedBytes = 0;
        var rootPath = Path.GetFullPath(destinationDirectory);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destinationPath = Path.GetFullPath(Path.Combine(rootPath, entry.FullName));
            if (!destinationPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The packaged payload contains an invalid path.");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            using var entryStream = entry.Open();
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fileStream.Write(buffer, 0, bytesRead);
                processedBytes += bytesRead;

                var percent = totalBytes == 0
                    ? 90d
                    : 8d + ((double)processedBytes / totalBytes * 84d);

                progress.Report(new InstallerProgress("EXTRACTING", $"Expanding {entry.Name}...", percent));
            }

            File.SetLastWriteTime(destinationPath, entry.LastWriteTime.LocalDateTime);
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.#} {units[unitIndex]}";
    }

    private void InstallCore(
        string installPath,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken,
        InstallerOptions options,
        bool closeRunningInstances,
        bool cacheUninstaller,
        bool registerWithWindows)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (closeRunningInstances)
        {
            CloseRunningAudioBitProcesses(progress);
        }

        var installRoot = Path.GetFullPath(installPath);
        var parentDirectory = Directory.GetParent(installRoot)
            ?? throw new InvalidOperationException("The selected install path is invalid.");

        Directory.CreateDirectory(parentDirectory.FullName);

        var stagingDirectory = installRoot + ".staging";
        var backupDirectory = installRoot + ".backup";

        SafeDeleteDirectory(stagingDirectory);
        SafeDeleteDirectory(backupDirectory);

        try
        {
            progress.Report(new InstallerProgress("EXTRACTING", "Expanding the bundled AudioBit payload.", 8));
            ExtractPayloadToDirectory(_payloadPath, stagingDirectory, progress, cancellationToken);

            progress.Report(new InstallerProgress("SWAPPING", "Applying the new AudioBit files.", 88));
            if (Directory.Exists(installRoot))
            {
                Directory.Move(installRoot, backupDirectory);
            }

            Directory.Move(stagingDirectory, installRoot);

            if (cacheUninstaller)
            {
                progress.Report(new InstallerProgress("REGISTERING", "Caching the themed uninstaller.", 92));
                TryCacheInstallerRuntime(progress);
            }

            if (options.CreateAnyShortcut)
            {
                progress.Report(new InstallerProgress("SHORTCUTS", "Refreshing app and uninstall shortcuts.", 95));
                CreateShortcuts(installRoot, options);
            }
            else
            {
                RemoveShortcuts();
            }

            if (options.RequiresPinRefresh)
            {
                progress.Report(new InstallerProgress("PINNING", "Applying Start and taskbar pins.", 97));
                RefreshPins(installRoot, options);
            }

            if (registerWithWindows)
            {
                progress.Report(new InstallerProgress("WINDOWS", "Registering AudioBit in Windows Apps.", 98));
                InstallerRegistrationService.RegisterInstall(installRoot, InstallerPaths.GetCachedSetupExecutablePath());
            }

            SafeDeleteDirectory(backupDirectory);
            InstallerLogger.Log($"Install completed successfully. Target='{installRoot}'.");
        }
        catch
        {
            SafeDeleteDirectory(stagingDirectory);

            if (!Directory.Exists(installRoot) && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, installRoot);
            }

            throw;
        }
    }

    private void TryCacheInstallerRuntime(IProgress<InstallerProgress> progress)
    {
        try
        {
            CacheInstallerRuntime();
        }
        catch (Exception ex)
        {
            InstallerLogger.Log($"Installer cache refresh failed. Continuing without cache update. {ex}");
            progress.Report(new InstallerProgress("REGISTERING", "Continuing without refreshing cached uninstaller files.", 93));
        }
    }

    private void RemoveShortcuts()
    {
        DeleteFileIfExists(InstallerPaths.GetDesktopShortcutPath());
        DeleteFileIfExists(InstallerPaths.GetStartMenuShortcutPath());
        DeleteFileIfExists(InstallerPaths.GetStartMenuUninstallShortcutPath());
        DeleteDirectoryIfEmpty(InstallerPaths.GetStartMenuDirectoryPath());
    }

    private void RefreshPins(string installRoot, InstallerOptions options)
    {
        if (!options.RequiresPinRefresh)
        {
            return;
        }

        var executablePath = InstallerPaths.GetInstalledExecutablePath(installRoot);
        var startMenuShortcutPath = InstallerPaths.GetStartMenuShortcutPath();

        if (options.PinToStart)
        {
            var startTarget = File.Exists(startMenuShortcutPath) ? startMenuShortcutPath : executablePath;
            if (!ShellPinService.TryPinToStart(startTarget))
            {
                InstallerLogger.Log($"Start pin request was skipped or blocked for '{startTarget}'.");
            }
        }

        if (options.PinToTaskbar)
        {
            var taskbarTarget = File.Exists(executablePath) ? executablePath : startMenuShortcutPath;
            if (!ShellPinService.TryPinToTaskbar(taskbarTarget))
            {
                InstallerLogger.Log($"Taskbar pin request was skipped or blocked for '{taskbarTarget}'.");
            }
        }
    }

    private void RemovePins(string installRoot)
    {
        var executablePath = InstallerPaths.GetInstalledExecutablePath(installRoot);
        var startMenuShortcutPath = InstallerPaths.GetStartMenuShortcutPath();

        TryUnpin(executablePath);
        TryUnpin(startMenuShortcutPath);
    }

    private void TryUnpin(string itemPath)
    {
        if (string.IsNullOrWhiteSpace(itemPath) || !File.Exists(itemPath))
        {
            return;
        }

        var startUnpinned = ShellPinService.TryUnpinFromStart(itemPath);
        var taskbarUnpinned = ShellPinService.TryUnpinFromTaskbar(itemPath);
        if (!startUnpinned && !taskbarUnpinned)
        {
            InstallerLogger.Log($"No removable Start or taskbar pin was found for '{itemPath}'.");
        }
    }

    private void SafeDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var filePath in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(filePath, FileAttributes.Normal);
        }

        Directory.Delete(path, true);
    }

    private void ScheduleCacheCleanupIfNeeded()
    {
        var cacheDirectory = Path.GetFullPath(InstallerPaths.GetInstallerCacheDirectory());
        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory);
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            return;
        }

        var currentDirectory = Path.GetFullPath(executableDirectory);
        if (!string.Equals(currentDirectory.TrimEnd('\\'), cacheDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var scriptPath = Path.Combine(Path.GetTempPath(), $"audiobit-cleanup-{Guid.NewGuid():N}.cmd");
        var scriptLines = new[]
        {
            "@echo off",
            "ping 127.0.0.1 -n 3 > nul",
            $"rmdir /s /q \"{cacheDirectory}\"",
            "del /f /q \"%~f0\""
        };

        File.WriteAllLines(scriptPath, scriptLines);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private void UninstallCore(
        string installPath,
        IProgress<InstallerProgress> progress,
        CancellationToken cancellationToken,
        bool closeRunningInstances,
        bool removeWindowsRegistration)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (closeRunningInstances)
        {
            CloseRunningAudioBitProcesses(progress);
        }

        var installRoot = Path.GetFullPath(installPath);

        progress.Report(new InstallerProgress("PINNING", "Removing AudioBit Start and taskbar pins.", 12));
        RemovePins(installRoot);

        progress.Report(new InstallerProgress("SHORTCUTS", "Removing AudioBit shortcuts.", 20));
        RemoveShortcuts();

        if (removeWindowsRegistration)
        {
            progress.Report(new InstallerProgress("WINDOWS", "Removing AudioBit from Windows Apps.", 40));
            InstallerRegistrationService.RemoveInstallRegistration();
        }

        if (Directory.Exists(installRoot))
        {
            progress.Report(new InstallerProgress("FILES", "Removing installed AudioBit files.", 72));
            SafeDeleteDirectory(installRoot);
        }

        progress.Report(new InstallerProgress("CLEANUP", "Finalizing uninstall.", 94));
        ScheduleCacheCleanupIfNeeded();

        InstallerLogger.Log($"Uninstall completed successfully. Target='{installRoot}'.");
    }

    private void ValidateInstallPath(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            throw new InvalidOperationException("Choose a valid install path before continuing.");
        }

        if (!Path.IsPathRooted(installPath))
        {
            throw new InvalidOperationException("The install path must be an absolute Windows path.");
        }
    }
}
