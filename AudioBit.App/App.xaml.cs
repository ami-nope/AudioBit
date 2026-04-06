using System.IO;
using System.Windows;
using AudioBit.App.Infrastructure;
using AudioBit.App.Services;
using AudioBit.App.ViewModels;
using AudioBit.Core;
using AudioBit.Core.Diagnostics;

namespace AudioBit.App;

public partial class App : Application
{
    private AudioSessionService? _audioSessionService;
    private RemoteClientService? _remoteClientService;
    private AppUpdaterService? _appUpdaterService;
    private DiscordRpcService? _discordRpcService;
    private GoogleSheetsLogSyncService? _googleSheetsLogSyncService;
    private MainViewModel? _mainViewModel;
    private AppSettingsStore? _appSettingsStore;
    private StartupRegistrationService? _startupRegistrationService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = false;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogCrash("UnhandledException", ex);
            }
        };

        try
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            _googleSheetsLogSyncService = new GoogleSheetsLogSyncService();
            AppLog.Info("App", "AudioBit startup sequence initialized.");
            AppLog.Trace("App", $"Log directory: {AudioBitPaths.LogsDirectoryPath}");

            var externalLinks = ExternalLinksConfigurationLoader.Load(
                localFallbackPath: Path.Combine(AppContext.BaseDirectory, "external-links.json"));
            AppLog.Trace("App", $"External links loaded from '{externalLinks.Source}'.");
            _audioSessionService = new AudioSessionService();
            AppLog.Trace("App", "AudioSessionService created.");
            _remoteClientService = new RemoteClientService(_audioSessionService, externalLinks);
            AppLog.Trace("App", "RemoteClientService created.");
            _appUpdaterService = new AppUpdaterService();
            AppLog.Trace("App", "AppUpdaterService created.");
            var qrCodeService = new QrCodeService(externalLinks);
            AppLog.Trace("App", "QrCodeService created.");
            _appSettingsStore = new AppSettingsStore();
            AppLog.Trace("App", "AppSettingsStore created.");
            _startupRegistrationService = new StartupRegistrationService();
            AppLog.Trace("App", "StartupRegistrationService created.");
            var spotifyAuthStateStore = new SpotifyAuthStateStore();
            var spotifyService = new SpotifyService(
                spotifyAuthStateStore,
                NetworkClientFactory.CreateHttpClient(TimeSpan.FromSeconds(12)));
            var spotifyClientId = SpotifyClientIdResolver.Resolve(_appSettingsStore, spotifyAuthStateStore);
            var spotifyViewModel = new SpotifyViewModel(spotifyService, spotifyClientId);
            AppLog.Trace("App", $"Spotify services initialized. configured={!string.IsNullOrWhiteSpace(spotifyClientId)}");

            // Discord RPC setup.
            var discordAuthStateStore = new DiscordAuthStateStore();
            var discordClientId = DiscordClientIdResolver.ResolveClientId();
            var discordClientSecret = DiscordClientIdResolver.ResolveClientSecret();
            var discordRedirectUri = DiscordClientIdResolver.ResolveRedirectUri();
            _discordRpcService = new DiscordRpcService(
                discordAuthStateStore,
                discordClientId,
                discordClientSecret,
                discordRedirectUri);
            var discordViewModel = new DiscordViewModel(_discordRpcService);
            AppLog.Trace("App", $"Discord services initialized. configured={!string.IsNullOrWhiteSpace(discordClientId) && !string.IsNullOrWhiteSpace(discordClientSecret) && !string.IsNullOrWhiteSpace(discordRedirectUri)}");

            _mainViewModel = new MainViewModel(
                _audioSessionService,
                _remoteClientService,
                qrCodeService,
                _appSettingsStore,
                _startupRegistrationService,
                _appUpdaterService,
                _googleSheetsLogSyncService,
                spotifyViewModel,
                discordViewModel);

            var mainWindow = new MainWindow(_mainViewModel);
            MainWindow = mainWindow;
            mainWindow.Show();
            AppLog.Info("App", "AudioBit main window shown.");
        }
        catch (Exception ex)
        {
            LogCrash("OnStartup", ex);
            MessageBox.Show($"AudioBit failed to start:\n\n{ex}", "AudioBit Crash", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("App", $"AudioBit shutdown requested (exitCode={e.ApplicationExitCode}).");
        AppLog.Trace("App", "Disposing application services.");
        _mainViewModel?.Dispose();
        _appUpdaterService?.Dispose();
        _discordRpcService?.Dispose();
        _remoteClientService?.Dispose();
        _audioSessionService?.Dispose();
        _googleSheetsLogSyncService?.Dispose();
        base.OnExit(e);
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            AppLog.Error("App", $"Unhandled exception ({source})", ex);
        }
        catch
        {
        }

        try
        {
            var logDir = AudioBitPaths.LogsDirectoryPath;
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, "crash.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] [{source}] {ex}\n\n");
        }
        catch
        {
        }
    }
}
