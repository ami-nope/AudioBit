using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AudioBit.App.Infrastructure;

internal static class AudioBitIconLoader
{
    private static readonly Uri IconUri = new(
        "pack://application:,,,/AudioBit.App;component/Assets/AudioBit.ico",
        UriKind.Absolute);

    private static readonly Lazy<BitmapFrame[]> IconFrames = new(LoadFrames);

    public static ImageSource WindowIcon => SelectFrame(64);

    public static BitmapFrame TrayIcon => SelectFrame(32);

    private static BitmapFrame[] LoadFrames()
    {
        var streamInfo = Application.GetResourceStream(IconUri)
            ?? throw new InvalidOperationException("AudioBit icon resource was not found.");

        using var stream = streamInfo.Stream;

        var decoder = new IconBitmapDecoder(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        return decoder.Frames
            .Select(frame =>
            {
                var clone = BitmapFrame.Create(frame);
                if (clone.CanFreeze)
                {
                    clone.Freeze();
                }

                return clone;
            })
            .ToArray();
    }

    private static BitmapFrame SelectFrame(int targetSize)
    {
        return IconFrames.Value
            .OrderBy(frame => Math.Abs(frame.PixelWidth - targetSize))
            .ThenByDescending(frame => frame.PixelWidth)
            .First();
    }
}
