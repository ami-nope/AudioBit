namespace AudioBit.Core;

public sealed class AudioDeviceOptionModel
{
    public AudioDeviceOptionModel(string id, string displayName, AudioDeviceFlow flow, bool isSystemDefault = false)
    {
        Id = id;
        DisplayName = displayName;
        Flow = flow;
        IsSystemDefault = isSystemDefault;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public AudioDeviceFlow Flow { get; }

    public bool IsSystemDefault { get; }
}
