using System.IO;
using System.Net.Http;
using System.Windows;
using AudioBit.App.Infrastructure;
using AudioBit.App.Services;
using AudioBit.App.ViewModels;
using AudioBit.Core;

namespace AudioBit.App;

public partial class App : Application
{
    private AudioSessionService? _audioSessionService;
    private RemoteClientService? _remoteClientService;
    private AppUpdaterService? _appUpdaterService;
    private MainViewModel? _mainViewModel;
    private AppSettingsStore? _appSettingsStore;
    private StartupRegistrationService? _startupRegistrationService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        var externalLinks = ExternalLinksConfigurationLoader.Load(
            localFallbackPath: Path.Combine(AppContext.BaseDirectory, "external-links.json"));
        _audioSessionService = new AudioSessionService();
        _remoteClientService = new RemoteClientService(_audioSessionService, externalLinks);
        _appUpdaterService = new AppUpdaterService();
        var qrCodeService = new QrCodeService(externalLinks);
        _appSettingsStore = new AppSettingsStore();
        _startupRegistrationService = new StartupRegistrationService();
        var spotifyAuthStateStore = new SpotifyAuthStateStore();
        var spotifyService = new SpotifyService(spotifyAuthStateStore, new HttpClient { Timeout = TimeSpan.FromSeconds(12) });
        var spotifyClientId = SpotifyClientIdResolver.Resolve(_appSettingsStore, spotifyAuthStateStore);
        var spotifyViewModel = new SpotifyViewModel(spotifyService, spotifyClientId);
        _mainViewModel = new MainViewModel(
            _audioSessionService,
            _remoteClientService,
            qrCodeService,
            _appSettingsStore,
            _startupRegistrationService,
            _appUpdaterService,
            spotifyViewModel);

        var mainWindow = new MainWindow(_mainViewModel);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.Dispose();
        _appUpdaterService?.Dispose();
        _remoteClientService?.Dispose();
        _audioSessionService?.Dispose();
        base.OnExit(e);
    }
}
