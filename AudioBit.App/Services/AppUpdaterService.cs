using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using AudioBit.App.Infrastructure;
using AudioBit.App.Models;
using AudioBit.Core;
using Velopack;
using Velopack.Sources;

namespace AudioBit.App.Services;

internal sealed class AppUpdaterService : IDisposable
{
    private const string UpdateRepositoryUrl = "https://github.com/ami-nope/AudioBit";
    private const string LegacyInstallFolderName = "AudioBit";
    private const int DebugLogTailLineCount = 60;
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly object LogFileGate = new();
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex MarkdownImageRegex = new(@"!\[[^\]]*\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownLinkRegex = new(@"\[(?<label>[^\]]+)\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex MarkdownHeadingRegex = new(@"^\s{0,3}#+\s*", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex MarkdownListRegex = new(@"^\s*(?:[-*+]|\d+\.)\s+", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex MarkdownDecorationRegex = new(@"[*_`~>#]", RegexOptions.Compiled);
    private static readonly Regex ParagraphSplitRegex = new(@"\r?\n\s*\r?\n", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _statusGate = new();
    private readonly Assembly _appAssembly;
    private bool _initializeStarted;
    private bool _disposed;
    private VelopackAsset? _pendingUpdate;
    private AppUpdateStatusSnapshot _currentStatus;

    public AppUpdaterService()
    {
        _appAssembly = typeof(AppUpdaterService).Assembly;
        var currentVersion = AppVersionInfo.GetCurrentVersion(_appAssembly);
        var displayVersion = AppVersionInfo.GetDisplayVersion(_appAssembly);
        var installKind = DetectInstallKind();
        _currentStatus = CreateInitialStatus(currentVersion, displayVersion, installKind);
    }

    public event Action<AppUpdateStatusSnapshot>? StatusChanged;

    public AppUpdateStatusSnapshot CurrentStatus
    {
        get
        {
            lock (_statusGate)
            {
                return _currentStatus;
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        lock (_statusGate)
        {
            if (_initializeStarted)
            {
                return;
            }

            _initializeStarted = true;
        }

        if (CurrentStatus.InstallKind != AppInstallKind.Velopack)
        {
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token, cancellationToken);
        var linkedToken = linkedCts.Token;

        try
        {
            await Task.Delay(StartupDelay, linkedToken).ConfigureAwait(false);
            UpdateStatus(snapshot => snapshot with
            {
                State = AppUpdateState.Checking,
                StatusTitle = "Checking for updates",
                StatusDetail = "Checking for the latest stable AudioBit release.",
            });

            var updateManager = CreateUpdateManager();
            var pendingUpdate = updateManager.UpdatePendingRestart;
            if (pendingUpdate is not null)
            {
                _pendingUpdate = pendingUpdate;
                UpdateStatus(snapshot => snapshot with
                {
                    State = AppUpdateState.RestartRequired,
                    StatusTitle = "Restart required",
                    StatusDetail = $"AudioBit needs to restart to finish installing version {AppVersionInfo.NormalizeForDisplay(pendingUpdate.Version.ToString())}.",
                    TargetVersion = AppVersionInfo.NormalizeForDisplay(pendingUpdate.Version.ToString()),
                    ReleaseSummary = BuildReleaseSummary(pendingUpdate),
                    IsRestartDialogVisible = true,
                    IsUpdateSummaryVisible = true,
                });
                return;
            }

            var updateInfo = await updateManager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (updateInfo is null)
            {
                UpdateStatus(snapshot => snapshot with
                {
                    State = AppUpdateState.UpToDate,
                    StatusTitle = "Up to date",
                    StatusDetail = "AudioBit is on the latest stable release.",
                    TargetVersion = null,
                    ReleaseSummary = null,
                    IsRestartDialogVisible = false,
                    IsUpdateSummaryVisible = false,
                });
                return;
            }

            var targetVersion = AppVersionInfo.NormalizeForDisplay(updateInfo.TargetFullRelease.Version.ToString());
            UpdateStatus(snapshot => snapshot with
            {
                State = AppUpdateState.Downloading,
                StatusTitle = "Downloading update",
                StatusDetail = $"Downloading AudioBit {targetVersion} in the background.",
                TargetVersion = targetVersion,
                ReleaseSummary = null,
                IsRestartDialogVisible = false,
                IsUpdateSummaryVisible = false,
            });

            await updateManager.DownloadUpdatesAsync(updateInfo, _ => { }, linkedToken).ConfigureAwait(false);

            _pendingUpdate = updateInfo.TargetFullRelease;
            var releaseSummary = BuildReleaseSummary(updateInfo.TargetFullRelease);
            UpdateStatus(snapshot => snapshot with
            {
                State = AppUpdateState.RestartRequired,
                StatusTitle = "Restart required",
                StatusDetail = $"AudioBit needs to restart to finish installing version {targetVersion}.",
                TargetVersion = targetVersion,
                ReleaseSummary = releaseSummary,
                IsRestartDialogVisible = true,
                IsUpdateSummaryVisible = !string.IsNullOrWhiteSpace(releaseSummary),
            });
        }
        catch (OperationCanceledException) when (linkedToken.IsCancellationRequested)
        {
            Log("Updater initialization cancelled.");
        }
        catch (Exception ex)
        {
            Log($"Update check failed: {ex}");
            UpdateStatus(snapshot => snapshot with
            {
                State = AppUpdateState.Failed,
                StatusTitle = "Update check failed",
                StatusDetail = "Update check failed. AudioBit will try again next launch.",
                IsRestartDialogVisible = false,
            });
        }
    }

    public void RestartNow()
    {
        ThrowIfDisposed();

        var updateToApply = _pendingUpdate;
        if (updateToApply is null)
        {
            Log("Restart requested with no pending update.");
            return;
        }

        try
        {
            var updateManager = CreateUpdateManager();
            updateManager.ApplyUpdatesAndRestart(updateToApply, Array.Empty<string>());
        }
        catch (Exception ex)
        {
            Log($"Failed to apply update: {ex}");
            UpdateStatus(snapshot => snapshot with
            {
                State = AppUpdateState.Failed,
                StatusTitle = "Update install failed",
                StatusDetail = "AudioBit could not apply the downloaded update. It will try again next launch.",
                IsRestartDialogVisible = false,
            });
        }
    }

    public void RestartLater()
    {
        ThrowIfDisposed();

        UpdateStatus(snapshot => snapshot with
        {
            IsRestartDialogVisible = false,
        });
    }

    public string BuildDebugReport()
    {
        ThrowIfDisposed();

        var status = CurrentStatus;
        var baseDirectory = NormalizeDirectory(AppContext.BaseDirectory);
        var parentDirectory = Directory.GetParent(baseDirectory)?.FullName;
        var sqVersionPath = Path.Combine(baseDirectory, "sq.version");
        var updateExePath = string.IsNullOrWhiteSpace(parentDirectory)
            ? string.Empty
            : Path.Combine(parentDirectory, "Update.exe");
        var updaterLogPath = Path.Combine(AudioBitPaths.LogsDirectoryPath, "app-updater.log");
        var report = new List<string>
        {
            "AudioBit updater debug report",
            $"GeneratedAtUtc: {DateTime.UtcNow:O}",
            $"RepositoryUrl: {UpdateRepositoryUrl}",
            $"CurrentVersion: {status.CurrentVersion}",
            $"DisplayVersion: {status.DisplayVersion}",
            $"InstallKind: {status.InstallKind}",
            $"State: {status.State}",
            $"StatusTitle: {status.StatusTitle}",
            $"StatusDetail: {status.StatusDetail}",
            $"TargetVersion: {status.TargetVersion ?? "(none)"}",
            $"BaseDirectory: {baseDirectory}",
            $"SqVersionPath: {sqVersionPath}",
            $"SqVersionExists: {File.Exists(sqVersionPath)}",
            $"UpdateExePath: {(string.IsNullOrWhiteSpace(updateExePath) ? "(none)" : updateExePath)}",
            $"UpdateExeExists: {!string.IsNullOrWhiteSpace(updateExePath) && File.Exists(updateExePath)}",
            $"UpdaterLogPath: {updaterLogPath}",
            string.Empty,
            "Recent updater log tail:",
        };

        foreach (var line in ReadLogTail(updaterLogPath, DebugLogTailLineCount))
        {
            report.Add(line);
        }

        return string.Join(Environment.NewLine, report);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    private static AppInstallKind DetectInstallKind()
    {
        var baseDirectory = NormalizeDirectory(AppContext.BaseDirectory);
        var velopackMarkerPath = Path.Combine(baseDirectory, "sq.version");
        var parentDirectory = Directory.GetParent(baseDirectory)?.FullName;
        var updateExePath = string.IsNullOrWhiteSpace(parentDirectory)
            ? string.Empty
            : Path.Combine(parentDirectory, "Update.exe");

        if (File.Exists(velopackMarkerPath) && File.Exists(updateExePath))
        {
            return AppInstallKind.Velopack;
        }

        var legacyInstallDirectory = NormalizeDirectory(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                LegacyInstallFolderName));

        if (baseDirectory.StartsWith(legacyInstallDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return AppInstallKind.LegacyInstaller;
        }

        return AppInstallKind.Development;
    }

    private static string NormalizeDirectory(string path)
    {
        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static IReadOnlyList<string> ReadLogTail(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new[] { "(log file not found)" };
            }

            var allLines = File.ReadAllLines(path);
            if (allLines.Length == 0)
            {
                return new[] { "(log file is empty)" };
            }

            var startIndex = Math.Max(0, allLines.Length - maxLines);
            return allLines[startIndex..];
        }
        catch (Exception ex)
        {
            return new[] { $"(failed to read updater log: {ex.Message})" };
        }
    }

    private static AppUpdateStatusSnapshot CreateInitialStatus(string currentVersion, string displayVersion, AppInstallKind installKind)
    {
        return installKind switch
        {
            AppInstallKind.Velopack => new AppUpdateStatusSnapshot(
                currentVersion,
                displayVersion,
                installKind,
                AppUpdateState.Idle,
                "Launch check queued",
                "Checking for updates on launch.",
                null,
                null,
                false,
                false),
            AppInstallKind.LegacyInstaller => new AppUpdateStatusSnapshot(
                currentVersion,
                displayVersion,
                installKind,
                AppUpdateState.Unsupported,
                "Updater unavailable",
                "Auto-update is unavailable on legacy installs. Install the Velopack release to enable updates.",
                null,
                null,
                false,
                false),
            _ => new AppUpdateStatusSnapshot(
                currentVersion,
                displayVersion,
                installKind,
                AppUpdateState.Unsupported,
                "Updater unavailable",
                "Updater unavailable in development builds.",
                null,
                null,
                false,
                false),
        };
    }

    private static string BuildReleaseSummary(VelopackAsset release)
    {
        var summary = ExtractSummary(release.NotesMarkdown);
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = ExtractSummary(release.NotesHTML);
        }

        return string.IsNullOrWhiteSpace(summary)
            ? "A newer AudioBit release is ready to install."
            : summary;
    }

    private static string? ExtractSummary(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        foreach (var paragraph in ParagraphSplitRegex.Split(notes))
        {
            var cleaned = CleanReleaseNotesParagraph(paragraph);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            return TrimToLength(cleaned, 240);
        }

        var fallback = CleanReleaseNotesParagraph(notes);
        return string.IsNullOrWhiteSpace(fallback) ? null : TrimToLength(fallback, 240);
    }

    private static string CleanReleaseNotesParagraph(string value)
    {
        var normalized = value.Replace("\r", "\n", StringComparison.Ordinal);
        normalized = MarkdownImageRegex.Replace(normalized, " ");
        normalized = MarkdownLinkRegex.Replace(normalized, "${label}");
        normalized = HtmlTagRegex.Replace(normalized, " ");
        normalized = MarkdownHeadingRegex.Replace(normalized, string.Empty);
        normalized = MarkdownListRegex.Replace(normalized, string.Empty);
        normalized = MarkdownDecorationRegex.Replace(normalized, string.Empty);
        normalized = WhitespaceRegex.Replace(normalized, " ");
        return normalized.Trim();
    }

    private static string TrimToLength(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength - 3)].TrimEnd() + "...";
    }

    private static UpdateManager CreateUpdateManager()
    {
        var source = new GithubSource(UpdateRepositoryUrl, string.Empty, false, new HttpClientFileDownloader());
        return new UpdateManager(source, new UpdateOptions(), null);
    }

    private void UpdateStatus(Func<AppUpdateStatusSnapshot, AppUpdateStatusSnapshot> transform)
    {
        AppUpdateStatusSnapshot nextStatus;

        lock (_statusGate)
        {
            nextStatus = transform(_currentStatus);
            _currentStatus = nextStatus;
        }

        StatusChanged?.Invoke(nextStatus);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private void Log(string message)
    {
        var logLine = $"[{DateTime.Now:HH:mm:ss.fff}] [AppUpdater] {message}";
        Debug.WriteLine(logLine);

        try
        {
            lock (LogFileGate)
            {
                Directory.CreateDirectory(AudioBitPaths.LogsDirectoryPath);
                File.AppendAllText(Path.Combine(AudioBitPaths.LogsDirectoryPath, "app-updater.log"), logLine + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never break control flow.
        }
    }
}
