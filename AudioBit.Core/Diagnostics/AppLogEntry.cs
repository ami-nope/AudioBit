namespace AudioBit.Core.Diagnostics;

public sealed record AppLogEntry(
    long SequenceId,
    DateTimeOffset TimestampUtc,
    AppLogLevel Level,
    string Category,
    string Message,
    string? Details,
    int ManagedThreadId)
{
    public string ToDisplayLine()
    {
        return $"[{TimestampUtc:O}] [#{SequenceId:D8}] [{Level}] [{Category}] {Message}";
    }
}
