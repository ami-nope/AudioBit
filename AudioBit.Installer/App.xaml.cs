using System.Windows;

namespace AudioBit.Installer;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mode = ResolveMode(e.Args);
        var installPath = ResolveInstallPath(e.Args, mode);
        var isSilent = e.Args.Any(arg =>
            string.Equals(arg, "--install-silent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--uninstall-silent", StringComparison.OrdinalIgnoreCase));

        if (isSilent)
        {
            var engine = new InstallerEngine(InstallerPaths.GetPayloadPath(AppContext.BaseDirectory));
            var targetPath = string.IsNullOrWhiteSpace(installPath)
                ? InstallerPaths.GetDefaultInstallPath()
                : installPath;

            try
            {
                if (mode == InstallerMode.Uninstall)
                {
                    await engine.UninstallAsync(targetPath, new Progress<InstallerProgress>(_ => { }));
                }
                else
                {
                    await engine.InstallAsync(targetPath, new Progress<InstallerProgress>(_ => { }));
                }

                Shutdown(0);
            }
            catch (Exception ex)
            {
                InstallerLogger.Log($"Silent {mode} failed: {ex}");
                Shutdown(1);
            }

            return;
        }

        MainWindow = new MainWindow(mode, installPath);
        MainWindow.Show();
    }

    private static string ResolveInstallPath(IReadOnlyList<string> args, InstallerMode mode)
    {
        var flagValue = TryGetOptionValue(args, "--install-path");
        if (!string.IsNullOrWhiteSpace(flagValue))
        {
            return flagValue;
        }

        if (mode == InstallerMode.Install)
        {
            var legacyValue = TryGetPositionalValue(args, "--install-silent");
            if (!string.IsNullOrWhiteSpace(legacyValue))
            {
                return legacyValue;
            }
        }

        return InstallerPaths.GetDefaultInstallPath();
    }

    private static InstallerMode ResolveMode(IReadOnlyList<string> args)
    {
        return args.Any(arg =>
            string.Equals(arg, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--uninstall-silent", StringComparison.OrdinalIgnoreCase))
            ? InstallerMode.Uninstall
            : InstallerMode.Install;
    }

    private static string? TryGetOptionValue(IReadOnlyList<string> args, string optionName)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], optionName, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string? TryGetPositionalValue(IReadOnlyList<string> args, string modeSwitch)
    {
        if (args.Count == 0 || !string.Equals(args[0], modeSwitch, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (args.Count > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            return args[1];
        }

        return string.Empty;
    }
}
