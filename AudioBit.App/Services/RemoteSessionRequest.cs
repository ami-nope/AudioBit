namespace AudioBit.App.Services;

internal readonly record struct RemoteSessionRequest(string SessionId, string PairCode)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(SessionId) && string.IsNullOrWhiteSpace(PairCode);
}
