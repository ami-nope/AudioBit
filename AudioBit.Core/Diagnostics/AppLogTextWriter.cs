namespace AudioBit.Core.Diagnostics;

public static class AppLogTextWriter
{
    public static void Write(string category, string message, AppLogLevel? level = null)
    {
        var normalizedMessage = string.IsNullOrWhiteSpace(message)
            ? "(empty message)"
            : message.Trim();
        var (summary, details) = SplitMessage(normalizedMessage);
        AppLog.Write(category, summary, level ?? AppLogSeverityClassifier.Infer(normalizedMessage), details);
    }

    public static void Write(
        string category,
        string message,
        Exception exception,
        AppLogLevel level = AppLogLevel.Error,
        IEnumerable<KeyValuePair<string, object?>>? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var normalizedMessage = string.IsNullOrWhiteSpace(message)
            ? exception.Message
            : message.Trim();
        var (summary, details) = SplitMessage(normalizedMessage);
        var combinedDetails = AppLogExceptionFormatter.Format(category, summary, exception, details, context);
        AppLog.Write(category, summary, level, combinedDetails);
    }

    private static (string Summary, string? Details) SplitMessage(string message)
    {
        var normalized = message
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        var firstLineBreak = normalized.IndexOf('\n');
        if (firstLineBreak < 0)
        {
            return (normalized, null);
        }

        var summary = normalized[..firstLineBreak].Trim();
        if (summary.Length == 0)
        {
            summary = "(details attached)";
        }

        var details = normalized[(firstLineBreak + 1)..].Trim();
        return (summary, details.Length == 0 ? null : details);
    }
}
