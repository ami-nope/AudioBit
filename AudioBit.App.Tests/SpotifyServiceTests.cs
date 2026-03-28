using System.Net;
using System.Net.Http;
using AudioBit.App.Models;
using AudioBit.App.Services;
using Xunit;

namespace AudioBit.App.Tests;

public sealed class SpotifyServiceTests
{
    [Fact]
    public void CreateCodeVerifier_ReturnsUrlSafeVerifier()
    {
        var verifier = SpotifyService.CreateCodeVerifier();

        Assert.InRange(verifier.Length, 43, 128);
        Assert.DoesNotContain("=", verifier);
        Assert.DoesNotContain("+", verifier);
        Assert.DoesNotContain("/", verifier);
    }

    [Fact]
    public void CreateCodeChallenge_ReturnsExpectedHash()
    {
        var challenge = SpotifyService.CreateCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", challenge);
    }

    [Fact]
    public void TryParseAuthorizeCallback_ReturnsCodeForMatchingState()
    {
        var uri = new Uri("http://127.0.0.1:43871/spotify/callback/?code=abc123&state=expected-state");

        var result = SpotifyService.TryParseAuthorizeCallback(uri, "expected-state", out var code, out var errorDescription);

        Assert.True(result);
        Assert.Equal("abc123", code);
        Assert.Null(errorDescription);
    }

    [Fact]
    public void TryParseAuthorizeCallback_RejectsStateMismatch()
    {
        var uri = new Uri("http://127.0.0.1:43871/spotify/callback/?code=abc123&state=wrong");

        var result = SpotifyService.TryParseAuthorizeCallback(uri, "expected-state", out _, out var errorDescription);

        Assert.False(result);
        Assert.Equal("Spotify sign-in failed.", errorDescription);
    }

    [Fact]
    public void TryParseRetryAfterSeconds_ReadsResponseHeader()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.Add("Retry-After", "5");

        var parsed = SpotifyService.TryParseRetryAfterSeconds(response, out var retryAfterSeconds);

        Assert.True(parsed);
        Assert.Equal(5, retryAfterSeconds);
    }

    [Fact]
    public void ParsePlaybackStateJson_MapsPlayingTrack()
    {
        const string json = """
                            {
                              "device": { "name": "Desktop" },
                              "is_playing": true,
                              "progress_ms": 30000,
                              "item": {
                                "id": "track-1",
                                "name": "Midnight City",
                                "duration_ms": 240000,
                                "artists": [{ "name": "M83" }],
                                "album": {
                                  "name": "Hurry Up, We're Dreaming",
                                  "images": [{ "url": "https://image.test/cover.png" }]
                                }
                              }
                            }
                            """;

        var snapshot = SpotifyService.ParsePlaybackStateJson(json);

        Assert.Equal(SpotifyConnectionState.Playing, snapshot.ConnectionState);
        Assert.True(snapshot.HasActiveDevice);
        Assert.True(snapshot.CanControlPlayback);
        Assert.NotNull(snapshot.Track);
        Assert.Equal("Midnight City", snapshot.Track!.TrackName);
        Assert.Equal("M83", snapshot.Track.ArtistName);
        Assert.Equal("https://image.test/cover.png", snapshot.Track.AlbumArtUrl);
        Assert.Equal(30000, snapshot.Track.ProgressMs);
    }

    [Fact]
    public void ParsePlaybackStateJson_MapsPausedTrackWithoutAlbumArt()
    {
        const string json = """
                            {
                              "device": { "name": "Desktop" },
                              "is_playing": false,
                              "progress_ms": 12000,
                              "item": {
                                "id": "track-2",
                                "name": "Numb",
                                "duration_ms": 185000,
                                "artists": [{ "name": "Linkin Park" }],
                                "album": {
                                  "name": "Meteora",
                                  "images": []
                                }
                              }
                            }
                            """;

        var snapshot = SpotifyService.ParsePlaybackStateJson(json);

        Assert.Equal(SpotifyConnectionState.Paused, snapshot.ConnectionState);
        Assert.NotNull(snapshot.Track);
        Assert.Equal(string.Empty, snapshot.Track!.AlbumArtUrl);
    }

    [Fact]
    public void ParsePlaybackStateJson_MapsIdleDeviceState()
    {
        const string json = """
                            {
                              "device": { "name": "Desktop" },
                              "is_playing": false,
                              "progress_ms": 0,
                              "item": null
                            }
                            """;

        var snapshot = SpotifyService.ParsePlaybackStateJson(json);

        Assert.Equal(SpotifyConnectionState.ConnectedIdle, snapshot.ConnectionState);
        Assert.True(snapshot.HasActiveDevice);
        Assert.Null(snapshot.Track);
        Assert.Equal("Nothing playing", snapshot.StatusText);
    }
}
