using System.Windows;
using AudioBit.App.ViewModels;
using AudioBit.Core;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace AudioBit.App;

public partial class App : Application
{
    private AudioSessionService? _audioSessionService;
    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, updateAccent: true);

        _audioSessionService = new AudioSessionService();
        _mainViewModel = new MainViewModel(_audioSessionService);

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
