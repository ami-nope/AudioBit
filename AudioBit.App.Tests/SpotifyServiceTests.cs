using System.IO;
using System.Net;
using System.Net.Http;
using AudioBit.App.Infrastructure;
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

    [Fact]
    public void ParsePlaybackStateJson_MapsPlayingDeviceStateWithoutTrack()
    {
        const string json = """
                            {
                              "device": { "name": "Living Room Speaker" },
                              "is_playing": true,
                              "progress_ms": 0,
                              "item": null
                            }
                            """;

        var snapshot = SpotifyService.ParsePlaybackStateJson(json);

        Assert.Equal(SpotifyConnectionState.Playing, snapshot.ConnectionState);
        Assert.True(snapshot.HasActiveDevice);
        Assert.True(snapshot.CanControlPlayback);
        Assert.Null(snapshot.Track);
        Assert.Equal("Playing on Spotify", snapshot.StatusText);
        Assert.Equal("Living Room Speaker", snapshot.DeviceName);
    }

    [Fact]
    public async Task InitializeAsync_KeepsRecoverableStateWhenTokenRefreshIsTemporarilyUnavailable()
    {
        var clientId = "0123456789abcdef0123456789abcdef";
        var tokenState = new SpotifyTokenState
        {
            ClientId = clientId,
            AccessToken = "expired-access-token",
            RefreshToken = "refresh-token",
            Scope = "scope",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
        };

        using var context = CreateServiceContext(
            tokenState,
            static request => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        await context.Service.InitializeAsync(clientId, CancellationToken.None);

        Assert.Equal(SpotifyConnectionState.Error, context.Service.CurrentSnapshot.ConnectionState);
        Assert.True(context.Service.CurrentSnapshot.IsAuthenticated);
        Assert.Equal("Reconnecting to Spotify...", context.Service.CurrentSnapshot.StatusText);
    }

    [Fact]
    public async Task StartPollingAsync_AuthExpiryDuringRefreshDoesNotHangPolling()
    {
        var clientId = "0123456789abcdef0123456789abcdef";
        var tokenState = new SpotifyTokenState
        {
            ClientId = clientId,
            AccessToken = "access-token",
            RefreshToken = "refresh-token",
            Scope = "scope",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
        };

        using var context = CreateServiceContext(
            tokenState,
            static request =>
            {
                if (request.RequestUri?.AbsoluteUri == "https://api.spotify.com/v1/me/player/currently-playing")
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                if (request.RequestUri?.AbsoluteUri == "https://accounts.spotify.com/api/token")
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var authExpired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Service.PlaybackStateChanged += (_, snapshot) =>
        {
            if (snapshot.ConnectionState == SpotifyConnectionState.AuthExpired)
            {
                authExpired.TrySetResult();
            }
        };

        await context.Service.InitializeAsync(clientId, CancellationToken.None);
        await context.Service.StartPollingAsync(CancellationToken.None);
        await authExpired.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await context.Service.StopPollingAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SpotifyConnectionState.AuthExpired, context.Service.CurrentSnapshot.ConnectionState);
    }

    private static ServiceContext CreateServiceContext(
        SpotifyTokenState tokenState,
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"AudioBit.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        var store = new SpotifyAuthStateStore(Path.Combine(tempDirectory, "spotify-auth.bin"));
        store.Save(tokenState);

        var handler = new CallbackHttpMessageHandler(responseFactory);
        var service = new SpotifyService(store, new HttpClient(handler));
        return new ServiceContext(tempDirectory, service);
    }

    private sealed class ServiceContext(string tempDirectory, SpotifyService service) : IDisposable
    {
        public string TempDirectory { get; } = tempDirectory;

        public SpotifyService Service { get; } = service;

        public void Dispose()
        {
            Service.Dispose();

            try
            {
                if (Directory.Exists(TempDirectory))
                {
                    Directory.Delete(TempDirectory, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    private sealed class CallbackHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory = responseFactory;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responseFactory(request));
        }
    }
}
