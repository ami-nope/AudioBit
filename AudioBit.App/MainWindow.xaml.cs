using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AudioBit.App.Infrastructure;
using AudioBit.App.ViewModels;
using Wpf.Ui.Controls;

namespace AudioBit.App;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;
    private readonly AudioBitNotifyIconService _notifyIconService;

    private bool _hasStarted;
    private bool _isExiting;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = _viewModel;
        Icon = CreateWindowIcon();

        _notifyIconService = CreateNotifyIconService();

        Loaded += MainWindowOnLoaded;
        StateChanged += MainWindowOnStateChanged;
        Closing += MainWindowOnClosing;
        Closed += MainWindowOnClosed;
    }

    private void MainWindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hasStarted)
        {
            return;
        }

        _hasStarted = true;
        _notifyIconService.SetParentWindow(this);
        _viewModel.Start();
    }

    private void MainWindowOnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _viewModel.HideToTrayOnMinimize)
        {
            HideToTray();
        }
    }

    private void MainWindowOnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            _viewModel.Stop();
        }

        if (_notifyIconService.IsRegistered)
        {
            _notifyIconService.Unregister();
        }
    }

    private void MainWindowOnClosed(object? sender, EventArgs e)
    {
        _notifyIconService.LeftDoubleClickReceived -= NotifyIconServiceOnLeftDoubleClickReceived;
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();

        if (!_notifyIconService.IsRegistered)
        {
            _notifyIconService.Register();
        }
    }

    private void RestoreFromTray()
    {
        if (_notifyIconService.IsRegistered)
        {
            _notifyIconService.Unregister();
        }

        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _isExiting = true;
        Close();
    }

    private AudioBitNotifyIconService CreateNotifyIconService()
    {
        var service = new AudioBitNotifyIconService
        {
            TooltipText = "AudioBit",
            Icon = CreateTrayIcon(),
            ContextMenu = BuildTrayMenu(),
        };

        service.LeftDoubleClickReceived += NotifyIconServiceOnLeftDoubleClickReceived;
        return service;
    }

    private ContextMenu BuildTrayMenu()
    {
        var contextMenu = new ContextMenu();

        var openItem = new System.Windows.Controls.MenuItem { Header = "Open AudioBit" };
        openItem.Click += (_, _) => RestoreFromTray();

        var muteAllItem = new System.Windows.Controls.MenuItem { Header = "Mute All" };
        muteAllItem.Click += (_, _) => _viewModel.ToggleMuteAllCommand.Execute(null);

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitFromTray();

        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(muteAllItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(exitItem);

        return contextMenu;
    }

    private void NotifyIconServiceOnLeftDoubleClickReceived(object? sender, EventArgs e)
    {
        RestoreFromTray();
    }

    private static BitmapFrame CreateTrayIcon()
    {
        using var icon = System.Drawing.SystemIcons.Application;

        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(32, 32));

        source.Freeze();

        var frame = BitmapFrame.Create(source);
        frame.Freeze();
        return frame;
    }

    private static BitmapSource CreateWindowIcon()
    {
        using var icon = System.Drawing.SystemIcons.Application;

        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(32, 32));

        source.Freeze();
        return source;
    }
}
