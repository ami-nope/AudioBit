using System.Windows;
using AudioBit.App.Infrastructure;
using AudioBit.App.ViewModels;
using AudioBit.Core;

namespace AudioBit.App;

public partial class App : Application
{
    private AudioSessionService? _audioSessionService;
    private MainViewModel? _mainViewModel;
    private AppSettingsStore? _appSettingsStore;
    private StartupRegistrationService? _startupRegistrationService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        _audioSessionService = new AudioSessionService();
        _appSettingsStore = new AppSettingsStore();
        _startupRegistrationService = new StartupRegistrationService();
        _mainViewModel = new MainViewModel(_audioSessionService, _appSettingsStore, _startupRegistrationService);

        var mainWindow = new MainWindow(_mainViewModel);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.Dispose();
        _audioSessionService?.Dispose();
        base.OnExit(e);
    }
}
