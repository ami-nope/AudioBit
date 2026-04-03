namespace AudioBit.Core;

public sealed class AudioDeviceOptionModel
{
    private const string SystemDefaultPrefix = "System default - ";

    public AudioDeviceOptionModel(string id, string displayName, AudioDeviceFlow flow, bool isSystemDefault = false)
    {
        Id = id;
        DisplayName = displayName;
        Flow = flow;
        IsSystemDefault = isSystemDefault;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string CompactDisplayName => IsSystemDefault && DisplayName.StartsWith(SystemDefaultPrefix, StringComparison.Ordinal)
        ? DisplayName[SystemDefaultPrefix.Length..]
        : DisplayName;

    public AudioDeviceFlow Flow { get; }

    public bool IsSystemDefault { get; }
}
