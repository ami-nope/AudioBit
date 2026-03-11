namespace AudioBit.App.Models;

public sealed class RemoteSessionInfo
{
    public static readonly RemoteSessionInfo Empty = new();

    public string SessionId { get; init; } = string.Empty;

    public string PairCode { get; init; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; init; } = DateTimeOffset.MinValue;

    public string QrUrl { get; init; } = string.Empty;

    public bool IsRelayConnected { get; init; }

    public bool IsConnected { get; init; }

    public string Status { get; init; } = "Disconnected";

    public string DeviceId { get; init; } = string.Empty;

    public string DeviceName { get; init; } = string.Empty;

    public string DeviceLocation { get; init; } = string.Empty;

    public string ConnectionType { get; init; } = string.Empty;

    public string IpAddress { get; init; } = string.Empty;

    public string UserAgent { get; init; } = string.Empty;

    public int? DeviceLatencyMs { get; init; }

    public DateTimeOffset DeviceLatencyUpdatedAtUtc { get; init; } = DateTimeOffset.MinValue;

    public int? ExistingDeviceCount { get; init; }

    public int? ConnectedDeviceCount { get; init; }

    public string RelayRouteLabel { get; init; } = string.Empty;

    public int? RelayProbeLatencyMs { get; init; }
}
