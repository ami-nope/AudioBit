using System.Text.Json.Serialization;

namespace AudioBit.App.Models;

public sealed class RemoteStateModel
{
    [JsonPropertyName("t")]
    public string Type { get; set; } = "state";

    [JsonPropertyName("sid")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("rev")]
    public long Revision { get; set; }

    [JsonPropertyName("master_volume")]
    public int MasterVolume { get; set; }

    [JsonPropertyName("master_muted")]
    public bool MasterMuted { get; set; }

    [JsonPropertyName("mic_muted")]
    public bool MicMuted { get; set; }

    [JsonPropertyName("default_output_device")]
    public string DefaultOutputDevice { get; set; } = string.Empty;

    [JsonPropertyName("default_input_device")]
    public string DefaultInputDevice { get; set; } = string.Empty;

    [JsonPropertyName("output_devices")]
    public List<RemoteDeviceModel> OutputDevices { get; set; } = [];

    [JsonPropertyName("input_devices")]
    public List<RemoteDeviceModel> InputDevices { get; set; } = [];

    [JsonPropertyName("apps")]
    public List<RemoteAppStateModel> Apps { get; set; } = [];
}

public sealed class RemoteDeviceModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class RemoteAppStateModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("pid")]
    public int ProcessId { get; set; }

    [JsonPropertyName("volume")]
    public int Volume { get; set; }

    [JsonPropertyName("muted")]
    public bool Muted { get; set; }

    [JsonPropertyName("output_device")]
    public string OutputDevice { get; set; } = string.Empty;

    [JsonPropertyName("input_device")]
    public string InputDevice { get; set; } = string.Empty;

    [JsonPropertyName("peak")]
    public int Peak { get; set; }
}

public sealed class RemoteLevelUpdateModel
{
    [JsonPropertyName("t")]
    public string Type { get; set; } = "lvl";

    [JsonPropertyName("sid")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("rev")]
    public long Revision { get; set; }

    [JsonPropertyName("apps")]
    public List<object[]> Apps { get; set; } = [];
}
