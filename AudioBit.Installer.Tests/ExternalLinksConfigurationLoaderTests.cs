using AudioBit.Core;
using Xunit;

namespace AudioBit.Installer.Tests;

public sealed class ExternalLinksConfigurationLoaderTests
{
    [Fact]
    public void TryParseJson_UsesConfiguredUrls()
    {
        const string json = """
            {
              "relay": {
                "http_base_url": "https://relay.example.com/",
                "ws_url": "wss://relay.example.com/ws",
                "backup_http_base_url": "https://backup-relay.example.com/",
                "backup_ws_url": "wss://backup-relay.example.com/ws"
              },
              "remote_web": {
                "connect_base_url": "https://remote.example.com/connect"
              },
              "services": {
                "geo_ip_lookup_url_template": "https://geo.example.com/{ip}"
              },
              "project": {
                "about_url": "https://example.com/about"
              }
            }
            """;

        var configuration = ExternalLinksConfigurationLoader.TryParseJson(json, "test");

        Assert.NotNull(configuration);
        Assert.Equal("https://relay.example.com/", configuration!.RelayHttpBaseUri.AbsoluteUri);
        Assert.Equal("wss://relay.example.com/ws", configuration.RelayWebSocketUri.AbsoluteUri);
        Assert.NotNull(configuration.BackupRelayHttpBaseUri);
        Assert.NotNull(configuration.BackupRelayWebSocketUri);
        Assert.Equal("https://backup-relay.example.com/", configuration.BackupRelayHttpBaseUri!.AbsoluteUri);
        Assert.Equal("wss://backup-relay.example.com/ws", configuration.BackupRelayWebSocketUri!.AbsoluteUri);
        Assert.Equal("https://remote.example.com/connect", configuration.RemoteConnectBaseUri.AbsoluteUri);
        Assert.Equal("https://geo.example.com/{ip}", configuration.GeoIpLookupUrlTemplate);
        Assert.Equal("https://example.com/about", configuration.AboutUri.AbsoluteUri);
        Assert.Equal("test", configuration.Source);
    }

    [Fact]
    public void TryParseJson_DerivesRelayWebSocketUrlFromHttpBaseUrl()
    {
        const string json = """
            {
              "relay": {
                "http_base_url": "https://relay.example.com/"
              }
            }
            """;

        var configuration = ExternalLinksConfigurationLoader.TryParseJson(json, "test");

        Assert.NotNull(configuration);
        Assert.Equal("https://relay.example.com/", configuration!.RelayHttpBaseUri.AbsoluteUri);
        Assert.Equal("wss://relay.example.com/ws", configuration.RelayWebSocketUri.AbsoluteUri);
        Assert.Null(configuration.BackupRelayHttpBaseUri);
        Assert.Null(configuration.BackupRelayWebSocketUri);
    }

    [Fact]
    public void TryParseJson_DerivesBackupRelayWebSocketUrlFromBackupHttpBaseUrl()
    {
        const string json = """
            {
              "relay": {
                "backup_http_base_url": "https://backup-relay.example.com/"
              }
            }
            """;

        var configuration = ExternalLinksConfigurationLoader.TryParseJson(json, "test");

        Assert.NotNull(configuration);
        Assert.NotNull(configuration!.BackupRelayHttpBaseUri);
        Assert.NotNull(configuration.BackupRelayWebSocketUri);
        Assert.Equal("https://backup-relay.example.com/", configuration.BackupRelayHttpBaseUri!.AbsoluteUri);
        Assert.Equal("wss://backup-relay.example.com/ws", configuration.BackupRelayWebSocketUri!.AbsoluteUri);
    }

    [Fact]
    public void TryParseJson_ReturnsNullForInvalidJson()
    {
        var configuration = ExternalLinksConfigurationLoader.TryParseJson("{ relay: ", "test");

        Assert.Null(configuration);
    }
}
