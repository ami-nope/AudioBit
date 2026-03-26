using AudioBit.Core;
using Xunit;

namespace AudioBit.Installer.Tests;

public sealed class AppVersionInfoTests
{
    [Theory]
    [InlineData("1.1", "1.1.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.10", "1.10.0")]
    [InlineData("1.2.3", "1.2.3")]
    public void NormalizeForDisplay_NormalizesReleaseVersions(string input, string expected)
    {
        Assert.Equal(expected, AppVersionInfo.NormalizeForDisplay(input));
    }

    [Fact]
    public void VersionComparison_UsesNumericOrdering()
    {
        Assert.True(Version.Parse("1.2") > Version.Parse("1.1"));
        Assert.True(Version.Parse("1.10") > Version.Parse("1.2"));
    }
}
