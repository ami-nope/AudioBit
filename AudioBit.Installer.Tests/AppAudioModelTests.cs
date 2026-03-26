using AudioBit.Core;
using Xunit;

namespace AudioBit.Installer.Tests;

public sealed class AppAudioModelTests
{
    [Theory]
    [InlineData("Discord.exe", "Discord")]
    [InlineData("Spotify", "Spotify")]
    [InlineData("  chrome.exe  ", "chrome")]
    public void CreateIdentityKey_NormalizesExecutableNames(string appName, string expected)
    {
        var key = AppAudioModel.CreateIdentityKey(appName);

        Assert.Equal(expected, key);
    }

    [Fact]
    public void Opacity_RemainsFullyVisibleForPinnedSilentApps()
    {
        var model = new AppAudioModel
        {
            AppName = "Discord",
            Volume = 1.0f,
            Peak = 0.0f,
            IsMuted = false,
            IsPinned = true,
            LastAudioTime = DateTime.UtcNow - TimeSpan.FromMinutes(1),
        };

        Assert.Equal(1.0, model.Opacity);
    }
}
