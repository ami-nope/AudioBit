using System.Windows.Media;

namespace AudioBit.UI;

internal static class GpuMeterRenderLoop
{
    private static readonly HashSet<GpuPeakMeterControl> Controls = [];

    private static bool _isSubscribed;
    private static TimeSpan _lastRenderingTime = TimeSpan.MinValue;

    public static void Register(GpuPeakMeterControl control)
    {
        Controls.Add(control);
        UpdateSubscription();
    }

    public static void Unregister(GpuPeakMeterControl control)
    {
        Controls.Remove(control);
        UpdateSubscription();
    }

    public static void NotifyStateChanged()
    {
        UpdateSubscription();
    }

    private static void UpdateSubscription()
    {
        var shouldRun = Controls.Any(control => control.IsRenderActive);
        if (shouldRun && !_isSubscribed)
        {
            _lastRenderingTime = TimeSpan.MinValue;
            CompositionTarget.Rendering += OnRendering;
            _isSubscribed = true;
            return;
        }

        if (!shouldRun && _isSubscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isSubscribed = false;
            _lastRenderingTime = TimeSpan.MinValue;
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var renderingTime = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        var frameDelta = _lastRenderingTime == TimeSpan.MinValue
            ? TimeSpan.FromSeconds(1.0 / 60.0)
            : renderingTime - _lastRenderingTime;

        if (frameDelta <= TimeSpan.Zero || frameDelta > TimeSpan.FromSeconds(0.25))
        {
            frameDelta = TimeSpan.FromSeconds(1.0 / 60.0);
        }

        _lastRenderingTime = renderingTime;

        var activeControls = 0;
        foreach (var control in Controls.ToArray())
        {
            if (!control.IsLoaded)
            {
                continue;
            }

            if (!control.IsRenderActive)
            {
                continue;
            }

            activeControls++;
            control.AdvanceFrame(frameDelta);
        }

        if (activeControls == 0)
        {
            UpdateSubscription();
        }
    }
}
