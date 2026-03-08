using System.IO;

namespace AudioBit.App.Infrastructure;

internal static class AudioBitPaths
{
    public static string DataDirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioBit");

    public static string SettingsFilePath => Path.Combine(DataDirectoryPath, "settings.json");

    public static string LogsDirectoryPath => Path.Combine(DataDirectoryPath, "Logs");
}
