namespace AudioBit.Core.Diagnostics;

public static class AppLogSeverityClassifier
{
    private static readonly string[] ErrorKeywords =
    [
        "failed",
        "failure",
        "error",
        "exception",
        "crash",
        "unable",
        "timed out",
        "timeout",
        "not connected",
    ];

    private static readonly string[] WarningKeywords =
    [
        "lost",
        "closed",
        "unavailable",
        "skipped",
        "retry",
        "disconnect",
    ];

    public static AppLogLevel Infer(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return AppLogLevel.Info;
        }

        foreach (var keyword in ErrorKeywords)
        {
            if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return AppLogLevel.Error;
            }
        }

        foreach (var keyword in WarningKeywords)
        {
            if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return AppLogLevel.Warning;
            }
        }

        return AppLogLevel.Info;
    }
}
