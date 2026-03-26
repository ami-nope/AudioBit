namespace AudioBit.App.Models;

internal sealed record AppUpdateStatusSnapshot(
    string CurrentVersion,
    string DisplayVersion,
    AppInstallKind InstallKind,
    AppUpdateState State,
    string StatusTitle,
    string StatusDetail,
    string? TargetVersion,
    string? ReleaseSummary,
    bool IsRestartDialogVisible,
    bool IsUpdateSummaryVisible);
