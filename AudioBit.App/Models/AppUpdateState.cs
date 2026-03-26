namespace AudioBit.App.Models;

internal enum AppUpdateState
{
    Unsupported,
    Idle,
    Checking,
    UpToDate,
    Downloading,
    RestartRequired,
    Failed,
}
