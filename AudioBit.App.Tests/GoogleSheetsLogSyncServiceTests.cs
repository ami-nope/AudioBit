using System.Reflection;
using AudioBit.App.Services;
using Xunit;

namespace AudioBit.App.Tests;

public sealed class GoogleSheetsLogSyncServiceTests
{
    [Fact]
    public void FormatTimestampForGoogleSheets_UsesIstAndExpectedFormat()
    {
        var serviceType = typeof(SpotifyService).Assembly.GetType("AudioBit.App.Services.GoogleSheetsLogSyncService", throwOnError: true);
        var formatter = serviceType!.GetMethod("FormatTimestampForGoogleSheets", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(formatter);

        var formatted = formatter!.Invoke(null, new object[] { new DateTimeOffset(2026, 4, 18, 1, 40, 0, TimeSpan.Zero) }) as string;

        Assert.Equal("18-Apr-26 || 07:10 AM", formatted);
    }
}
