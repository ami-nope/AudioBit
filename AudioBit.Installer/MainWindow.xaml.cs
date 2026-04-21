using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AudioBit.Core;
using Forms = System.Windows.Forms;

namespace AudioBit.Installer;

internal enum InstallerStep
{
    Location,
    Options,
    Progress,
}

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int WindowCornerRadius = 34;
    private const double ShellClipCornerRadius = 30;
    private const double ShellClipInset = 1.0;

    private readonly InstallerEngine _installerEngine;
    private readonly InstallerMetadataService _metadataService = new();
    private readonly InstallerMode _mode;
    private bool _canInstall = true;
    private bool _canLaunch;
    private bool _closeOnPrimaryAction;
    private bool _createDesktopShortcut = true;
    private bool _createStartMenuShortcut = true;
    private InstallerStep _currentStep;
    private string _installButtonText;
    private string _installPath;
    private bool _launchAfterInstall = true;
    private string _payloadSummaryText = string.Empty;
    private string _phaseText = "READY";
    private bool _pinToStart;
    private bool _pinToTaskbar;
    private double _progressValue;
    private string _progressPercentText = "0%";
    private string _statusText = string.Empty;
    private string _latestVersionText = "Loading...";
    private string _metadataFootnoteText = "Version syncs from GitHub. Website metrics load when the site is reachable.";
    private string _metadataStatusText = "Fetching live metadata";
    private double? _websiteRatingValue;
    private int? _websiteRatingCount;
    private int? _websiteStarsCount;

    public MainWindow(InstallerMode mode, string? installPath = null)
    {
        InitializeComponent();
        DataContext = this;

        _mode = mode;
        _installerEngine = new InstallerEngine(InstallerPaths.GetPayloadPath(AppContext.BaseDirectory));
        DefaultInstallPath = InstallerPaths.GetDefaultInstallPath();
        _installPath = string.IsNullOrWhiteSpace(installPath)
            ? DefaultInstallPath
            : installPath;
        _installButtonText = mode == InstallerMode.Install ? "Install AudioBit" : "Remove AudioBit";
        _currentStep = mode == InstallerMode.Install ? InstallerStep.Location : InstallerStep.Progress;

        SourceInitialized += MainWindowOnSourceInitialized;
        Loaded += MainWindowOnLoaded;
        SizeChanged += MainWindowOnSizeChanged;
        StateChanged += MainWindowOnStateChanged;

        RefreshState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string DefaultInstallPath { get; }

    public bool CanInstall
    {
        get => _canInstall;
        private set
        {
            if (SetProperty(ref _canInstall, value))
            {
                OnPropertyChanged(nameof(CanProceedFromLocation));
                OnPropertyChanged(nameof(CanRunPrimaryAction));
                OnPropertyChanged(nameof(ShowReturnToOptionsButton));
                OnPropertyChanged(nameof(ProgressTitleText));
                OnPropertyChanged(nameof(CanUseProgressSecondaryAction));
                OnPropertyChanged(nameof(ShowProgressSecondaryActionButton));
                OnPropertyChanged(nameof(ShowProgressStatusPill));
                OnPropertyChanged(nameof(ProgressSecondaryActionText));
                OnPropertyChanged(nameof(ProgressStatusPillText));
            }
        }
    }

    public bool CanLaunch
    {
        get => _canLaunch;
        private set
        {
            if (SetProperty(ref _canLaunch, value))
            {
                OnPropertyChanged(nameof(ShowLaunchButton));
                OnPropertyChanged(nameof(ShowReturnToOptionsButton));
                OnPropertyChanged(nameof(CanUseProgressSecondaryAction));
                OnPropertyChanged(nameof(ShowProgressSecondaryActionButton));
                OnPropertyChanged(nameof(ShowProgressStatusPill));
                OnPropertyChanged(nameof(ProgressSecondaryActionText));
            }
        }
    }

    public bool CanProceedFromLocation => IsInstallMode && CanInstall && !string.IsNullOrWhiteSpace(InstallPath);

    public bool CanRunPrimaryAction => CanInstall && !string.IsNullOrWhiteSpace(InstallPath);

    public bool CreateDesktopShortcut
    {
        get => _createDesktopShortcut;
        set
        {
            if (SetProperty(ref _createDesktopShortcut, value))
            {
                OnOptionSelectionChanged();
            }
        }
    }

    public bool CreateStartMenuShortcut
    {
        get => _createStartMenuShortcut;
        set
        {
            if (!value && PinToStart)
            {
                PinToStart = false;
            }

            if (SetProperty(ref _createStartMenuShortcut, value))
            {
                OnOptionSelectionChanged();
            }
        }
    }

    public string InstallButtonText
    {
        get => _installButtonText;
        private set => SetProperty(ref _installButtonText, value);
    }

    public string InstallPath
    {
        get => _installPath;
        set
        {
            if (SetProperty(ref _installPath, value))
            {
                OnPropertyChanged(nameof(CanProceedFromLocation));
                OnPropertyChanged(nameof(CanRunPrimaryAction));
                OnPropertyChanged(nameof(InstallPathBadgeText));
                OnPropertyChanged(nameof(InstallFolderHeaderText));
                RefreshState();
            }
        }
    }

    public string InstallPathBadgeText => IsDefaultInstallPath() ? "DEFAULT FOLDER" : "CUSTOM FOLDER";

    public string InstallFolderHeaderText => IsDefaultInstallPath() ? "Default Folder" : "Custom Folder";

    public bool IsInstallMode => _mode == InstallerMode.Install;

    public bool IsUninstallMode => _mode == InstallerMode.Uninstall;

    public bool LaunchAfterInstall
    {
        get => _launchAfterInstall;
        set
        {
            if (SetProperty(ref _launchAfterInstall, value))
            {
                OnOptionSelectionChanged();
            }
        }
    }

    public string HeaderModeText => IsInstallMode ? "Install" : "Uninstall";

    public string HeaderTitleText => "Audiobit";

    public string ModeHeadline => IsInstallMode ? "AudioBit Setup" : "AudioBit Uninstall";

    public string ModePillText => IsInstallMode ? "INSTALLER" : "UNINSTALLER";

    public string ModeSummaryText =>
        IsInstallMode
            ? "Current-user install with Windows Apps registration and a themed uninstaller."
            : "AudioBit will be removed from Windows Apps, its shortcuts, and its installed files.";

    public string ModeSubtitle =>
        IsInstallMode
            ? "Default install folder first, then shell options and built-in payload extraction."
            : "Rounded custom removal flow without dropping into the native uninstaller dialog.";

    public string PathSectionTitle => IsInstallMode ? "Install target" : "Installed location";

    public string PayloadSummaryText
    {
        get => _payloadSummaryText;
        private set => SetProperty(ref _payloadSummaryText, value);
    }

    public string LatestVersionText
    {
        get => _latestVersionText;
        private set
        {
            if (SetProperty(ref _latestVersionText, value))
            {
                OnPropertyChanged(nameof(VersionLineText));
            }
        }
    }

    public string MetadataFootnoteText
    {
        get => _metadataFootnoteText;
        private set => SetProperty(ref _metadataFootnoteText, value);
    }

    public string MetadataStatusText
    {
        get => _metadataStatusText;
        private set => SetProperty(ref _metadataStatusText, value);
    }

    public string PhaseText
    {
        get => _phaseText;
        private set
        {
            if (SetProperty(ref _phaseText, value))
            {
                OnPropertyChanged(nameof(ProgressTitleText));
                OnPropertyChanged(nameof(ProgressStatusPillText));
            }
        }
    }

    public bool PinToStart
    {
        get => _pinToStart;
        set
        {
            if (value && !CreateStartMenuShortcut)
            {
                CreateStartMenuShortcut = true;
            }

            if (SetProperty(ref _pinToStart, value))
            {
                OnOptionSelectionChanged();
            }
        }
    }

    public string PinningNoteText =>
        "Start and taskbar pinning is requested through Windows shell verbs. Some Windows builds or policies may block it.";

    public bool PinToTaskbar
    {
        get => _pinToTaskbar;
        set
        {
            if (SetProperty(ref _pinToTaskbar, value))
            {
                OnOptionSelectionChanged();
            }
        }
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set
        {
            if (SetProperty(ref _progressValue, value))
            {
                OnPropertyChanged(nameof(RemainingProgressValue));
                OnPropertyChanged(nameof(MainBarPercentLabelText));
                OnPropertyChanged(nameof(ProgressLaneOnePercentText));
                OnPropertyChanged(nameof(ProgressLaneTwoPercentText));
                OnPropertyChanged(nameof(ProgressLaneThreePercentText));
                OnPropertyChanged(nameof(ProgressLaneFourPercentText));
                OnPropertyChanged(nameof(ProgressLaneFivePercentText));
            }
        }
    }

    public string ProgressPercentText
    {
        get => _progressPercentText;
        private set => SetProperty(ref _progressPercentText, value);
    }

    public string ProgressBarLabelText => IsInstallMode ? "Install Progress" : "Removal Progress";

    public string MainBarPercentLabelText => ProgressPercentText;

    public string ProgressStepTag => IsInstallMode ? "STEP 3 OF 3" : "UNINSTALL";

    public string ProgressLaneOneTitle => IsInstallMode ? "Preparing Installation..." : "Stopping processes...";

    public string ProgressLaneTwoTitle => IsInstallMode ? "Extracting files..." : "Removing shortcuts...";

    public string ProgressLaneThreeTitle => IsInstallMode ? "Installing components..." : "Deleting files...";

    public string ProgressLaneFourTitle => IsInstallMode ? "Verifying installation..." : "Cleaning registry...";

    public string ProgressLaneFiveTitle => IsInstallMode ? "Finalising Setup..." : "Finishing cleanup...";

    public string ProgressLaneOnePercentText => FormatLanePercent(GetLaneVisualPercent(0.6));

    public string ProgressLaneTwoPercentText => FormatLanePercent(GetLaneVisualPercent(1.4));

    public string ProgressLaneThreePercentText => FormatLanePercent(GetLaneVisualPercent(0.8));

    public string ProgressLaneFourPercentText => FormatLanePercent(GetLaneVisualPercent(0.5));

    public string ProgressLaneFivePercentText => FormatLanePercent(GetLaneVisualPercent(0.2));

    public string ProgressTitleText
    {
        get
        {
            if (string.Equals(PhaseText, "FAILED", StringComparison.OrdinalIgnoreCase))
            {
                return IsInstallMode ? "Installing Audiobit" : "Removing Audiobit";
            }

            if (_closeOnPrimaryAction)
            {
                return IsInstallMode ? "Audiobit Installed" : "Audiobit Removed";
            }

            return IsInstallMode ? "Installing Audiobit" : "Removing Audiobit";
        }
    }

    public double RemainingProgressValue => Math.Max(100d - ProgressValue, 0d);

    public string SetupSummaryText
    {
        get
        {
            if (!IsInstallMode)
            {
                return "Setup cleans Windows registration, shortcuts, pins, and the installed files.";
            }

            var selections = new List<string>();

            if (CreateStartMenuShortcut)
            {
                selections.Add("Start menu shortcut");
            }

            if (CreateDesktopShortcut)
            {
                selections.Add("desktop shortcut");
            }

            if (PinToStart)
            {
                selections.Add("Start pin");
            }

            if (PinToTaskbar)
            {
                selections.Add("taskbar pin");
            }

            if (LaunchAfterInstall)
            {
                selections.Add("launch after install");
            }

            return selections.Count == 0
                ? "No extra shell actions selected."
                : $"Selected: {string.Join(" | ", selections)}.";
        }
    }

    public bool ShowLaunchButton => IsInstallMode && ShowProgressStep && CanLaunch;

    public bool ShowLocationStep => IsInstallMode && _currentStep == InstallerStep.Location;

    public bool ShowOptionsStep => IsInstallMode && _currentStep == InstallerStep.Options;

    public bool ShowPinningNote => IsInstallMode && (PinToStart || PinToTaskbar);

    public bool ShowProgressStep => IsUninstallMode || _currentStep == InstallerStep.Progress;

    public bool ShowProgressSecondaryActionButton => ShowLaunchButton || ShowReturnToOptionsButton;

    public bool ShowProgressStatusPill => !ShowProgressSecondaryActionButton;

    public bool ShowReturnToOptionsButton =>
        IsInstallMode &&
        ShowProgressStep &&
        CanInstall &&
        !_closeOnPrimaryAction &&
        !CanLaunch;

    public bool CanUseProgressSecondaryAction => ShowProgressStep;

    public string ProgressSecondaryActionText
    {
        get
        {
            if (ShowLaunchButton)
            {
                return "Launch";
            }

            if (!CanInstall && !_closeOnPrimaryAction)
            {
                return "Cancel";
            }

            if (ShowReturnToOptionsButton)
            {
                return "Back";
            }

            return "Close";
        }
    }

    public string ProgressStatusPillText => _closeOnPrimaryAction ? "Done" : PhaseText;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string WebsiteDisplayText => "audiobit.amii.lol";

    public string GitHubDisplayText => "@ami-nope";

    public string PublisherLineText => "Publisher : Ami";

    public string VersionLineText => $"Version : {LatestVersionText}";

    public string SourceLineText => "Source : Github Releases";

    public string RatingCountDisplayText =>
        _websiteRatingCount.HasValue
            ? $"{Math.Max(_websiteRatingCount.Value, 0):00} RATINGS"
            : "00 RATINGS";

    public string RatingDisplayText =>
        _websiteRatingValue.HasValue
            ? _websiteRatingValue.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : "--";

    public string RatingStarsText => BuildRatingStars(_websiteRatingValue);

    public string RatingStarsDisplayText => BuildVisibleRatingStars(_websiteRatingValue);

    public string LanguageText => "EN";

    public string OptionsPrimaryActionText => IsInstallMode ? "Next" : "Remove";

    public string GitHubLinkToolTipText =>
        _websiteStarsCount.HasValue
            ? $"{InstallerMetadataService.GitHubProfileUrl} | {_websiteStarsCount.Value:n0} stars"
            : InstallerMetadataService.GitHubProfileUrl;

    public string WebsiteLinkToolTipText =>
        _websiteRatingValue.HasValue
            ? $"{WebsiteDisplayText} | {RatingDisplayText}/5"
            : WebsiteDisplayText;

    public string GitHubButtonToolTipText =>
        _websiteStarsCount.HasValue
            ? $"{InstallerMetadataService.GitHubProfileUrl} • {_websiteStarsCount.Value:n0} stars"
            : InstallerMetadataService.GitHubProfileUrl;

    public string WebsiteButtonToolTipText =>
        _websiteRatingValue.HasValue
            ? $"{WebsiteDisplayText} • {RatingDisplayText}/5"
            : WebsiteDisplayText;

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

    private async void MainWindowOnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyRoundedWindowRegion();
        ApplyRoundedVisualClips();
        AnimateProgressVisuals(true);
        await LoadMetadataAsync();
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

    private void MainWindowOnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyRoundedWindowRegion();
        ApplyRoundedVisualClips();
    }

    private void MainWindowOnStateChanged(object? sender, EventArgs e)
    {
        ApplyRoundedWindowRegion();
    }

    private async Task LoadMetadataAsync()
    {
        try
        {
            var snapshot = await _metadataService.LoadAsync();
            LatestVersionText = snapshot.LatestVersionText;
            SetWebsiteStats(snapshot.RatingValue, snapshot.RatingCount, snapshot.StarsCount);
            MetadataStatusText = snapshot.StatusText;
            MetadataFootnoteText = snapshot.StatusText switch
            {
                "Live website stats" => "Version syncs from GitHub releases. Rating and stars are pulled from audiobit.amii.lol.",
                "Website unavailable, using GitHub stars" => "Version syncs from GitHub releases. The website was unavailable, so the star count fell back to GitHub.",
                _ => "Version syncs from GitHub releases. Website metrics could not be loaded on this network."
            };
        }
        catch (Exception ex)
        {
            InstallerLogger.Log($"Installer metadata load failed: {ex}");
            LatestVersionText = AppVersionInfo.GetDisplayVersion(GetType().Assembly);
            SetWebsiteStats(null, null, null);
            MetadataStatusText = "Metadata unavailable";
            MetadataFootnoteText = "GitHub releases and website stats could not be reached.";
        }
    }

    private void ProceedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanProceedFromLocation)
        {
            return;
        }

        SetCurrentStep(InstallerStep.Options);
        RefreshState();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInstall)
        {
            return;
        }

        if (ShowOptionsStep)
        {
            SetCurrentStep(InstallerStep.Location);
            RefreshState();
            return;
        }

        if (ShowReturnToOptionsButton)
        {
            SetCurrentStep(InstallerStep.Options);
            RefreshState();
        }
    }

    private void BrowsePathButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInstall || !IsInstallMode)
        {
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose where AudioBit should be installed.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(InstallPath)
                ? InstallPath
                : Path.GetDirectoryName(InstallPath) ?? DefaultInstallPath
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            InstallPath = dialog.SelectedPath;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanInstall)
        {
            StatusText = "Wait for the current operation to finish before closing.";
            return;
        }

        Close();
    }

    private void GitHubButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl(InstallerMetadataService.GitHubProfileUrl);
    }

    private void WebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        OpenExternalUrl(InstallerMetadataService.WebsiteUrl);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        TryBeginWindowDrag(e);
    }

    private void ProgressSecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (ShowLaunchButton)
        {
            TryLaunchInstalledApp(false);
            return;
        }

        if (!CanInstall && !_closeOnPrimaryAction)
        {
            SetStatus("RUNNING", "Cancellation is not available once setup starts.");
            return;
        }

        if (ShowReturnToOptionsButton)
        {
            BackButton_Click(sender, e);
            return;
        }

        Close();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closeOnPrimaryAction)
        {
            Close();
            return;
        }

        if (!CanRunPrimaryAction)
        {
            return;
        }

        if (IsInstallMode)
        {
            SetCurrentStep(InstallerStep.Progress);
        }

        CanInstall = false;
        CanLaunch = false;
        InstallButtonText = IsInstallMode ? "Installing...." : "Removing....";
        SetProgress(
            "PREPARING",
            IsInstallMode ? "Starting the AudioBit install." : "Starting the AudioBit uninstall.",
            2);

        var progress = new Progress<InstallerProgress>(UpdateProgress);

        try
        {
            if (IsInstallMode)
            {
                var options = new InstallerOptions(
                    CreateDesktopShortcut,
                    CreateStartMenuShortcut,
                    PinToStart,
                    PinToTaskbar);

                await _installerEngine.InstallAsync(InstallPath, progress, options: options);

                _closeOnPrimaryAction = true;
                CanLaunch = true;
                InstallButtonText = "Close";
                NotifyProgressPresentationChanged();
                SetProgress("COMPLETE", "AudioBit was installed successfully and registered with Windows.", 100);

                if (LaunchAfterInstall)
                {
                    TryLaunchInstalledApp(true);
                }
            }
            else
            {
                await _installerEngine.UninstallAsync(InstallPath, progress);
                _closeOnPrimaryAction = true;
                InstallButtonText = "Close";
                NotifyProgressPresentationChanged();
                SetProgress("COMPLETE", "AudioBit was removed from Windows successfully.", 100);
            }
        }
        catch (Exception ex)
        {
            InstallerLogger.Log($"Interactive {_mode} failed: {ex}");
            InstallButtonText = IsInstallMode ? "Retry Install" : "Retry Remove";
            SetProgress("FAILED", SimplifyException(ex), 0);
        }
        finally
        {
            CanInstall = true;
            RefreshPayloadSummary();
            OnPropertyChanged(nameof(ShowLaunchButton));
            OnPropertyChanged(nameof(ShowReturnToOptionsButton));
            OnPropertyChanged(nameof(ShowProgressSecondaryActionButton));
            OnPropertyChanged(nameof(ShowProgressStatusPill));
            OnPropertyChanged(nameof(CanUseProgressSecondaryAction));
            OnPropertyChanged(nameof(ProgressSecondaryActionText));
            OnPropertyChanged(nameof(ProgressStatusPillText));
        }
    }

    private void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        TryLaunchInstalledApp(false);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void RefreshPayloadSummary()
    {
        if (IsInstallMode)
        {
            PayloadSummaryText = _installerEngine.GetPayloadSummaryText();
            return;
        }

        if (!Directory.Exists(InstallPath))
        {
            PayloadSummaryText = "Installed files were not found at the selected path.";
            return;
        }

        PayloadSummaryText = $"Installed footprint: {FormatSize(GetDirectorySize(InstallPath))}";
    }

    private void RefreshState()
    {
        if (!CanInstall)
        {
            return;
        }

        RefreshPayloadSummary();

        if (string.IsNullOrWhiteSpace(InstallPath))
        {
            CanLaunch = false;
            if (!_closeOnPrimaryAction)
            {
                InstallButtonText = IsInstallMode ? "Install AudioBit" : "Remove AudioBit";
            }

            SetStatus(
                "PATH REQUIRED",
                IsInstallMode
                    ? "Choose where AudioBit should be installed."
                    : "Choose the installed AudioBit location.");
            return;
        }

        var installedExecutable = InstallerPaths.GetInstalledExecutablePath(InstallPath);
        if (IsInstallMode)
        {
            CanLaunch = File.Exists(installedExecutable);

            if (_closeOnPrimaryAction)
            {
                return;
            }

            InstallButtonText = CanLaunch ? "Reinstall AudioBit" : "Install AudioBit";
            SetStatus(
                "READY",
                CanLaunch
                    ? "AudioBit is already present at this location. Continuing will replace the existing files."
                    : "Ready to install AudioBit for the current Windows user.");
            return;
        }

        CanLaunch = false;
        if (_closeOnPrimaryAction)
        {
            return;
        }

        InstallButtonText = "Remove AudioBit";
        SetStatus(
            "READY",
            Directory.Exists(InstallPath) || File.Exists(installedExecutable)
                ? "AudioBit is ready to be removed from Windows."
                : "Installed files were not found, but Windows registration and shell entries can still be cleaned up.");

        OnPropertyChanged(nameof(CanUseProgressSecondaryAction));
        OnPropertyChanged(nameof(ShowProgressSecondaryActionButton));
        OnPropertyChanged(nameof(ShowProgressStatusPill));
        OnPropertyChanged(nameof(ProgressSecondaryActionText));
        OnPropertyChanged(nameof(ProgressStatusPillText));
    }

    private void SetCurrentStep(InstallerStep step)
    {
        if (_currentStep == step)
        {
            return;
        }

        _currentStep = step;
        OnPropertyChanged(nameof(ShowLocationStep));
        OnPropertyChanged(nameof(ShowOptionsStep));
        OnPropertyChanged(nameof(ShowProgressStep));
        OnPropertyChanged(nameof(ShowLaunchButton));
        OnPropertyChanged(nameof(ShowReturnToOptionsButton));
        OnPropertyChanged(nameof(CanUseProgressSecondaryAction));
        OnPropertyChanged(nameof(ShowProgressSecondaryActionButton));
        OnPropertyChanged(nameof(ShowProgressStatusPill));
        OnPropertyChanged(nameof(ProgressSecondaryActionText));
        OnPropertyChanged(nameof(ProgressStatusPillText));
    }

    private void SetProgress(string phase, string status, double percent)
    {
        PhaseText = phase;
        StatusText = status;
        ProgressValue = Math.Clamp(percent, 0, 100);
        ProgressPercentText = $"{Math.Round(ProgressValue):0}%";
        AnimateProgressVisuals();
    }

    private void SetStatus(string phase, string status)
    {
        PhaseText = phase;
        StatusText = status;
    }

    private string SimplifyException(Exception ex)
    {
        return ex switch
        {
            UnauthorizedAccessException => IsInstallMode
                ? "Setup hit a Windows file permission lock. Close other setup windows and retry."
                : "Uninstall could not remove files from the selected path.",
            FileNotFoundException => IsInstallMode
                ? "Setup payload is missing from the installer folder."
                : "AudioBit files were already removed from the selected path.",
            IOException => IsInstallMode
                ? "Setup could not replace the existing files. Make sure AudioBit is closed."
                : "Uninstall could not remove the files. Make sure AudioBit is closed.",
            _ => ex.Message
        };
    }

    private void TryLaunchInstalledApp(bool closeAfterLaunch)
    {
        var installedExecutable = InstallerPaths.GetInstalledExecutablePath(InstallPath);
        if (!File.Exists(installedExecutable))
        {
            SetStatus("NOT FOUND", "AudioBit is not installed at the selected path.");
            CanLaunch = false;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(installedExecutable)
            {
                UseShellExecute = true,
                WorkingDirectory = InstallPath
            });

            if (closeAfterLaunch)
            {
                Close();
            }
        }
        catch (Exception ex)
        {
            InstallerLogger.Log($"Launch failed: {ex}");
            SetStatus("LAUNCH FAILED", "AudioBit installed successfully, but Windows could not launch it.");
            CanLaunch = true;
        }
    }

    private void UpdateProgress(InstallerProgress progress)
    {
        SetProgress(progress.Phase, progress.Status, progress.Percent);
    }

    private void TopBar_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || HasAncestor<System.Windows.Controls.Primitives.ButtonBase>(source)
            || HasAncestor<System.Windows.Controls.Primitives.ScrollBar>(source)
            || HasAncestor<Selector>(source)
            || HasAncestor<System.Windows.Controls.Primitives.TextBoxBase>(source))
        {
            return;
        }

        TryBeginWindowDrag(e);
    }

    private void ShellBackground_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        TryBeginWindowDrag(e);
    }

    private void TryBeginWindowDrag(MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            
        }
    }

    private void OpenExternalUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            InstallerLogger.Log($"Failed to open external URL '{url}': {ex}");
            SetStatus("LINK FAILED", "Windows could not open the requested link.");
        }
    }

    private void AnimateProgressVisuals(bool immediate = false)
    {
        if (MainProgressFill is null
            || LaneOneFill is null
            || LaneTwoFill is null
            || LaneThreeFill is null
            || LaneFourFill is null
            || LaneFiveFill is null)
        {
            return;
        }

        var mainValue = ProgressValue / 100d;
        var laneOne = GetLaneVisualPercent(0.6) / 100d;
        var laneTwo = GetLaneVisualPercent(1.4) / 100d;
        var laneThree = GetLaneVisualPercent(0.8) / 100d;
        var laneFour = GetLaneVisualPercent(0.5) / 100d;
        var laneFive = GetLaneVisualPercent(0.2) / 100d;

        SetFillScale(MainProgressFill, mainValue, immediate);
        SetFillScale(LaneOneFill, laneOne, immediate);
        SetFillScale(LaneTwoFill, laneTwo, immediate);
        SetFillScale(LaneThreeFill, laneThree, immediate);
        SetFillScale(LaneFourFill, laneFour, immediate);
        SetFillScale(LaneFiveFill, laneFive, immediate);
    }

    private double GetLaneVisualPercent(double multiplier)
    {
        if (_closeOnPrimaryAction || string.Equals(PhaseText, "COMPLETE", StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        var percent = ProgressValue * multiplier;
        if (string.Equals(PhaseText, "FAILED", StringComparison.OrdinalIgnoreCase))
        {
            percent = Math.Max(percent, 8);
        }

        return Math.Clamp(Math.Round(percent), 0, 100);
    }

    private static string FormatLanePercent(double percent)
    {
        return $"{Math.Round(percent):0} %";
    }

    private void SetWebsiteStats(double? ratingValue, int? ratingCount, int? starsCount)
    {
        _websiteRatingValue = ratingValue;
        _websiteRatingCount = ratingCount;
        _websiteStarsCount = starsCount;

        OnPropertyChanged(nameof(RatingCountDisplayText));
        OnPropertyChanged(nameof(RatingDisplayText));
        OnPropertyChanged(nameof(RatingStarsText));
        OnPropertyChanged(nameof(RatingStarsDisplayText));
        OnPropertyChanged(nameof(GitHubButtonToolTipText));
        OnPropertyChanged(nameof(WebsiteButtonToolTipText));
        OnPropertyChanged(nameof(GitHubLinkToolTipText));
        OnPropertyChanged(nameof(WebsiteLinkToolTipText));
    }

    private void NotifyProgressPresentationChanged()
    {
        OnPropertyChanged(nameof(ProgressTitleText));
        OnPropertyChanged(nameof(ProgressSecondaryActionText));
        OnPropertyChanged(nameof(ProgressStatusPillText));
        OnPropertyChanged(nameof(ProgressLaneOnePercentText));
        OnPropertyChanged(nameof(ProgressLaneTwoPercentText));
        OnPropertyChanged(nameof(ProgressLaneThreePercentText));
        OnPropertyChanged(nameof(ProgressLaneFourPercentText));
        OnPropertyChanged(nameof(ProgressLaneFivePercentText));
    }

    private static string BuildVisibleRatingStars(double? ratingValue)
    {
        const string filledStar = "*";
        const string emptyStar = "-";

        if (!ratingValue.HasValue)
        {
            return string.Join(" ", Enumerable.Repeat(emptyStar, 5));
        }

        var filledStars = Math.Clamp((int)Math.Round(ratingValue.Value, MidpointRounding.AwayFromZero), 0, 5);
        var stars = Enumerable
            .Repeat(filledStar, filledStars)
            .Concat(Enumerable.Repeat(emptyStar, 5 - filledStars));
        return string.Join(" ", stars);
    }

    private static string BuildRatingStars(double? ratingValue)
    {
        if (!ratingValue.HasValue)
        {
            return "-----";
        }

        var filledStars = Math.Clamp((int)Math.Round(ratingValue.Value, MidpointRounding.AwayFromZero), 0, 5);
        return string.Concat(new string('*', filledStars), new string('-', 5 - filledStars));
    }

    private static void SetFillScale(Border border, double scale, bool immediate)
    {
        if (border.RenderTransform is not ScaleTransform transform)
        {
            return;
        }

        var target = Math.Clamp(scale, 0, 1);
        if (immediate)
        {
            transform.ScaleX = target;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnOptionSelectionChanged()
    {
        OnPropertyChanged(nameof(SetupSummaryText));
        OnPropertyChanged(nameof(ShowPinningNote));
    }

    private bool IsDefaultInstallPath()
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(InstallPath).TrimEnd('\\'),
                Path.GetFullPath(DefaultInstallPath).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
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

        var regionHandle = CreateRoundRectRgn(0, 0, width + 1, height + 1, cornerDiameterX, cornerDiameterY);
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
        ApplyRoundedClip(ShellClipHost, ShellClipCornerRadius);
    }

    private static void ApplyRoundedClip(FrameworkElement element, double radius)
    {
        var clipWidth = element.ActualWidth - (ShellClipInset * 2);
        var clipHeight = element.ActualHeight - (ShellClipInset * 2);
        if (clipWidth <= 0 || clipHeight <= 0)
        {
            return;
        }

        var adjustedRadius = Math.Max(0, radius - ShellClipInset);
        var clip = new RectangleGeometry(
            new Rect(ShellClipInset, ShellClipInset, clipWidth, clipHeight),
            adjustedRadius,
            adjustedRadius);
        if (clip.CanFreeze)
        {
            clip.Freeze();
        }

        element.Clip = clip;
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

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is FrameworkContentElement contentElement)
        {
            return contentElement.Parent ?? contentElement.TemplatedParent;
        }

        return VisualTreeHelper.GetParent(child);
    }

    private static long GetDirectorySize(string directoryPath)
    {
        return Directory
            .EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
            .Select(filePath => new FileInfo(filePath).Length)
            .Sum();
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.#} {units[unitIndex]}";
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
    {
        if (!string.IsNullOrWhiteSpace(propertyName))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
