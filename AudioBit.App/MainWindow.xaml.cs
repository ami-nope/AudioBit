using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AudioBit.App.Infrastructure;
using AudioBit.App.ViewModels;

namespace AudioBit.App;

public partial class MainWindow : Window
{
    private const int WindowCornerRadius = 28;

    private readonly MainViewModel _viewModel;
    private readonly AudioBitNotifyIconService _notifyIconService;
    private readonly GlobalHotKeyService _globalHotKeyService;
    private readonly ContextMenu _trayContextMenu;
    private readonly MouseWheelEventHandler _closeComboBoxesOnMouseWheelHandler;
    private readonly ScrollChangedEventHandler _closeComboBoxesOnScrollChangedHandler;

    private bool _hasStarted;
    private bool _exitRequested;

    internal MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;

        InitializeComponent();

        DataContext = _viewModel;
        Icon = AudioBitIconLoader.WindowIcon;

        _trayContextMenu = BuildTrayMenu();
        _notifyIconService = CreateNotifyIconService(_trayContextMenu);
        _globalHotKeyService = new GlobalHotKeyService();
        _closeComboBoxesOnMouseWheelHandler = CloseOpenComboBoxesOnPreviewMouseWheel;
        _closeComboBoxesOnScrollChangedHandler = CloseOpenComboBoxesOnScrollChanged;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        _globalHotKeyService.Pressed += GlobalHotKeyServiceOnPressed;

        ApplyThemePalette(_viewModel.IsDarkTheme);
        ApplyTrayMenuTheme();
        ApplyLowPerformanceVisualState();

        SourceInitialized += MainWindowOnSourceInitialized;
        Loaded += MainWindowOnLoaded;
        SizeChanged += MainWindowOnSizeChanged;
        StateChanged += MainWindowOnStateChanged;
        Deactivated += MainWindowOnDeactivated;
        Closing += MainWindowOnClosing;
        Closed += MainWindowOnClosed;
        PreviewKeyDown += MainWindowOnPreviewKeyDown;
        PreviewMouseDown += MainWindowOnPreviewMouseDown;
        AddHandler(UIElement.PreviewMouseWheelEvent, _closeComboBoxesOnMouseWheelHandler, true);
        AddHandler(ScrollViewer.ScrollChangedEvent, _closeComboBoxesOnScrollChangedHandler, true);
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(
        int nLeftRect,
        int nTopRect,
        int nRightRect,
        int nBottomRect,
        int nWidthEllipse,
        int nHeightEllipse);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int RgnOr = 2;

    private void MainWindowOnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyRoundedWindowRegion();
        ApplyRoundedVisualClips();
        _globalHotKeyService.Attach(this);
        RefreshHotKeyRegistration();
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
        UpdateOverlayAnchors();

        if (_viewModel.ConsumeStartMinimized())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(HideToTray));
        }
    }

    private void MainWindowOnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyRoundedWindowRegion();
        ApplyRoundedVisualClips();
        UpdateOverlayAnchors();
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

    private void MainWindowOnDeactivated(object? sender, EventArgs e)
    {
        SpotifyWidget?.Collapse();
    }

    private void MainWindowOnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested && _viewModel.ShouldHideOnClose)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _viewModel.Stop();
        _globalHotKeyService.Unregister();

        if (_notifyIconService.IsRegistered)
        {
            _notifyIconService.Unregister();
        }
    }

    private void MainWindowOnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        _notifyIconService.LeftDoubleClickReceived -= NotifyIconServiceOnLeftDoubleClickReceived;
        _globalHotKeyService.Pressed -= GlobalHotKeyServiceOnPressed;
        _globalHotKeyService.Dispose();
        PreviewKeyDown -= MainWindowOnPreviewKeyDown;
        PreviewMouseDown -= MainWindowOnPreviewMouseDown;
        Deactivated -= MainWindowOnDeactivated;
        RemoveHandler(UIElement.PreviewMouseWheelEvent, _closeComboBoxesOnMouseWheelHandler);
        RemoveHandler(ScrollViewer.ScrollChangedEvent, _closeComboBoxesOnScrollChangedHandler);
    }

    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        TryBeginWindowDrag(e);
    }

    private void TopBar_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || HasAncestor<ButtonBase>(source)
            || HasAncestor<ScrollBar>(source)
            || HasAncestor<Selector>(source)
            || HasAncestor<TextBoxBase>(source))
        {
            return;
        }

        TryBeginWindowDrag(e);
    }

    private void ShellBackground_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        TryBeginWindowDrag(e);
    }

    private void MixerListBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || HasAncestor<ListBoxItem>(source)
            || HasAncestor<ScrollBar>(source))
        {
            return;
        }

        TryBeginWindowDrag(e);
    }

    private void ContextMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = PlacementMode.Top;
        button.ContextMenu.IsOpen = true;
    }

    private void MinimizeWindow_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseWindow_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void HideToTray()
    {
        SpotifyWidget?.Collapse();
        ShowInTaskbar = false;
        Hide();
        _viewModel.OnHiddenToTray();

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
        _viewModel.OnRestoredFromTray();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    private void MainWindowOnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel.TryHandleMicMuteHotKeyCapture(e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && SpotifyWidget is not null && SpotifyWidget.IsExpanded)
        {
            SpotifyWidget.Collapse();
            e.Handled = true;
        }
    }

    private void MainWindowOnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SpotifyWidget is not null
            && SpotifyWidget.IsExpanded
            && (e.OriginalSource is not DependencyObject spotifySource || !SpotifyWidget.ContainsElement(spotifySource)))
        {
            SpotifyWidget.Collapse();
        }

        var isDeviceInfoVisible = _viewModel.IsRemoteDeviceInfoPanelVisible;
        if (!isDeviceInfoVisible)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject source)
        {
            if (isDeviceInfoVisible)
            {
                _viewModel.CloseRemoteDeviceInfoPanel();
            }

            return;
        }

        var isInsideDeviceInfo = isDeviceInfoVisible
            && (IsDescendantOf(source, RemoteDeviceInfoButton) || IsDescendantOf(source, RemoteDeviceInfoPanel));
        if (isInsideDeviceInfo)
        {
            return;
        }

        if (isDeviceInfoVisible)
        {
            _viewModel.CloseRemoteDeviceInfoPanel();
        }
    }

    private void RemoteDeviceInfoTrigger_OnPreviewMouseEnter(object sender, MouseEventArgs e)
    {
        _viewModel.SetRemoteDeviceInfoPanelHover(true);
    }

    private void RemoteDeviceInfoTrigger_OnPreviewMouseLeave(object sender, MouseEventArgs e)
    {
        if (RemoteDeviceInfoPanel.IsMouseOver)
        {
            return;
        }

        _viewModel.SetRemoteDeviceInfoPanelHover(false);
    }

    private void RemoteDeviceInfoPanel_OnMouseEnter(object sender, MouseEventArgs e)
    {
        _viewModel.SetRemoteDeviceInfoPanelHover(true);
    }

    private void RemoteDeviceInfoPanel_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (RemoteDeviceInfoButton.IsMouseOver)
        {
            return;
        }

        _viewModel.SetRemoteDeviceInfoPanelHover(false);
    }

    private void TryBeginWindowDrag(MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Ignore drag requests from transient mouse states.
        }
    }

    private static bool HasAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is FrameworkContentElement contentElement)
        {
            return contentElement.Parent ?? contentElement.TemplatedParent;
        }

        return VisualTreeHelper.GetParent(child);
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDarkTheme))
        {
            ApplyThemePalette(_viewModel.IsDarkTheme);
            ApplyTrayMenuTheme();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsLowPerformanceMode))
        {
            ApplyLowPerformanceVisualState();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.MicMuteHotKey))
        {
            RefreshHotKeyRegistration();
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsUpdateRestartDialogVisible))
        {
            if (_viewModel.IsUpdateRestartDialogVisible)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowUpdateRestartDialog));
            }

            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsAnyOverlayVisible)
            || e.PropertyName == nameof(MainViewModel.IsRemoteQrPanelVisible)
            || e.PropertyName == nameof(MainViewModel.IsProfilesTabSelected)
            || e.PropertyName == nameof(MainViewModel.IsSettingsTabSelected))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateOverlayAnchors));
        }
    }

    private void UpdateOverlayAnchors()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (!_viewModel.IsAnyOverlayVisible)
        {
            ResetOverlayShift();
            return;
        }

        if (OverlayLayer is null)
        {
            ResetOverlayShift();
            return;
        }

        var anchor = GetOverlayAnchor();
        if (anchor is null || anchor.ActualWidth <= 0 || anchor.ActualHeight <= 0)
        {
            ResetOverlayShift();
            return;
        }

        var anchorCenter = anchor.TranslatePoint(
            new Point(anchor.ActualWidth / 2.0, anchor.ActualHeight / 2.0),
            OverlayLayer);

        ApplyOverlayShift(CalibrateOverlayHost, CalibrateOverlayShift, anchorCenter);
        ApplyOverlayShift(SavedOverlayHost, SavedOverlayShift, anchorCenter);
    }

    private FrameworkElement? GetOverlayAnchor()
    {
        if (RemoteQrButtonCompact is not null
            && RemoteQrButtonCompact.IsVisible
            && RemoteQrButtonCompact.ActualWidth > 0
            && RemoteQrButtonCompact.ActualHeight > 0)
        {
            return RemoteQrButtonCompact;
        }

        if (RemoteQrButtonLarge is not null
            && RemoteQrButtonLarge.IsVisible
            && RemoteQrButtonLarge.ActualWidth > 0
            && RemoteQrButtonLarge.ActualHeight > 0)
        {
            return RemoteQrButtonLarge;
        }

        return null;
    }

    private void ApplyOverlayShift(FrameworkElement? host, TranslateTransform? shift, Point anchorCenter)
    {
        if (host is null || shift is null || OverlayLayer is null)
        {
            return;
        }

        if (host.ActualWidth <= 0 || host.ActualHeight <= 0)
        {
            shift.X = 0;
            shift.Y = 0;
            return;
        }

        var hostCenter = host.TranslatePoint(
            new Point(host.ActualWidth / 2.0, host.ActualHeight / 2.0),
            OverlayLayer);

        shift.X = anchorCenter.X - hostCenter.X;
        shift.Y = anchorCenter.Y - hostCenter.Y;
    }

    private void ResetOverlayShift()
    {
        if (CalibrateOverlayShift is not null)
        {
            CalibrateOverlayShift.X = 0;
            CalibrateOverlayShift.Y = 0;
        }

        if (SavedOverlayShift is not null)
        {
            SavedOverlayShift.X = 0;
            SavedOverlayShift.Y = 0;
        }
    }

    private void ShowUpdateRestartDialog()
    {
        if (!_viewModel.IsUpdateRestartDialogVisible)
        {
            return;
        }

        if (!IsVisible || !ShowInTaskbar || WindowState == WindowState.Minimized || _notifyIconService.IsRegistered)
        {
            RestoreFromTray();
        }
        else
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
            ApplyRoundedWindowRegion();
        }

        Topmost = true;
        Activate();
        Focus();
        Topmost = _viewModel.IsAlwaysOnTop;
    }

    private void RefreshHotKeyRegistration()
    {
        _globalHotKeyService.Register(_viewModel.MicMuteHotKey);
    }

    private void GlobalHotKeyServiceOnPressed(object? sender, EventArgs e)
    {
        _viewModel.HandleGlobalMicMuteHotKey();
    }

    private void VolumeSlider_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled)
        {
            return;
        }

        e.Handled = true;
        ForwardMouseWheelToScrollHost(slider, e);
    }

    private void CloseOpenComboBoxesOnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0)
        {
            return;
        }

        CloseOpenComboBoxes();
    }

    private void CloseOpenComboBoxesOnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) <= double.Epsilon
            && Math.Abs(e.HorizontalChange) <= double.Epsilon)
        {
            return;
        }

        CloseOpenComboBoxes();
    }

    private void CloseOpenComboBoxes()
    {
        foreach (var comboBox in EnumerateVisualDescendants<ComboBox>(this))
        {
            if (comboBox.IsDropDownOpen)
            {
                comboBox.IsDropDownOpen = false;
            }
        }
    }

    private void ApplyThemePalette(bool isDarkTheme)
    {
        if (isDarkTheme)
        {
            SetBrush("WindowBackdropBrush", Solid("#0D0F16"));
            SetBrush("ShellBackgroundBrush", Gradient("#2B2B39", "#191A24"));
            SetBrush("TopBarBackgroundBrush", Gradient("#343545", "#2A2B38"));
            SetBrush("BrandTileBrush", Gradient("#FF9B3E", "#F36D1E"));
            SetBrush("BrandTileBorderBrush", Solid("#FFB676"));
            SetBrush("BrandGlyphBrush", Solid("#FFF7F1"));
            SetBrush("UtilityButtonBrush", Solid("#343546"));
            SetBrush("UtilityButtonHoverBrush", Solid("#404154"));
            SetBrush("UtilityButtonBorderBrush", Solid("#4A4C60"));
            SetBrush("TextPrimaryBrush", Solid("#F4F2F8"));
            SetBrush("TextSecondaryBrush", Solid("#A7A8B7"));
            SetBrush("TextMutedBrush", Solid("#8B8D9E"));
            SetBrush("IconSecondaryBrush", Solid("#9A9DAF"));
            SetBrush("AccentOrangeBrush", Solid("#FF8D33"));
            SetBrush("AccentGreenBrush", Solid("#49BF69"));
            SetBrush("SpotifyAccentBrush", Solid("#1DB954"));
            SetBrush("CardBackgroundBrush", Gradient("#2A2A38", "#242532"));
            SetBrush("CardBorderBrush", Solid("#434557"));
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
            SetBrush("SpotifyWidgetGlassBrush", Gradient("#B2252934", "#C11A1F29"));
            SetBrush("SpotifyWidgetGlassHoverBrush", Gradient("#C12A303B", "#CF1F2430"));
            SetBrush("SpotifyWidgetBorderBrush", Solid("#4E5368"));
            SetBrush("SpotifyWidgetGlowBrush", Solid("#661DB954"));
            SetBrush("SpotifyWidgetProgressBrush", Solid("#1DB954"));
            SetBrush("SpotifyWidgetMutedTextBrush", Solid("#A2A7B6"));
            SetBrush("SpotifyWidgetIconBrush", Solid("#FFFFFF"));
            return;
        }

        SetBrush("WindowBackdropBrush", Solid("#F0EEF2"));
        SetBrush("ShellBackgroundBrush", Gradient("#FFFFFF", "#EBE8EE"));
        SetBrush("TopBarBackgroundBrush", Gradient("#FFFFFF", "#F4F1F5"));
        SetBrush("BrandTileBrush", Gradient("#FF9B3E", "#F36D1E"));
        SetBrush("BrandTileBorderBrush", Solid("#F3B784"));
        SetBrush("BrandGlyphBrush", Solid("#FFF8F4"));
        SetBrush("UtilityButtonBrush", Solid("#FBF9FC"));
        SetBrush("UtilityButtonHoverBrush", Solid("#EFEAF2"));
        SetBrush("UtilityButtonBorderBrush", Solid("#DDD8E2"));
        SetBrush("TextPrimaryBrush", Solid("#2E3040"));
        SetBrush("TextSecondaryBrush", Solid("#727486"));
        SetBrush("TextMutedBrush", Solid("#9598A8"));
        SetBrush("IconSecondaryBrush", Solid("#8E91A2"));
        SetBrush("AccentOrangeBrush", Solid("#F58A2C"));
        SetBrush("AccentGreenBrush", Solid("#46B86A"));
        SetBrush("SpotifyAccentBrush", Solid("#1DB954"));
        SetBrush("CardBackgroundBrush", Gradient("#FFFFFF", "#F8F6FA"));
        SetBrush("CardBorderBrush", Solid("#DDD9E1"));
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
        SetBrush("SpotifyWidgetGlassBrush", Gradient("#E8FFFFFF", "#D7F3F0F6"));
        SetBrush("SpotifyWidgetGlassHoverBrush", Gradient("#F2FFFFFF", "#E5F6F3F8"));
        SetBrush("SpotifyWidgetBorderBrush", Solid("#D7DDE6"));
        SetBrush("SpotifyWidgetGlowBrush", Solid("#551DB954"));
        SetBrush("SpotifyWidgetProgressBrush", Solid("#1DB954"));
        SetBrush("SpotifyWidgetMutedTextBrush", Solid("#6B7080"));
        SetBrush("SpotifyWidgetIconBrush", Solid("#FFFFFF"));
    }

    private void MasterCardHost_OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (_viewModel.IsLowPerformanceMode)
        {
            ResetMasterCardVisuals();
            return;
        }

        AnimateMasterCardVisuals(-2.0, 1.0, 0.72, 180);
    }

    private void MasterCardHost_OnMouseLeave(object sender, MouseEventArgs e)
    {
        ResetMasterCardVisuals(220);
    }

    private void ApplyLowPerformanceVisualState()
    {
        if (_viewModel.IsLowPerformanceMode)
        {
            ResetMasterCardVisuals();
        }
    }

    private static void AnimateDouble(DependencyObject target, DependencyProperty property, double value, int durationMilliseconds)
    {
        if (durationMilliseconds <= 0)
        {
            if (target is IAnimatable animatable)
            {
                animatable.BeginAnimation(property, null);
            }

            target.SetValue(property, value);
            return;
        }

        if (target is not IAnimatable animationTarget)
        {
            target.SetValue(property, value);
            return;
        }

        animationTarget.BeginAnimation(
            property,
            new DoubleAnimation
            {
                To = value,
                Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
            });
    }

    private void ResetMasterCardVisuals(int durationMilliseconds = 0)
    {
        AnimateMasterCardVisuals(0.0, 0.0, 0.0, durationMilliseconds);
    }

    private void AnimateMasterCardVisuals(double translateY, double sheenOpacity, double strokeOpacity, int durationMilliseconds)
    {
        AnimateDouble(MasterCardLiftTransform, TranslateTransform.YProperty, translateY, durationMilliseconds);
        AnimateDouble(MasterCardSheen, OpacityProperty, sheenOpacity, durationMilliseconds);
        AnimateDouble(MasterCardHoverStroke, OpacityProperty, strokeOpacity, durationMilliseconds);
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

    private AudioBitNotifyIconService CreateNotifyIconService(ContextMenu trayContextMenu)
    {
        var service = new AudioBitNotifyIconService
        {
            TooltipText = "AudioBit",
            Icon = AudioBitIconLoader.TrayIcon,
            ContextMenu = trayContextMenu,
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

        ApplyMenuTheme(contextMenu);
        return contextMenu;
    }

    private void ApplyTrayMenuTheme()
    {
        _trayContextMenu.Resources.Clear();

        foreach (var key in Resources.Keys)
        {
            _trayContextMenu.Resources[key] = Resources[key];
        }

        ApplyMenuTheme(_trayContextMenu);
    }

    private void ApplyMenuTheme(ContextMenu contextMenu)
    {
        if (TryFindResource("OverflowContextMenuStyle") is Style contextMenuStyle)
        {
            contextMenu.Style = contextMenuStyle;
        }

        foreach (var item in contextMenu.Items)
        {
            ApplyMenuTheme(item);
        }
    }

    private void ApplyMenuTheme(object item)
    {
        switch (item)
        {
            case MenuItem menuItem:
                if (TryFindResource("OverflowMenuItemStyle") is Style menuItemStyle)
                {
                    menuItem.Style = menuItemStyle;
                }

                foreach (var childItem in menuItem.Items)
                {
                    ApplyMenuTheme(childItem);
                }

                break;

            case Separator separator when TryFindResource("OverflowSeparatorStyle") is Style separatorStyle:
                separator.Style = separatorStyle;
                break;
        }
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
        var cornerDiameterX = Math.Max(2, (int)Math.Ceiling(WindowCornerRadius * 2 * dpi.DpiScaleX));
        var cornerDiameterY = Math.Max(2, (int)Math.Ceiling(WindowCornerRadius * 2 * dpi.DpiScaleY));

        var shellRegion = CreateShellRegion(dpi, width, height, cornerDiameterX, cornerDiameterY);
        var widgetRegion = CreateSpotifyWidgetRegion(dpi);
        var regionHandle = shellRegion;

        if (regionHandle != IntPtr.Zero && widgetRegion != IntPtr.Zero)
        {
            if (CombineRgn(regionHandle, shellRegion, widgetRegion, RgnOr) == 0)
            {
                DeleteObject(widgetRegion);
                widgetRegion = IntPtr.Zero;
            }
        }

        if (widgetRegion != IntPtr.Zero)
        {
            DeleteObject(widgetRegion);
        }

        if (regionHandle == IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRgn(handle, regionHandle, true) == 0)
        {
            DeleteObject(regionHandle);
        }
    }

    private IntPtr CreateShellRegion(DpiScale dpi, int fallbackWidth, int fallbackHeight, int cornerDiameterX, int cornerDiameterY)
    {
        if (ShellBorder is null || ShellBorder.ActualWidth <= 0 || ShellBorder.ActualHeight <= 0)
        {
            return CreateRoundRectRgn(0, 0, fallbackWidth + 1, fallbackHeight + 1, cornerDiameterX, cornerDiameterY);
        }

        var shellOrigin = ShellBorder.TranslatePoint(new Point(0, 0), this);
        var left = (int)Math.Floor(shellOrigin.X * dpi.DpiScaleX);
        var top = (int)Math.Floor(shellOrigin.Y * dpi.DpiScaleY);
        var right = (int)Math.Ceiling((shellOrigin.X + ShellBorder.ActualWidth) * dpi.DpiScaleX);
        var bottom = (int)Math.Ceiling((shellOrigin.Y + ShellBorder.ActualHeight) * dpi.DpiScaleY);

        return CreateRoundRectRgn(left, top, right + 1, bottom + 1, cornerDiameterX, cornerDiameterY);
    }

    private IntPtr CreateSpotifyWidgetRegion(DpiScale dpi)
    {
        if (SpotifyWidget is null || SpotifyWidget.ActualWidth <= 0 || SpotifyWidget.ActualHeight <= 0)
        {
            return IntPtr.Zero;
        }

        var widgetOrigin = SpotifyWidget.TranslatePoint(new Point(0, 0), this);
        const double widgetCornerRadius = 18;

        var left = (int)Math.Floor(widgetOrigin.X * dpi.DpiScaleX);
        var top = (int)Math.Floor(widgetOrigin.Y * dpi.DpiScaleY);
        var right = (int)Math.Ceiling((widgetOrigin.X + SpotifyWidget.ActualWidth) * dpi.DpiScaleX);
        var bottom = (int)Math.Ceiling((widgetOrigin.Y + SpotifyWidget.ActualHeight) * dpi.DpiScaleY);
        var cornerDiameterX = Math.Max(2, (int)Math.Ceiling(widgetCornerRadius * 2 * dpi.DpiScaleX));
        var cornerDiameterY = Math.Max(2, (int)Math.Ceiling(widgetCornerRadius * 2 * dpi.DpiScaleY));

        return CreateRoundRectRgn(left, top, right + 1, bottom + 1, cornerDiameterX, cornerDiameterY);
    }

    private void ApplyRoundedVisualClips()
    {
        ApplyRoundedClip(CalibrateOverlayHost, 24);
        ApplyRoundedClip(SavedOverlayHost, 24);
        ApplyRoundedClip(UpdateRestartOverlayHost, 24);
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

    private static void ForwardMouseWheelToScrollHost(DependencyObject source, MouseWheelEventArgs e)
    {
        var scrollHost = FindAncestor<ScrollViewer>(source);
        if (scrollHost is null)
        {
            return;
        }

        var forwardedEvent = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = source,
        };

        scrollHost.RaiseEvent(forwardedEvent);
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (DependencyObject? current = GetParent(source); current is not null; current = GetParent(current))
        {
            if (current is T typedCurrent)
            {
                return typedCurrent;
            }
        }

        return null;
    }

    private static IEnumerable<T> EnumerateVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in EnumerateVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

}
