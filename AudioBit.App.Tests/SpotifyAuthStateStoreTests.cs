using System.IO;
using AudioBit.App.Infrastructure;
using AudioBit.App.Models;
using Xunit;

namespace AudioBit.App.Tests;

public sealed class SpotifyAuthStateStoreTests
{
    [Fact]
    public void SaveLoadAndClear_RoundTripsProtectedTokenState()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "AudioBit.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var filePath = Path.Combine(tempDirectory, "spotify-auth.bin");
        var store = new SpotifyAuthStateStore(filePath);
        var expected = new SpotifyTokenState
        {
            ClientId = "client-id",
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            Scope = "user-read-playback-state",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30),
        };

        try
        {
            store.Save(expected);
            var loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal(expected.ClientId, loaded!.ClientId);
            Assert.Equal(expected.AccessToken, loaded.AccessToken);
            Assert.Equal(expected.RefreshToken, loaded.RefreshToken);
            Assert.Equal(expected.Scope, loaded.Scope);

            store.Clear();
            Assert.Null(store.Load());
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
}
