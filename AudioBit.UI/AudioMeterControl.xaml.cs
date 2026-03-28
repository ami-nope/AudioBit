using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AudioBit.UI;

public partial class AudioMeterControl : UserControl
{
    private const double PeakAttackPerSecond = 13.0;
    private const double PeakReleasePerSecond = 5.1;
    private const double PulseReleasePerSecond = 2.4;
    private const double BarAttackPerSecond = 24.0;
    private const double BarReleasePerSecond = 10.5;
    private const double DynamicBarThreshold = 0.0018;
    private const double StopThreshold = 0.0015;
    private const double InputNoiseFloor = 0.003;
    private const double InputSensitivityExponent = 0.63;
    private const double InputSensitivityGain = 1.14;
    private const double InputSensitivityBlend = 0.22;
    private const double PeakPulseRiseThreshold = 0.009;

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
        var initialPeak = ShapePeakSignal(Math.Clamp(PeakValue, 0.0, 1.0));
        _targetPeak = initialPeak;
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
        var energizedPeak = ShapePeakSignal(clampedPeak);
        var rise = Math.Max(0.0, energizedPeak - _lastSamplePeak);
        if (rise > PeakPulseRiseThreshold)
        {
            var transient = Math.Clamp((rise * 4.4) + (energizedPeak * 0.58), 0.0, 1.0);
            _peakPulse = Math.Max(_peakPulse, transient);
        }

        _lastSamplePeak = energizedPeak;
        _targetPeak = energizedPeak;

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
            var envelopeWeight = 0.54 + (_barBias[index] * 0.40) + (centerWeight * 0.24);
            var transientWeight = 0.18 + (_barBias[index] * 0.16) + (centerWeight * 0.34);
            var envelope = _displayPeak * envelopeWeight;
            var transient = _peakPulse * transientWeight;
            var energy = Math.Clamp((envelope * 1.05) + (transient * 1.12), 0.0, 1.0);
            var targetScale = baseFloor + (envelope * 1.08) + (transient * 1.12) + ComputeOrganicMotion(index, position, centerWeight, energy);

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
            (Math.Max(0.0, wave) * 0.54)
            + ((flutter + 1.0) * 0.26)
            + ((jitter + 1.0) * 0.13);
        var amplitude = (0.020 + (centerWeight * 0.030) + (_barBias[index] * 0.017))
            * (0.34 + (energy * 1.92));

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
            var baseFloor = 0.055 + (_barBias[index] * 0.022);
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

    private static double ShapePeakSignal(double peak)
    {
        if (peak <= InputNoiseFloor)
        {
            return 0.0;
        }

        var normalizedPeak = Math.Clamp((peak - InputNoiseFloor) / (1.0 - InputNoiseFloor), 0.0, 1.0);
        var liftedPeak = Math.Pow(normalizedPeak, InputSensitivityExponent);
        return Math.Clamp((liftedPeak * InputSensitivityGain) + (normalizedPeak * InputSensitivityBlend), 0.0, 1.0);
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
