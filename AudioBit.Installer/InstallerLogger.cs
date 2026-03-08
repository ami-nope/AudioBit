using System.IO;

namespace AudioBit.Installer;

internal static class InstallerLogger
{
    private static readonly object SyncRoot = new();

    public static void Log(string message)
    {
        try
        {
            var logPath = InstallerPaths.GetInstallerLogPath();
            var logDirectory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrWhiteSpace(logDirectory))
            {
                Directory.CreateDirectory(logDirectory);
            }

            lock (SyncRoot)
            {
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
