using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AudioBit.App.Models;

namespace AudioBit.App.Infrastructure;

internal sealed class DiscordAuthStateStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly string _filePath;

    public DiscordAuthStateStore(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? AudioBitPaths.DiscordAuthStateFilePath
            : filePath;
    }

    public DiscordTokenState? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var protectedBytes = File.ReadAllBytes(_filePath);
            var jsonBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<DiscordTokenState>(jsonBytes, SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Save(DiscordTokenState tokenState)
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
            
        }
    }
}
