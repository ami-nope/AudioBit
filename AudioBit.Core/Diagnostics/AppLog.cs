using System.Diagnostics;

namespace AudioBit.Core.Diagnostics;

public static class AppLog
{
    private const int MaxEntries = 20000;
    private static readonly object Gate = new();
    private static readonly List<AppLogEntry> Entries = [];
    private static long _sequenceCounter;

    public static event Action<AppLogEntry>? EntryWritten;

    public static void Trace(string category, string message, string? details = null)
    {
        Write(category, message, AppLogLevel.Trace, details);
    }

    public static void Info(string category, string message, string? details = null)
    {
        Write(category, message, AppLogLevel.Info, details);
    }

    public static void Warning(string category, string message, string? details = null)
    {
        Write(category, message, AppLogLevel.Warning, details);
    }

    public static void Error(string category, string message, string? details = null)
    {
        Write(category, message, AppLogLevel.Error, details);
    }

    public static void Error(string category, string message, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var details = AppLogExceptionFormatter.Format(category, message, exception);
        Write(category, message, AppLogLevel.Error, details);
    }

    public static void Write(string category, string message, AppLogLevel level = AppLogLevel.Info, string? details = null)
    {
        var normalizedCategory = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
        var normalizedMessage = string.IsNullOrWhiteSpace(message) ? "(empty message)" : message.TrimEnd();
        var normalizedDetails = string.IsNullOrWhiteSpace(details) ? null : details.Trim();
        var entry = new AppLogEntry(
            Interlocked.Increment(ref _sequenceCounter),
            DateTimeOffset.UtcNow,
            level,
            normalizedCategory,
            normalizedMessage,
            normalizedDetails,
            Environment.CurrentManagedThreadId);

        Action<AppLogEntry>? handlers;

        lock (Gate)
        {
            Entries.Add(entry);
            if (Entries.Count > MaxEntries)
            {
                Entries.RemoveRange(0, Entries.Count - MaxEntries);
            }

            handlers = EntryWritten;
        }

        Debug.WriteLine(entry.ToDisplayLine());
        if (!string.IsNullOrWhiteSpace(entry.Details))
        {
            Debug.WriteLine(entry.Details);
        }

        handlers?.Invoke(entry);
    }

    public static IReadOnlyList<AppLogEntry> GetEntriesSince(DateTimeOffset sinceUtc, int maxEntries = 800)
    {
        if (maxEntries <= 0)
        {
            return [];
        }

        lock (Gate)
        {
            var filtered = Entries.Where(entry => entry.TimestampUtc >= sinceUtc).ToArray();
            if (filtered.Length <= maxEntries)
            {
                return filtered;
            }

            return filtered[^maxEntries..];
        }
    }

    public static IReadOnlyList<AppLogEntry> GetRecentEntries(TimeSpan window, int maxEntries = 800)
    {
        if (window <= TimeSpan.Zero)
        {
            return [];
        }

        return GetEntriesSince(DateTimeOffset.UtcNow - window, maxEntries);
    }
}
