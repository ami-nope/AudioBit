using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AudioBit.App.Models;

namespace AudioBit.App.Infrastructure;

public sealed class SpotifyAuthStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _filePath;

    public SpotifyAuthStateStore(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? AudioBitPaths.SpotifyAuthStateFilePath
            : filePath;
    }

    public SpotifyTokenState? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var protectedBytes = File.ReadAllBytes(_filePath);
            var jsonBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<SpotifyTokenState>(jsonBytes, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(SpotifyTokenState tokenState)
    {
        ArgumentNullException.ThrowIfNull(tokenState);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(tokenState, SerializerOptions);
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(jsonBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_filePath, protectedBytes);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        catch
        {
            // Auth persistence should never crash the shell.
        }
    }
}
