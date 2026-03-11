namespace AudioBit.App.Models;

public sealed class RemoteSessionHistoryEntry
{
    public RemoteSessionHistoryEntry(string timestampText, string sessionId, string status, string device)
    {
        TimestampText = timestampText;
        SessionId = sessionId;
        Status = status;
        Device = device;
        Summary = $"{timestampText}  {sessionId}  {status}  {device}";
    }

    public string TimestampText { get; }

    public string SessionId { get; }

    public string Status { get; }

    public string Device { get; }

    public string Summary { get; }
}
