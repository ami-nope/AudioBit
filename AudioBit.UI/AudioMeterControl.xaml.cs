using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AudioBit.UI;

public partial class AudioMeterControl : UserControl
{
    private const double PeakAttackPerSecond = 9.5;
    private const double PeakReleasePerSecond = 4.4;
    private const double PulseReleasePerSecond = 3.6;
    private const double BarAttackPerSecond = 18.0;
    private const double BarReleasePerSecond = 9.0;
    private const double DynamicBarThreshold = 0.003;
    private const double StopThreshold = 0.0025;

    public static readonly DependencyProperty PeakValueProperty = DependencyProperty.Register(
        nameof(PeakValue),
        typeof(double),
        typeof(AudioMeterControl),
        new PropertyMetadata(0.0, OnPeakValueChanged));

    public static readonly DependencyProperty CardOpacityProperty = DependencyProperty.Register(
        nameof(CardOpacity),
        typeof(double),
        typeof(AudioMeterControl),
        new PropertyMetadata(1.0, OnCardOpacityChanged));

    public static readonly DependencyProperty IsLowPerformanceModeProperty = DependencyProperty.Register(
        nameof(IsLowPerformanceMode),
        typeof(bool),
        typeof(AudioMeterControl),
        new PropertyMetadata(false, OnIsLowPerformanceModeChanged));

    private readonly double[] _barBias =
    [
        0.14,
        0.23,
        0.35,
        0.54,
        0.73,
        0.46,
        0.84,
        0.97,
        0.58,
        0.88,
        0.49,
        0.74,
        0.38,
        0.66,
    ];

    private bool _hasAnimatedIn;
    private bool _isRendering;
    private bool _hasDynamicBarEnergy;
    private long _lastRenderTimestamp;
    private double _spectrumTime;
    private double _targetPeak;
    private double _displayPeak;
    private double _peakPulse;
    private double _lastSamplePeak;
    private double[] _barFloors = Array.Empty<double>();
    private double[] _barScales = Array.Empty<double>();
    private ScaleTransform[]? _meterTransforms;

    public AudioMeterControl()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public double PeakValue
    {
        get => (double)GetValue(PeakValueProperty);
        set => SetValue(PeakValueProperty, value);
    }

    public double CardOpacity
    {
        get => (double)GetValue(CardOpacityProperty);
        set => SetValue(CardOpacityProperty, value);
    }

    public bool IsLowPerformanceMode
    {
        get => (bool)GetValue(IsLowPerformanceModeProperty);
        set => SetValue(IsLowPerformanceModeProperty, value);
    }

    private static void OnPeakValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AudioMeterControl control)
        {
            control.UpdatePeakSignal((double)e.NewValue);
        }
    }

    private static void OnCardOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AudioMeterControl control)
        {
            control.AnimateOpacity((double)e.NewValue);
        }
    }

    private static void OnIsLowPerformanceModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AudioMeterControl control)
        {
            control.ApplyPerformanceModeVisuals();
        }
    }

    private void RootGrid_OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureMeterTransforms();

        _hasAnimatedIn = true;
        _spectrumTime = 0.0;
        _targetPeak = Math.Clamp(PeakValue, 0.0, 1.0);
        _displayPeak = _targetPeak;
        _lastSamplePeak = _targetPeak;
        AdvanceSpectrum(1.0 / 60.0);
        RenderMeter();
        ApplyPerformanceModeVisuals();

        if (!IsLowPerformanceMode && _targetPeak > StopThreshold)
        {
            EnsureRendering();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopRendering();
    }

    private void UpdatePeakSignal(double targetPeak)
    {
        var clampedPeak = Math.Clamp(targetPeak, 0.0, 1.0);
        var rise = Math.Max(0.0, clampedPeak - _lastSamplePeak);
        if (rise > 0.015)
        {
            var transient = Math.Clamp((rise * 2.8) + (clampedPeak * 0.32), 0.0, 1.0);
            _peakPulse = Math.Max(_peakPulse, transient);
        }

        _lastSamplePeak = clampedPeak;
        _targetPeak = clampedPeak;

        if (!_hasAnimatedIn)
        {
            return;
        }

        if (IsLowPerformanceMode)
        {
            StopRendering();
            return;
        }

        if (_targetPeak <= StopThreshold && _displayPeak <= StopThreshold && _peakPulse <= StopThreshold)
        {
            RenderMeter();
            return;
        }

        EnsureRendering();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var frameSeconds = _lastRenderTimestamp == 0
            ? 1.0 / 60.0
            : Math.Clamp((now - _lastRenderTimestamp) / (double)Stopwatch.Frequency, 1.0 / 240.0, 1.0 / 20.0);

        _lastRenderTimestamp = now;

        AdvanceEnvelope(frameSeconds);
        AdvanceSpectrum(frameSeconds);
        RenderMeter();

        if (_targetPeak <= StopThreshold
            && _displayPeak <= StopThreshold
            && _peakPulse <= StopThreshold
            && !_hasDynamicBarEnergy)
        {
            StopRendering();
        }
    }

    private void AdvanceEnvelope(double frameSeconds)
    {
        if (_displayPeak < _targetPeak)
        {
            _displayPeak = MoveTowards(_displayPeak, _targetPeak, PeakAttackPerSecond * frameSeconds);
        }
        else
        {
            _displayPeak = MoveTowards(_displayPeak, _targetPeak, PeakReleasePerSecond * frameSeconds);
        }

        _peakPulse = MoveTowards(_peakPulse, 0.0, PulseReleasePerSecond * frameSeconds);
    }

    private void AdvanceSpectrum(double frameSeconds)
    {
        EnsureMeterTransforms();
        if (_meterTransforms is null)
        {
            return;
        }

        _spectrumTime += frameSeconds;
        _hasDynamicBarEnergy = false;

        for (var index = 0; index < _meterTransforms.Length; index++)
        {
            var position = index / Math.Max(1.0, _meterTransforms.Length - 1.0);
            var centerWeight = 1.0 - (Math.Abs(position - 0.5) * 2.0);
            var baseFloor = _barFloors[index];
            var envelopeWeight = 0.46 + (_barBias[index] * 0.34) + (centerWeight * 0.18);
            var transientWeight = 0.12 + (_barBias[index] * 0.10) + (centerWeight * 0.28);
            var envelope = _displayPeak * envelopeWeight;
            var transient = _peakPulse * transientWeight;
            var energy = Math.Clamp(envelope + transient, 0.0, 1.0);
            var targetScale = baseFloor + envelope + transient + ComputeOrganicMotion(index, position, centerWeight, energy);

            if (_displayPeak <= StopThreshold && _peakPulse <= StopThreshold)
            {
                targetScale = baseFloor;
            }

            targetScale = Math.Clamp(targetScale, baseFloor, 1.0);

            var response = _barScales[index] < targetScale
                ? BarAttackPerSecond + (_barBias[index] * 6.0) + (centerWeight * 3.0)
                : BarReleasePerSecond + (_barBias[index] * 4.0);

            _barScales[index] = DampedLerp(_barScales[index], targetScale, response, frameSeconds);

            if (_barScales[index] > baseFloor + DynamicBarThreshold)
            {
                _hasDynamicBarEnergy = true;
            }
        }
    }

    private double ComputeOrganicMotion(int index, double position, double centerWeight, double energy)
    {
        if (energy <= StopThreshold)
        {
            return 0.0;
        }

        var phase = (_barBias[index] * 2.2) + (index * 0.44);
        var wave = Math.Sin((_spectrumTime * (4.0 + (centerWeight * 1.2))) - (position * Math.PI * 3.5) + phase);
        var flutter = Math.Sin((_spectrumTime * (8.1 + (_barBias[index] * 1.8))) + (phase * 1.6));
        var jitter = Math.Sin((_spectrumTime * (11.8 + (index * 0.13))) + (phase * 2.4));
        var blend =
            (Math.Max(0.0, wave) * 0.58)
            + ((flutter + 1.0) * 0.22)
            + ((jitter + 1.0) * 0.10);
        var amplitude = (0.012 + (centerWeight * 0.022) + (_barBias[index] * 0.014))
            * (0.30 + (energy * 1.65));

        return blend * amplitude;
    }

    private void RenderMeter()
    {
        EnsureMeterTransforms();
        if (_meterTransforms is null)
        {
            return;
        }

        for (var index = 0; index < _meterTransforms.Length; index++)
        {
            _meterTransforms[index].ScaleY = _barScales[index];
        }
    }

    private void AnimateOpacity(double targetOpacity)
    {
        if (!_hasAnimatedIn)
        {
            return;
        }

        CardBorder.BeginAnimation(UIElement.OpacityProperty, null);
        CardBorder.Opacity = IsLowPerformanceMode ? 1.0 : targetOpacity;
    }

    private void EnsureMeterTransforms()
    {
        if (_meterTransforms is not null)
        {
            return;
        }

        _meterTransforms =
        [
            BarScale01,
            BarScale02,
            BarScale03,
            BarScale04,
            BarScale05,
            BarScale06,
            BarScale07,
            BarScale08,
            BarScale09,
            BarScale10,
            BarScale11,
            BarScale12,
            BarScale13,
            BarScale14,
        ];

        _barFloors = new double[_meterTransforms.Length];
        _barScales = new double[_meterTransforms.Length];

        for (var index = 0; index < _meterTransforms.Length; index++)
        {
            var baseFloor = 0.04 + (_barBias[index] * 0.015);
            _barFloors[index] = baseFloor;
            _barScales[index] = baseFloor;
        }
    }

    private void EnsureRendering()
    {
        if (_isRendering)
        {
            return;
        }

        _isRendering = true;
        _lastRenderTimestamp = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_isRendering)
        {
            return;
        }

        _isRendering = false;
        _lastRenderTimestamp = 0;
        CompositionTarget.Rendering -= OnRendering;
    }

    private static double MoveTowards(double current, double target, double amount)
    {
        if (current < target)
        {
            return Math.Min(target, current + amount);
        }

        return Math.Max(target, current - amount);
    }

    private static double DampedLerp(double current, double target, double rate, double frameSeconds)
    {
        if (Math.Abs(target - current) <= double.Epsilon)
        {
            return target;
        }

        var blend = 1.0 - Math.Exp(-Math.Max(0.0, rate) * frameSeconds);
        return current + ((target - current) * blend);
    }

    private void ApplyPerformanceModeVisuals()
    {
        ResetCardHoverVisuals();

        if (!_hasAnimatedIn)
        {
            return;
        }

        CardBorder.BeginAnimation(UIElement.OpacityProperty, null);
        CardBorder.Opacity = IsLowPerformanceMode ? 1.0 : CardOpacity;

        if (IsLowPerformanceMode)
        {
            StopRendering();
            return;
        }

        AdvanceSpectrum(1.0 / 60.0);
        RenderMeter();

        if (_targetPeak > StopThreshold || _displayPeak > StopThreshold || _peakPulse > StopThreshold || _hasDynamicBarEnergy)
        {
            EnsureRendering();
        }
    }

    private void CardBorder_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (IsLowPerformanceMode)
        {
            ResetCardHoverVisuals();
            return;
        }

        AnimateCardHoverState(-1.0, 0.08, 0.4, 160);
    }

    private void CardBorder_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ResetCardHoverVisuals(200);
    }

    private void ResetCardHoverVisuals(int durationMilliseconds = 0)
    {
        AnimateCardHoverState(0.0, 0.0, 0.0, durationMilliseconds);
    }

    private void AnimateCardHoverState(double translateY, double sheenOpacity, double strokeOpacity, int durationMilliseconds)
    {
        AnimateDouble(CardTranslateTransform, TranslateTransform.YProperty, translateY, durationMilliseconds);
        AnimateDouble(CardSheen, UIElement.OpacityProperty, sheenOpacity, durationMilliseconds);
        AnimateDouble(CardHoverStroke, UIElement.OpacityProperty, strokeOpacity, durationMilliseconds);
    }

    private static void AnimateDouble(DependencyObject target, DependencyProperty property, double value, int durationMilliseconds)
    {
        if (durationMilliseconds <= 0)
        {
            if (target is IAnimatable animatable)
            {
                animatable.BeginAnimation(property, null);
            }

            target.SetValue(property, value);
            return;
        }

        if (target is not IAnimatable animationTarget)
        {
            target.SetValue(property, value);
            return;
        }

        animationTarget.BeginAnimation(
            property,
            new DoubleAnimation
            {
                To = value,
                Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
            });
    }

    private void VolumeSlider_OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled)
        {
            return;
        }

        e.Handled = true;
        ForwardMouseWheelToScrollHost(slider, e);
    }

    private static void ForwardMouseWheelToScrollHost(DependencyObject source, System.Windows.Input.MouseWheelEventArgs e)
    {
        var scrollHost = FindAncestor<ScrollViewer>(source);
        if (scrollHost is null)
        {
            return;
        }

        var forwardedEvent = new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = source,
        };

        scrollHost.RaiseEvent(forwardedEvent);
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (DependencyObject? current = GetParent(source); current is not null; current = GetParent(current))
        {
            if (current is T typedCurrent)
            {
                return typedCurrent;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is FrameworkContentElement contentElement)
        {
            return contentElement.Parent ?? contentElement.TemplatedParent;
        }

        return VisualTreeHelper.GetParent(child);
    }
}
