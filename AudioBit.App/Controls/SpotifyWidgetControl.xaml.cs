using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AudioBit.App.ViewModels;

namespace AudioBit.App.Controls;

public partial class SpotifyWidgetControl : UserControl
{
    private static readonly Duration AnimationDuration = new(TimeSpan.FromMilliseconds(300));
    private static readonly TimeSpan CollapseDelay = TimeSpan.FromMilliseconds(150);
    private const double CollapsedPanelWidth = 48;
    private const double ExpandedPanelWidth = 280;
    private const double CollapsedPanelShift = 30;

    private static readonly TimeSpan VisualizerFrameInterval = TimeSpan.FromMilliseconds(16);
    private const double PeakAttackPerSecond = 11.4;
    private const double PeakReleasePerSecond = 4.8;
    private const double PulseReleasePerSecond = 2.3;
    private const double AmbientAttackPerSecond = 1.6;
    private const double AmbientReleasePerSecond = 0.9;
    private const double BarAttackPerSecond = 19.5;
    private const double BarReleasePerSecond = 9.4;
    private const double DynamicBarThreshold = 0.0015;
    private const double StopThreshold = 0.0012;
    private const double InputNoiseFloor = 0.006;
    private const double InputSensitivityExponent = 0.74;
    private const double InputSensitivityGain = 0.98;
    private const double InputSensitivityBlend = 0.16;
    private const double PeakPulseRiseThreshold = 0.018;
    private const double RemotePlaybackPeakThreshold = 0.08;
    private const double RemotePlaybackAmbientFloor = 0.24;
    private const double RemotePlaybackVisibleFloor = 0.10;
    private const double RemotePlaybackSwing = 0.18;
    private const double VisualizerBaselineConstant = 0.02;
    private const double VisualizerAmplitudeMultiplier = 2.0;

    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _visualizerTimer;
    private readonly ScaleTransform[] _visualizerBars;
    private readonly double[] _barBias = [0.12, 0.24, 0.40, 0.63, 0.92, 0.58, 0.83, 0.47, 0.71];
    private readonly double[] _barFloors;
    private readonly double[] _barScales;
    private bool _isStickyOpen;
    private bool _isAnimating;
    private bool _hasDynamicBarEnergy;
    private INotifyPropertyChanged? _currentDataContextNotifier;
    private DateTimeOffset _lastVisualizerTickUtc = DateTimeOffset.MinValue;
    private double _spectrumTime;
    private double _targetPeak;
    private double _displayPeak;
    private double _peakPulse;
    private double _ambientEnergy;
    private double _lastLivePeak;

    public SpotifyWidgetControl()
    {
        InitializeComponent();

        _visualizerBars =
        [
            Bar1Scale,
            Bar2Scale,
            Bar3Scale,
            Bar4Scale,
            Bar5Scale,
            Bar6Scale,
            Bar7Scale,
            Bar8Scale,
            Bar9Scale,
        ];
        _barFloors = new double[_visualizerBars.Length];
        _barScales = new double[_visualizerBars.Length];
        ResetVisualizerBars();
        _collapseTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = CollapseDelay,
        };
        _collapseTimer.Tick += CollapseTimerOnTick;
        _visualizerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = VisualizerFrameInterval,
        };
        _visualizerTimer.Tick += VisualizerTimerOnTick;
        Loaded += SpotifyWidgetControl_OnLoaded;
        DataContextChanged += SpotifyWidgetControl_OnDataContextChanged;
        Unloaded += SpotifyWidgetControl_OnUnloaded;
    }

    public bool IsExpanded => WidgetPopup.IsOpen;

    public void Collapse()
    {
        _isStickyOpen = false;
        _collapseTimer.Stop();
        BeginCollapse();
    }

    public bool ContainsElement(DependencyObject? source)
    {
        return source is not null
            && (IsDescendantOf(source, this)
                || (WidgetPopup.Child is DependencyObject popupChild && IsDescendantOf(source, popupChild)));
    }

    private void TabHitArea_OnMouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        BeginExpand();
        AnimateDouble(TabGlow, OpacityProperty, 0.92);
    }

    private void TabHitArea_OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!ExpandedPanel.IsMouseOver)
        {
            ScheduleCollapse();
        }

        if (!_isStickyOpen)
        {
            AnimateDouble(TabGlow, OpacityProperty, 0.0);
        }
    }

    private bool _wasJustClosed;

    private void TabHitArea_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && HasAncestor<ButtonBase>(source))
        {
            return;
        }

        if (_wasJustClosed)
        {
            _wasJustClosed = false;
            e.Handled = true;
            return;
        }

        _isStickyOpen = !IsExpanded || !_isStickyOpen;
        _collapseTimer.Stop();

        if (_isStickyOpen)
        {
            if (!WidgetPopup.IsOpen)
            {
                Dispatcher.BeginInvoke(new Action(() => BeginExpand()), System.Windows.Threading.DispatcherPriority.Input);
            }
            AnimateDouble(TabGlow, OpacityProperty, 0.92);
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() => BeginCollapse()), System.Windows.Threading.DispatcherPriority.Input);
        }

        e.Handled = true;
    }

    private void ExpandedPanel_OnMouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
    }

    private void ExpandedPanel_OnMouseLeave(object sender, MouseEventArgs e)
    {
        ScheduleCollapse();
    }

    private void ExpandedPanel_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isStickyOpen = true;
        _collapseTimer.Stop();
        AnimateDouble(TabGlow, OpacityProperty, 0.92);
    }

    private void CollapseTimerOnTick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (_isStickyOpen || TabHitArea.IsMouseOver || ExpandedPanel.IsMouseOver)
        {
            return;
        }

        BeginCollapse();
    }

    private void BeginExpand()
    {
        if (WidgetPopup.IsOpen)
        {
            return;
        }

        WidgetPopup.IsOpen = true;
        _isAnimating = true;

        ExpandedPanel.Width = ExpandedPanelWidth;
        ExpandedPanel.Opacity = 0;
        ExpandedPanelShift.X = -ExpandedPanelWidth;

        var storyboard = new Storyboard
        {
            FillBehavior = FillBehavior.Stop,
        };

        storyboard.Children.Add(CreateAnimation(0, 1, ExpandedPanel, OpacityProperty));
        storyboard.Children.Add(CreateAnimation(-ExpandedPanelWidth, 0, ExpandedPanelShift, TranslateTransform.XProperty));
        storyboard.Completed += (_, _) =>
        {
            ExpandedPanel.Opacity = 1;
            ExpandedPanelShift.X = 0;
            _isAnimating = false;
        };
        storyboard.Begin();
    }

    private void BeginCollapse()
    {
        if (!WidgetPopup.IsOpen || _isAnimating)
        {
            return;
        }

        _isAnimating = true;
        var storyboard = new Storyboard
        {
            FillBehavior = FillBehavior.Stop,
        };

        storyboard.Children.Add(CreateAnimation(ExpandedPanel.Opacity, 0, ExpandedPanel, OpacityProperty));
        storyboard.Children.Add(CreateAnimation(ExpandedPanelShift.X, -ExpandedPanelWidth, ExpandedPanelShift, TranslateTransform.XProperty));
        storyboard.Completed += (_, _) =>
        {
            ExpandedPanel.Opacity = 0;
            ExpandedPanelShift.X = -ExpandedPanelWidth;
            WidgetPopup.IsOpen = false;
            _isAnimating = false;
            if (!_isStickyOpen)
            {
                TabGlow.Opacity = 0;
            }
        };
        storyboard.Begin();
    }

    private void WidgetPopup_OnClosed(object? sender, EventArgs e)
    {
        _wasJustClosed = true;
        Dispatcher.BeginInvoke(new Action(() => _wasJustClosed = false), System.Windows.Threading.DispatcherPriority.Input);

        if (_isStickyOpen)
        {
            _isStickyOpen = false;
            TabGlow.Opacity = 0;
        }
        
        _isAnimating = false;
        ExpandedPanel.Opacity = 0;
        ExpandedPanelShift.X = -ExpandedPanelWidth;
    }

    private void ScheduleCollapse()
    {
        if (_isStickyOpen)
        {
            return;
        }

        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void SpotifyWidgetControl_OnUnloaded(object sender, RoutedEventArgs e)
    {
        _collapseTimer.Stop();
        _visualizerTimer.Stop();
        DetachCurrentDataContext();
    }

    private void SpotifyWidgetControl_OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachCurrentDataContext();
        UpdatePeakSignal(GetSpotifyLivePeak());
        RefreshVisualizerState(forceReset: true);
    }

    private void SpotifyWidgetControl_OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachCurrentDataContext();
        AttachCurrentDataContext();
        UpdatePeakSignal(GetSpotifyLivePeak());
        RefreshVisualizerState(forceReset: true);
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is FrameworkContentElement contentElement)
        {
            return contentElement.Parent ?? contentElement.TemplatedParent;
        }

        return VisualTreeHelper.GetParent(child);
    }

    private static DoubleAnimation CreateAnimation(double from, double to, DependencyObject target, DependencyProperty property)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = AnimationDuration,
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        return animation;
    }

    private static void AnimateDouble(DependencyObject target, DependencyProperty property, double value)
    {
        if (target is not IAnimatable animatable)
        {
            target.SetValue(property, value);
            return;
        }

        animatable.BeginAnimation(
            property,
            new DoubleAnimation
            {
                To = value,
                Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private static bool HasAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (DependencyObject? current = source; current is not null; current = GetParent(current))
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }

    private void AttachCurrentDataContext()
    {
        if (DataContext is not INotifyPropertyChanged notifier)
        {
            return;
        }

        if (ReferenceEquals(_currentDataContextNotifier, notifier))
        {
            return;
        }

        _currentDataContextNotifier = notifier;
        notifier.PropertyChanged += DataContextNotifierOnPropertyChanged;
    }

    private void DetachCurrentDataContext()
    {
        if (_currentDataContextNotifier is null)
        {
            return;
        }

        _currentDataContextNotifier.PropertyChanged -= DataContextNotifierOnPropertyChanged;
        _currentDataContextNotifier = null;
    }

    private void DataContextNotifierOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpotifyViewModel.IsPlaying)
            || e.PropertyName == nameof(SpotifyViewModel.HasTrack)
            || e.PropertyName == nameof(SpotifyViewModel.HasLocalAudioActivity)
            || e.PropertyName == nameof(SpotifyViewModel.LivePeak))
        {
            UpdatePeakSignal(ShouldAnimateSpotifyVisualizer() ? GetSpotifyLivePeak() : 0.0);
            RefreshVisualizerState(forceReset: e.PropertyName == nameof(SpotifyViewModel.IsPlaying));
        }
    }

    private void VisualizerTimerOnTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var frameSeconds = _lastVisualizerTickUtc == DateTimeOffset.MinValue
            ? 1.0 / 60.0
            : Math.Clamp((now - _lastVisualizerTickUtc).TotalSeconds, 1.0 / 240.0, 1.0 / 20.0);

        _lastVisualizerTickUtc = now;

        AdvanceEnvelope(frameSeconds);
        AdvanceSpectrum(frameSeconds);
        RenderBars();

        if (!ShouldAnimateSpotifyVisualizer()
            && _targetPeak <= StopThreshold
            && _displayPeak <= StopThreshold
            && _peakPulse <= StopThreshold
            && _ambientEnergy <= StopThreshold
            && !_hasDynamicBarEnergy)
        {
            _visualizerTimer.Stop();
            _lastVisualizerTickUtc = DateTimeOffset.MinValue;
        }
    }

    private void RefreshVisualizerState(bool forceReset)
    {
        if (forceReset)
        {
            _lastVisualizerTickUtc = DateTimeOffset.MinValue;
        }

        if (ShouldAnimateSpotifyVisualizer()
            || _targetPeak > StopThreshold
            || _displayPeak > StopThreshold
            || _peakPulse > StopThreshold
            || _ambientEnergy > StopThreshold
            || _hasDynamicBarEnergy)
        {
            if (!_visualizerTimer.IsEnabled)
            {
                _visualizerTimer.Start();
            }

            return;
        }

        _visualizerTimer.Stop();
        _lastVisualizerTickUtc = DateTimeOffset.MinValue;
        ResetVisualizerBars();
        RenderBars();
    }

    private void UpdatePeakSignal(double peak)
    {
        var clampedPeak = Math.Clamp(peak, 0.0, 1.0);
        var energizedPeak = ShapePeakSignal(clampedPeak);
        var softenedPeak = (energizedPeak * 0.68) + (_lastLivePeak * 0.32);
        var rise = Math.Max(0.0, softenedPeak - _lastLivePeak);
        if (rise > PeakPulseRiseThreshold)
        {
            var transient = Math.Clamp((rise * 3.0) + (softenedPeak * 0.34), 0.0, 0.72);
            _peakPulse = Math.Max(_peakPulse, transient);
        }

        _lastLivePeak = softenedPeak;
        _targetPeak = softenedPeak;
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

        var remoteFallbackBlend = ShouldUseRemotePlaybackFallback() ? GetRemotePlaybackBlend(_targetPeak) : 0.0;
        var ambientTarget = ShouldAnimateSpotifyVisualizer()
            ? Math.Max(0.10, (_targetPeak * 0.34) + (_peakPulse * 0.12))
            : 0.0;
        if (remoteFallbackBlend > StopThreshold)
        {
            ambientTarget = Math.Max(ambientTarget, Lerp(0.10, RemotePlaybackAmbientFloor, remoteFallbackBlend));
        }

        var ambientRate = _ambientEnergy < ambientTarget
            ? AmbientAttackPerSecond
            : AmbientReleasePerSecond;
        _ambientEnergy = MoveTowards(_ambientEnergy, ambientTarget, ambientRate * frameSeconds);
    }

    private void AdvanceSpectrum(double frameSeconds)
    {
        _spectrumTime += frameSeconds;
        _hasDynamicBarEnergy = false;
        var shouldAnimate = ShouldAnimateSpotifyVisualizer();
        var useRemoteFallback = ShouldUseRemotePlaybackFallback();
        var progressDrift = GetTrackProgress();
        var measuredEnergy = Math.Clamp((_displayPeak * 0.82) + (_peakPulse * 0.46), 0.0, 1.0);
        var remoteFallbackBlend = useRemoteFallback ? GetRemotePlaybackBlend(measuredEnergy) : 0.0;

        for (var index = 0; index < _visualizerBars.Length; index++)
        {
            var position = index / Math.Max(1.0, _visualizerBars.Length - 1.0);
            var centerWeight = 1.0 - (Math.Abs(position - 0.5) * 2.0);
            var baseFloor = _barFloors[index];
            var envelopeWeight = 0.55 + (_barBias[index] * 0.38) + (centerWeight * 0.22);
            var transientWeight = 0.20 + (_barBias[index] * 0.18) + (centerWeight * 0.30);
            var groove = shouldAnimate
                ? ComputePlaybackGroove(index, position, centerWeight, progressDrift, measuredEnergy, remoteFallbackBlend)
                : 0.0;
            var envelope = (_displayPeak * envelopeWeight * 0.68) + (groove * (0.34 + (_barBias[index] * 0.14)));
            var transient = (_peakPulse * transientWeight * 0.56) + (groove * (measuredEnergy < 0.14 ? 0.15 : 0.07));
            var energy = Math.Clamp((envelope * 0.88) + (transient * 0.70) + (_ambientEnergy * 0.74), 0.0, 1.0);
            var remoteLift = remoteFallbackBlend
                * (RemotePlaybackVisibleFloor + (centerWeight * 0.040) + (_barBias[index] * 0.028));
            var remoteMotion = useRemoteFallback
                ? ComputeRemotePlaybackMotion(index, position, centerWeight, progressDrift, remoteFallbackBlend)
                : 0.0;
            var animatedScale = remoteLift
                + remoteMotion
                + (envelope * 0.82)
                + (transient * 0.58)
                + (_ambientEnergy * (0.042 + (_barBias[index] * 0.020)))
                + ComputeOrganicMotion(index, position, centerWeight, energy, progressDrift, remoteFallbackBlend);
            var targetScale = baseFloor + (animatedScale * VisualizerAmplitudeMultiplier);

            if (_displayPeak <= StopThreshold
                && _peakPulse <= StopThreshold
                && _ambientEnergy <= StopThreshold
                && !shouldAnimate)
            {
                targetScale = baseFloor;
            }

            targetScale = Math.Clamp(targetScale, baseFloor, 1.0);
            var response = _barScales[index] < targetScale
                ? BarAttackPerSecond + (_barBias[index] * 5.5) + (centerWeight * 3.2)
                : BarReleasePerSecond + (_barBias[index] * 3.8);

            _barScales[index] = DampedLerp(_barScales[index], targetScale, response, frameSeconds);
            if (_barScales[index] > baseFloor + DynamicBarThreshold)
            {
                _hasDynamicBarEnergy = true;
            }
        }
    }

    private double ComputePlaybackGroove(
        int index,
        double position,
        double centerWeight,
        double progressDrift,
        double measuredEnergy,
        double remoteFallbackBlend)
    {
        if (_ambientEnergy <= StopThreshold)
        {
            return 0.0;
        }

        var phase = (_barBias[index] * 3.1)
            + (index * 0.71)
            + (progressDrift * Math.PI * (1.5 + centerWeight));
        var sway = (Math.Sin((_spectrumTime * (1.55 + (_barBias[index] * 0.90))) + phase) + 1.0) * 0.5;
        var skip = (Math.Sin((_spectrumTime * (2.45 + (centerWeight * 0.75))) - (position * Math.PI * 2.6) + (phase * 1.2)) + 1.0) * 0.5;
        var chatter = (Math.Sin((_spectrumTime * (4.6 + (index * 0.11))) + (phase * 1.8)) + 1.0) * 0.5;
        var selector = (Math.Sin((_spectrumTime * (0.42 + (_barBias[index] * 0.22))) + (progressDrift * Math.PI * 2.0) + phase) + 1.0) * 0.5;
        var blend = Lerp(sway, skip, selector) * 0.70 + (chatter * 0.30);
        var amplitude = (0.015 + (_barBias[index] * 0.010) + (centerWeight * 0.012))
            * (0.65 + (_ambientEnergy * 1.20) + (measuredEnergy * 0.35) + (remoteFallbackBlend * 1.15));

        return blend * amplitude;
    }

    private double ComputeRemotePlaybackMotion(
        int index,
        double position,
        double centerWeight,
        double progressDrift,
        double remoteFallbackBlend)
    {
        if (remoteFallbackBlend <= StopThreshold)
        {
            return 0.0;
        }

        var phase = (_barBias[index] * 4.1)
            + (index * 0.88)
            + (progressDrift * Math.PI * (1.7 + position));
        var drift = (Math.Sin((_spectrumTime * (0.95 + (centerWeight * 0.45))) + phase) + 1.0) * 0.5;
        var hop = (Math.Sin((_spectrumTime * (2.10 + (_barBias[index] * 1.10))) - (position * Math.PI * 2.3) + (phase * 1.3)) + 1.0) * 0.5;
        var flutter = (Math.Sin((_spectrumTime * (3.50 + (index * 0.13))) + (phase * 2.0)) + 1.0) * 0.5;
        var selector = (Math.Sin((_spectrumTime * (0.37 + (centerWeight * 0.18))) + (phase * 0.7)) + 1.0) * 0.5;
        var profile = (Lerp(drift, hop, selector) * 0.68) + (flutter * 0.32);
        var amplitude = (RemotePlaybackSwing + (centerWeight * 0.08) + (_barBias[index] * 0.06))
            * remoteFallbackBlend;

        return profile * amplitude;
    }

    private double ComputeOrganicMotion(
        int index,
        double position,
        double centerWeight,
        double energy,
        double progressDrift,
        double remoteFallbackBlend)
    {
        if (energy <= StopThreshold)
        {
            return 0.0;
        }

        var phase = (_barBias[index] * 2.4)
            + (index * 0.46)
            + (progressDrift * Math.PI * (0.8 + position));
        var wave = Math.Sin((_spectrumTime * (3.8 + (centerWeight * 0.9))) - (position * Math.PI * 3.1) + phase);
        var flutter = Math.Sin((_spectrumTime * (7.1 + (_barBias[index] * 1.2))) + (phase * 1.5));
        var jitter = Math.Sin((_spectrumTime * (10.1 + (index * 0.09))) + (phase * 2.1));
        var blend =
            (Math.Max(0.0, wave) * 0.45)
            + ((flutter + 1.0) * 0.19)
            + ((jitter + 1.0) * 0.10);
        var amplitude = (0.010 + (centerWeight * 0.015) + (_barBias[index] * 0.009))
            * (0.20 + (energy * 1.10) + (remoteFallbackBlend * 0.65));

        return blend * amplitude;
    }

    private void RenderBars()
    {
        for (var index = 0; index < _visualizerBars.Length; index++)
        {
            _visualizerBars[index].ScaleY = _barScales[index];
        }
    }

    private void ResetVisualizerBars()
    {
        _spectrumTime = 0.0;
        _targetPeak = 0.0;
        _displayPeak = 0.0;
        _peakPulse = 0.0;
        _ambientEnergy = 0.0;
        _lastLivePeak = 0.0;
        _hasDynamicBarEnergy = false;

        for (var index = 0; index < _visualizerBars.Length; index++)
        {
            var floor = 0.06 + VisualizerBaselineConstant + (_barBias[index] * 0.024);
            _barFloors[index] = floor;
            _barScales[index] = floor;
        }
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

    private static double Lerp(double from, double to, double amount)
    {
        return from + ((to - from) * Math.Clamp(amount, 0.0, 1.0));
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

    private static double GetRemotePlaybackBlend(double energy)
    {
        return Math.Clamp((RemotePlaybackPeakThreshold - energy) / RemotePlaybackPeakThreshold, 0.0, 1.0);
    }

    private double GetSpotifyLivePeak()
    {
        return DataContext is SpotifyViewModel viewModel ? viewModel.LivePeak : 0.0;
    }

    private double GetTrackProgress()
    {
        return DataContext is SpotifyViewModel viewModel
            ? Math.Clamp(viewModel.ProgressPercent / 100.0, 0.0, 1.0)
            : 0.0;
    }

    private bool IsSpotifyPlaying()
    {
        return DataContext is SpotifyViewModel viewModel && viewModel.IsPlaying;
    }

    private bool ShouldUseRemotePlaybackFallback()
    {
        return DataContext is SpotifyViewModel viewModel
            && viewModel.IsPlaying
            && !viewModel.HasLocalAudioActivity;
    }

    private bool ShouldAnimateSpotifyVisualizer()
    {
        return DataContext is SpotifyViewModel viewModel
            && (viewModel.IsPlaying || viewModel.HasTrack);
    }
}
