using System.Windows.Media;

namespace AudioBit.Core;

public sealed class AppAudioModel
{
    public const float SilenceThreshold = 0.01f;

    public int ProcessId { get; set; }

    public string AppName { get; set; } = string.Empty;

    public ImageSource? Icon { get; set; }

    public float Volume { get; set; } = 1.0f;

    public float Peak { get; set; }

    public bool IsMuted { get; set; }

    public DateTime LastAudioTime { get; set; } = DateTime.UtcNow;

    public bool IsActive => Peak > SilenceThreshold;

    public double Opacity => IsActive ? 1.0 : 0.5;

    public AppAudioModel Clone()
    {
        return new AppAudioModel
        {
            ProcessId = ProcessId,
            AppName = AppName,
            Icon = Icon,
            Volume = Volume,
            Peak = Peak,
            IsMuted = IsMuted,
            LastAudioTime = LastAudioTime,
        };
    }
}
