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
    private MainViewModel? _mainViewModel;
    private AppSettingsStore? _appSettingsStore;
    private StartupRegistrationService? _startupRegistrationService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        _audioSessionService = new AudioSessionService();
        _remoteClientService = new RemoteClientService(_audioSessionService);
        var qrCodeService = new QrCodeService();
        _appSettingsStore = new AppSettingsStore();
        _startupRegistrationService = new StartupRegistrationService();
        _mainViewModel = new MainViewModel(_audioSessionService, _remoteClientService, qrCodeService, _appSettingsStore, _startupRegistrationService);

        var mainWindow = new MainWindow(_mainViewModel);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.Dispose();
        _remoteClientService?.Dispose();
        _audioSessionService?.Dispose();
        base.OnExit(e);
    }
}
