using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioBit.App.Infrastructure;
using AudioBit.App.ViewModels;

namespace AudioBit.App;

public partial class MainWindow : Window
{
    private const int WindowCornerRadius = 28;

    private readonly MainViewModel _viewModel;
    private readonly AudioBitNotifyIconService _notifyIconService;

    private bool _hasStarted;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = _viewModel;
        Icon = CreateWindowIcon();

        _notifyIconService = CreateNotifyIconService();
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;

        ApplyThemePalette(_viewModel.IsDarkTheme);

        SourceInitialized += MainWindowOnSourceInitialized;
        Loaded += MainWindowOnLoaded;
        SizeChanged += MainWindowOnSizeChanged;
        StateChanged += MainWindowOnStateChanged;
        Closing += MainWindowOnClosing;
        Closed += MainWindowOnClosed;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(
        int nLeftRect,
        int nTopRect,
        int nRightRect,
        int nBottomRect,
        int nWidthEllipse,
        int nHeightEllipse);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    private void MainWindowOnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyRoundedWindowRegion();
        ApplyRoundedVisualClips();
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
        ApplyRoundedWindowRegion();
        ApplyRoundedVisualClips();
    }

    private void MainWindowOnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyRoundedWindowRegion();
        ApplyRoundedVisualClips();
    }

    private void MainWindowOnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _viewModel.HideToTrayOnMinimize)
        {
            HideToTray();
            return;
        }

        ApplyRoundedWindowRegion();
    }

    private void MainWindowOnClosing(object? sender, CancelEventArgs e)
    {
        _viewModel.Stop();

        if (_notifyIconService.IsRegistered)
        {
            _notifyIconService.Unregister();
        }
    }

    private void MainWindowOnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        _notifyIconService.LeftDoubleClickReceived -= NotifyIconServiceOnLeftDoubleClickReceived;
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OverflowButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Bottom;
        button.ContextMenu.IsOpen = true;
    }

    private void OverflowMinimizeItem_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OverflowCloseItem_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
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
        ApplyRoundedWindowRegion();
    }

    private void ExitFromTray()
    {
        Close();
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDarkTheme))
        {
            ApplyThemePalette(_viewModel.IsDarkTheme);
        }
    }

    private void ApplyThemePalette(bool isDarkTheme)
    {
        if (isDarkTheme)
        {
            SetBrush("WindowBackdropBrush", Solid("#0D0F16"));
            SetBrush("ShellBackgroundBrush", Gradient("#2B2B39", "#191A24"));
            SetBrush("ShellBorderBrush", Solid("#4A4B5D"));
            SetBrush("ShellSheenBrush", Gradient("#22FFFFFF", "#00FFFFFF"));
            SetBrush("TopBarBackgroundBrush", Gradient("#343545", "#2A2B38"));
            SetBrush("TopBarBorderBrush", Solid("#45475A"));
            SetBrush("BrandTileBrush", Gradient("#FF9B3E", "#F36D1E"));
            SetBrush("BrandTileBorderBrush", Solid("#FFB676"));
            SetBrush("BrandGlyphBrush", Solid("#FFF7F1"));
            SetBrush("UtilityGroupBrush", Gradient("#3C3C4E", "#323344"));
            SetBrush("UtilityGroupBorderBrush", Solid("#4D4E62"));
            SetBrush("UtilityButtonBrush", Solid("#343546"));
            SetBrush("UtilityButtonHoverBrush", Solid("#404154"));
            SetBrush("UtilityButtonBorderBrush", Solid("#4A4C60"));
            SetBrush("TextPrimaryBrush", Solid("#F4F2F8"));
            SetBrush("TextSecondaryBrush", Solid("#A7A8B7"));
            SetBrush("TextMutedBrush", Solid("#8B8D9E"));
            SetBrush("IconSecondaryBrush", Solid("#9A9DAF"));
            SetBrush("AccentOrangeBrush", Solid("#FF8D33"));
            SetBrush("AccentGreenBrush", Solid("#49BF69"));
            SetBrush("CardBackgroundBrush", Gradient("#2A2A38", "#242532"));
            SetBrush("CardBorderBrush", Solid("#434557"));
            SetBrush("CardHoverOverlayBrush", Solid("#14FFFFFF"));
            SetBrush("IconTileBrush", Solid("#373848"));
            SetBrush("IconTileBorderBrush", Solid("#4A4C60"));
            SetBrush("SliderInactiveBrush", Solid("#181924"));
            SetBrush("SliderThumbBrush", Solid("#FFF6EE"));
            SetBrush("SliderThumbBorderBrush", Solid("#E6CBBE"));
            SetBrush("MutePillBackgroundBrush", Solid("#343546"));
            SetBrush("MutePillBorderBrush", Solid("#4A4B5F"));
            SetBrush("MutePillHoverBrush", Solid("#404154"));
            SetBrush("MutePillActiveBackgroundBrush", Solid("#3C2C23"));
            SetBrush("MutePillActiveBorderBrush", Solid("#A3653C"));
            SetBrush("DividerBrush", Solid("#3A3B4D"));
            SetBrush("FooterBackgroundBrush", Gradient("#252632", "#21222D"));
            SetBrush("FooterBorderBrush", Solid("#3A3B4D"));
            SetBrush("FooterHoverBrush", Solid("#2E303D"));
            SetBrush("FooterActiveBrush", Solid("#FF8D33"));
            SetBrush("FooterInactiveBrush", Solid("#8E90A1"));
            SetBrush("OverlayShadeBrush", Solid("#8C090B12"));
            SetBrush("OverlayPanelBrush", Gradient("#2E2F3D", "#292A37"));
            SetBrush("OverlayPanelBorderBrush", Solid("#4A4C60"));
            SetBrush("OverlaySoftCardBrush", Gradient("#323340", "#2D2E3B"));
            SetBrush("OverlayOptionBrush", Solid("#2B2D39"));
            SetBrush("OverlayOptionBorderBrush", Solid("#4B4D61"));
            SetBrush("OverlayOptionHoverBrush", Solid("#353746"));
            SetBrush("OverlaySelectedBrush", Solid("#3A3D4F"));
            SetBrush("OverlaySelectedBorderBrush", Solid("#6B7088"));
            SetBrush("OverlayPrimaryBrush", Gradient("#FF8D33", "#F07A1F"));
            SetBrush("OverlayPrimaryHoverBrush", Gradient("#FF9947", "#F5862D"));
            SetBrush("OverlayPrimaryBorderBrush", Solid("#FFC38D"));
            SetBrush("ContextMenuBackgroundBrush", Solid("#262735"));
            SetBrush("ContextMenuBorderBrush", Solid("#44455A"));
            SetBrush("ContextMenuHoverBrush", Solid("#333648"));
            SetBrush("ScrollThumbBrush", Solid("#66697C"));
            SetBrush("ScrollThumbHoverBrush", Solid("#81859B"));
            return;
        }

        SetBrush("WindowBackdropBrush", Solid("#F0EEF2"));
        SetBrush("ShellBackgroundBrush", Gradient("#FFFFFF", "#EBE8EE"));
        SetBrush("ShellBorderBrush", Solid("#D3D0D8"));
        SetBrush("ShellSheenBrush", Gradient("#55FFFFFF", "#00FFFFFF"));
        SetBrush("TopBarBackgroundBrush", Gradient("#FFFFFF", "#F4F1F5"));
        SetBrush("TopBarBorderBrush", Solid("#E2DEE7"));
        SetBrush("BrandTileBrush", Gradient("#FF9B3E", "#F36D1E"));
        SetBrush("BrandTileBorderBrush", Solid("#F3B784"));
        SetBrush("BrandGlyphBrush", Solid("#FFF8F4"));
        SetBrush("UtilityGroupBrush", Gradient("#F8F6FA", "#F2EFF4"));
        SetBrush("UtilityGroupBorderBrush", Solid("#DED9E2"));
        SetBrush("UtilityButtonBrush", Solid("#FBF9FC"));
        SetBrush("UtilityButtonHoverBrush", Solid("#EFEAF2"));
        SetBrush("UtilityButtonBorderBrush", Solid("#DDD8E2"));
        SetBrush("TextPrimaryBrush", Solid("#2E3040"));
        SetBrush("TextSecondaryBrush", Solid("#727486"));
        SetBrush("TextMutedBrush", Solid("#9598A8"));
        SetBrush("IconSecondaryBrush", Solid("#8E91A2"));
        SetBrush("AccentOrangeBrush", Solid("#F58A2C"));
        SetBrush("AccentGreenBrush", Solid("#46B86A"));
        SetBrush("CardBackgroundBrush", Gradient("#FFFFFF", "#F8F6FA"));
        SetBrush("CardBorderBrush", Solid("#DDD9E1"));
        SetBrush("CardHoverOverlayBrush", Solid("#10FFFFFF"));
        SetBrush("IconTileBrush", Solid("#F4F1F6"));
        SetBrush("IconTileBorderBrush", Solid("#DDD9E1"));
        SetBrush("SliderInactiveBrush", Solid("#D6D2DB"));
        SetBrush("SliderThumbBrush", Solid("#FFF7F0"));
        SetBrush("SliderThumbBorderBrush", Solid("#E3D0C3"));
        SetBrush("MutePillBackgroundBrush", Solid("#F6F3F8"));
        SetBrush("MutePillBorderBrush", Solid("#DCD8E1"));
        SetBrush("MutePillHoverBrush", Solid("#EFEBF2"));
        SetBrush("MutePillActiveBackgroundBrush", Solid("#FFF1E6"));
        SetBrush("MutePillActiveBorderBrush", Solid("#F1B17C"));
        SetBrush("DividerBrush", Solid("#DED9E1"));
        SetBrush("FooterBackgroundBrush", Gradient("#F9F7FA", "#F2EFF4"));
        SetBrush("FooterBorderBrush", Solid("#E2DEE7"));
        SetBrush("FooterHoverBrush", Solid("#F1EEF4"));
        SetBrush("FooterActiveBrush", Solid("#F58A2C"));
        SetBrush("FooterInactiveBrush", Solid("#7A7D90"));
        SetBrush("OverlayShadeBrush", Solid("#9BFFFFFF"));
        SetBrush("OverlayPanelBrush", Gradient("#FFFFFF", "#F7F4F8"));
        SetBrush("OverlayPanelBorderBrush", Solid("#DDD8E2"));
        SetBrush("OverlaySoftCardBrush", Gradient("#FAF8FB", "#F4F1F6"));
        SetBrush("OverlayOptionBrush", Solid("#F8F5F9"));
        SetBrush("OverlayOptionBorderBrush", Solid("#DDD8E2"));
        SetBrush("OverlayOptionHoverBrush", Solid("#F0EDF3"));
        SetBrush("OverlaySelectedBrush", Solid("#F3EADF"));
        SetBrush("OverlaySelectedBorderBrush", Solid("#E3B07F"));
        SetBrush("OverlayPrimaryBrush", Gradient("#F58A2C", "#E9751A"));
        SetBrush("OverlayPrimaryHoverBrush", Gradient("#FB9540", "#EE7F25"));
        SetBrush("OverlayPrimaryBorderBrush", Solid("#F2B685"));
        SetBrush("ContextMenuBackgroundBrush", Solid("#FFFFFF"));
        SetBrush("ContextMenuBorderBrush", Solid("#DDD8E2"));
        SetBrush("ContextMenuHoverBrush", Solid("#F1EEF4"));
        SetBrush("ScrollThumbBrush", Solid("#C9C3CE"));
        SetBrush("ScrollThumbHoverBrush", Solid("#B3ADB8"));
    }

    private void SetBrush(string key, Brush brush)
    {
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        Resources[key] = brush;
    }

    private void NotifyIconServiceOnLeftDoubleClickReceived(object? sender, EventArgs e)
    {
        RestoreFromTray();
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

        var openItem = new MenuItem { Header = "Open AudioBit" };
        openItem.Click += (_, _) => RestoreFromTray();

        var muteAllItem = new MenuItem { Header = "Mute All" };
        muteAllItem.Click += (_, _) => _viewModel.ToggleMuteAllCommand.Execute(null);

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitFromTray();

        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(muteAllItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(exitItem);

        return contextMenu;
    }

    private void ApplyRoundedWindowRegion()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
        var radiusX = Math.Max(1, (int)Math.Ceiling(WindowCornerRadius * dpi.DpiScaleX));
        var radiusY = Math.Max(1, (int)Math.Ceiling(WindowCornerRadius * dpi.DpiScaleY));

        var regionHandle = CreateRoundRectRgn(0, 0, width + 1, height + 1, radiusX, radiusY);
        if (regionHandle == IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRgn(handle, regionHandle, true) == 0)
        {
            DeleteObject(regionHandle);
        }
    }

    private void ApplyRoundedVisualClips()
    {
        ApplyRoundedClip(ShellClipHost, 28);
        ApplyRoundedClip(MasterCardHost, 20);
        ApplyRoundedClip(DeviceCardHost, 20);
        ApplyRoundedClip(AgentsOverlayHost, 24);
        ApplyRoundedClip(CalibrateOverlayHost, 24);
        ApplyRoundedClip(SavedOverlayHost, 24);
    }

    private static void ApplyRoundedClip(FrameworkElement element, double radius)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return;
        }

        var clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius);
        if (clip.CanFreeze)
        {
            clip.Freeze();
        }

        element.Clip = clip;
    }

    private static SolidColorBrush Solid(string hex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
    }

    private static LinearGradientBrush Gradient(string startColor, string endColor)
    {
        return new LinearGradientBrush(
            new GradientStopCollection
            {
                new((Color)ColorConverter.ConvertFromString(startColor)!, 0),
                new((Color)ColorConverter.ConvertFromString(endColor)!, 1),
            },
            new Point(0, 0),
            new Point(1, 1));
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
