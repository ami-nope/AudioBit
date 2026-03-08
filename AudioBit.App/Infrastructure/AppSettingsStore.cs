using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioBit.App.Models;

namespace AudioBit.App.Infrastructure;

internal sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter(),
        },
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public AppSettingsSnapshot Load()
    {
        return LoadFrom(AudioBitPaths.SettingsFilePath, swallowErrors: true) ?? new AppSettingsSnapshot();
    }

    public void Save(AppSettingsSnapshot snapshot)
    {
        SaveTo(AudioBitPaths.SettingsFilePath, snapshot);
    }

    public AppSettingsSnapshot LoadFrom(string path)
    {
        return LoadFrom(path, swallowErrors: false) ?? new AppSettingsSnapshot();
    }

    public void SaveTo(string path, AppSettingsSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
        File.WriteAllText(path, json);
    }

    private static AppSettingsSnapshot? LoadFrom(string path, bool swallowErrors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettingsSnapshot>(json, SerializerOptions) ?? new AppSettingsSnapshot();
        }
        catch when (swallowErrors)
        {
            return null;
        }
    }
}
