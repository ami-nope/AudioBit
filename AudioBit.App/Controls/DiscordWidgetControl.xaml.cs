using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AudioBit.App.Controls;

public partial class DiscordWidgetControl : UserControl
{
    private const double TabCornerRadius = 18;
    private static readonly TimeSpan IconAnimationDuration = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan SlashAnimationDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan VisualizerFrameInterval = TimeSpan.FromMilliseconds(16);
    private const double VisualizerRadiusScale = 1.32;
    private const int MeshPointCount = 56;
    private const double VoiceNoiseFloor = 0.0045;
    private const double VoiceSensitivityExponent = 0.68;
    private const double VoiceSensitivityGain = 1.08;
    private const double VoiceSensitivityBlend = 0.22;
    private const double PeakAttackPerSecond = 17.2;
    private const double PeakReleasePerSecond = 6.8;
    private const double PulseReleasePerSecond = 3.6;
    private const double AmbientAttackPerSecond = 3.2;
    private const double AmbientReleasePerSecond = 1.45;
    private const double PulseRiseThreshold = 0.01;
    private const double StopThreshold = 0.0012;
    private const double PaletteBlendPerSecond = 4.8;
    private const double PaletteCooldownSeconds = 0.18;

    private static readonly VoicePalette[] VoicePalettes =
    [
        new(
            Color.FromRgb(0xEC, 0xF6, 0xFF),
            Color.FromRgb(0xA7, 0xC8, 0xFF),
            Color.FromRgb(0xFF, 0xFB, 0xDE),
            Color.FromRgb(0xC3, 0xEB, 0xFF),
            Color.FromRgb(0x96, 0xAA, 0xF2),
            Color.FromRgb(0xFF, 0xFF, 0xF2),
            Color.FromRgb(0xD6, 0xF7, 0xFF),
            Color.FromRgb(0x92, 0xA8, 0xFF),
            Color.FromRgb(0xFF, 0xB7, 0x72)),
        new(
            Color.FromRgb(0xEE, 0xFF, 0xF4),
            Color.FromRgb(0x99, 0xE7, 0xBE),
            Color.FromRgb(0xFB, 0xFF, 0xD6),
            Color.FromRgb(0xBD, 0xFF, 0xE8),
            Color.FromRgb(0x84, 0xC8, 0xA6),
            Color.FromRgb(0xFF, 0xFF, 0xEA),
            Color.FromRgb(0xD4, 0xFF, 0xEE),
            Color.FromRgb(0x8F, 0xDE, 0xBE),
            Color.FromRgb(0xFF, 0xB4, 0x62)),
        new(
            Color.FromRgb(0xFF, 0xF6, 0xDE),
            Color.FromRgb(0xFF, 0xD5, 0x72),
            Color.FromRgb(0xFF, 0xFF, 0xF2),
            Color.FromRgb(0xFF, 0xEE, 0xB4),
            Color.FromRgb(0xFF, 0xC8, 0x71),
            Color.FromRgb(0xFF, 0xFF, 0xE8),
            Color.FromRgb(0xFF, 0xE8, 0x92),
            Color.FromRgb(0xFF, 0xC9, 0x6B),
            Color.FromRgb(0xFF, 0x95, 0x54)),
        new(
            Color.FromRgb(0xFF, 0xF0, 0xF7),
            Color.FromRgb(0xFF, 0xB7, 0xD0),
            Color.FromRgb(0xFF, 0xFD, 0xEC),
            Color.FromRgb(0xFF, 0xD7, 0xE6),
            Color.FromRgb(0xF6, 0x9F, 0xC0),
            Color.FromRgb(0xFF, 0xFC, 0xF3),
            Color.FromRgb(0xFF, 0xDD, 0xEB),
            Color.FromRgb(0xFF, 0xAF, 0xCA),
            Color.FromRgb(0xFF, 0x89, 0x81)),
        new(
            Color.FromRgb(0xF2, 0xEE, 0xFF),
            Color.FromRgb(0xB4, 0xAA, 0xFF),
            Color.FromRgb(0xFF, 0xFF, 0xEE),
            Color.FromRgb(0xD8, 0xCB, 0xFF),
            Color.FromRgb(0xA9, 0x9C, 0xF8),
            Color.FromRgb(0xFF, 0xFD, 0xF5),
            Color.FromRgb(0xE1, 0xD8, 0xFF),
            Color.FromRgb(0xAE, 0xA0, 0xFF),
            Color.FromRgb(0xFF, 0xA0, 0x71)),
        new(
            Color.FromRgb(0xFF, 0xF3, 0xE7),
            Color.FromRgb(0xFF, 0xC0, 0x8F),
            Color.FromRgb(0xFF, 0xFF, 0xE8),
            Color.FromRgb(0xFF, 0xDA, 0xB5),
            Color.FromRgb(0xF4, 0xA5, 0x75),
            Color.FromRgb(0xFF, 0xFB, 0xEF),
            Color.FromRgb(0xFF, 0xDE, 0xB4),
            Color.FromRgb(0xFF, 0xB7, 0x7F),
            Color.FromRgb(0xFF, 0x7D, 0x5F)),
        new(
            Color.FromRgb(0xEE, 0xFF, 0xFB),
            Color.FromRgb(0x8F, 0xEF, 0xD0),
            Color.FromRgb(0xFF, 0xFF, 0xE6),
            Color.FromRgb(0xC5, 0xFF, 0xEE),
            Color.FromRgb(0x7E, 0xD6, 0xBE),
            Color.FromRgb(0xFF, 0xFF, 0xF0),
            Color.FromRgb(0xD5, 0xFF, 0xF4),
            Color.FromRgb(0x88, 0xE2, 0xCA),
            Color.FromRgb(0xFF, 0xAD, 0x5E)),
    ];

    private readonly DispatcherTimer _visualizerTimer;
    private readonly Random _paletteRandom = new(Environment.TickCount & int.MaxValue);
    private readonly LinearGradientBrush _voiceGlowBrush;
    private readonly LinearGradientBrush _voiceBackBrush;
    private readonly LinearGradientBrush _voiceFrontBrush;
    private DateTimeOffset _lastVisualizerTickUtc = DateTimeOffset.MinValue;
    private double _meshTime;
    private double _targetPeak;
    private double _displayPeak;
    private double _peakPulse;
    private double _ambientEnergy;
    private double _lastLivePeak;
    private double _paletteBlend = 1.0;
    private double _paletteCooldownRemaining;
    private VoicePalette _currentPalette = VoicePalettes[0];
    private VoicePalette _targetPalette = VoicePalettes[0];

    public static readonly DependencyProperty IsConnectedProperty = DependencyProperty.Register(
        nameof(IsConnected),
        typeof(bool),
        typeof(DiscordWidgetControl),
        new PropertyMetadata(false, OnVoiceVisualizerStateChanged));

    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
        nameof(IsMuted),
        typeof(bool),
        typeof(DiscordWidgetControl),
        new PropertyMetadata(false, OnIsMutedChanged));

    public static readonly DependencyProperty IsDeafenedProperty = DependencyProperty.Register(
        nameof(IsDeafened),
        typeof(bool),
        typeof(DiscordWidgetControl),
        new PropertyMetadata(false, OnIsDeafenedChanged));

    public static readonly DependencyProperty LivePeakProperty = DependencyProperty.Register(
        nameof(LivePeak),
        typeof(double),
        typeof(DiscordWidgetControl),
        new PropertyMetadata(0.0, OnLivePeakChanged));

    public static readonly DependencyProperty HasVoiceActivityProperty = DependencyProperty.Register(
        nameof(HasVoiceActivity),
        typeof(bool),
        typeof(DiscordWidgetControl),
        new PropertyMetadata(false, OnVoiceVisualizerStateChanged));

    public static readonly DependencyProperty ToggleMuteCommandProperty = DependencyProperty.Register(
        nameof(ToggleMuteCommand), typeof(ICommand), typeof(DiscordWidgetControl), new PropertyMetadata(null));

    public static readonly DependencyProperty ToggleDeafenCommandProperty = DependencyProperty.Register(
        nameof(ToggleDeafenCommand), typeof(ICommand), typeof(DiscordWidgetControl), new PropertyMetadata(null));

    public DiscordWidgetControl()
    {
        InitializeComponent();

        _voiceGlowBrush = CreateGradientBrush(new Point(0.1, 0.02), new Point(0.96, 0.98), _currentPalette.GlowStart, _currentPalette.GlowEnd);
        _voiceBackBrush = CreateGradientBrush(new Point(0.94, 0.08), new Point(0.08, 0.92), _currentPalette.BackStart, _currentPalette.BackMid, _currentPalette.BackEnd);
        _voiceFrontBrush = CreateGradientBrush(new Point(0.0, 0.44), new Point(1.0, 0.56), _currentPalette.FrontStart, _currentPalette.FrontMid, _currentPalette.FrontEnd);
        VoiceMeshGlowPath.Stroke = _voiceGlowBrush;
        VoiceMeshBackPath.Stroke = _voiceBackBrush;
        VoiceMeshFrontPath.Stroke = _voiceFrontBrush;

        _visualizerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = VisualizerFrameInterval,
        };
        _visualizerTimer.Tick += VisualizerTimerOnTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    public bool IsConnected
    {
        get => (bool)GetValue(IsConnectedProperty);
        set => SetValue(IsConnectedProperty, value);
    }

    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public bool IsDeafened
    {
        get => (bool)GetValue(IsDeafenedProperty);
        set => SetValue(IsDeafenedProperty, value);
    }

    public double LivePeak
    {
        get => (double)GetValue(LivePeakProperty);
        set => SetValue(LivePeakProperty, value);
    }

    public bool HasVoiceActivity
    {
        get => (bool)GetValue(HasVoiceActivityProperty);
        set => SetValue(HasVoiceActivityProperty, value);
    }

    public ICommand? ToggleMuteCommand
    {
        get => (ICommand?)GetValue(ToggleMuteCommandProperty);
        set => SetValue(ToggleMuteCommandProperty, value);
    }

    public ICommand? ToggleDeafenCommand
    {
        get => (ICommand?)GetValue(ToggleDeafenCommandProperty);
        set => SetValue(ToggleDeafenCommandProperty, value);
    }

    private void MuteButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ToggleMuteCommand is not null && ToggleMuteCommand.CanExecute(null))
        {
            ToggleMuteCommand.Execute(null);
        }
    }

    private void DeafenButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ToggleDeafenCommand is not null && ToggleDeafenCommand.CanExecute(null))
        {
            ToggleDeafenCommand.Execute(null);
        }
    }

    private static void OnIsMutedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DiscordWidgetControl control && e.NewValue is bool isMuted)
        {
            control.ApplyMuteState(isMuted, animate: control.IsLoaded);
        }
    }

    private static void OnIsDeafenedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DiscordWidgetControl control && e.NewValue is bool isDeafened)
        {
            control.ApplyDeafenState(isDeafened, animate: control.IsLoaded);
        }
    }

    private static void OnLivePeakChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DiscordWidgetControl control)
        {
            return;
        }

        control.UpdatePeakSignal(e.NewValue is double peak ? peak : 0.0);
        if (control.IsLoaded)
        {
            control.RefreshVoiceVisualizerState(forceReset: false);
        }
    }

    private static void OnVoiceVisualizerStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DiscordWidgetControl control || !control.IsLoaded)
        {
            return;
        }

        if (e.Property == IsConnectedProperty && e.NewValue is false)
        {
            control.UpdatePeakSignal(0.0);
        }

        control.RefreshVoiceVisualizerState(forceReset: e.Property == IsConnectedProperty);
        control.RenderVoiceVisualizer();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyRoundedShellClip(TabSurface, TabCornerRadius);
        ApplyMuteState(IsMuted, animate: false);
        ApplyDeafenState(IsDeafened, animate: false);
        UpdatePeakSignal(LivePeak);
        RenderVoiceVisualizer();
        RefreshVoiceVisualizerState(forceReset: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _visualizerTimer.Stop();
        _lastVisualizerTickUtc = DateTimeOffset.MinValue;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyRoundedShellClip(TabSurface, TabCornerRadius);
        RenderVoiceVisualizer();
    }

    private void ApplyMuteState(bool isMuted, bool animate)
    {
        UpdateSlashState(MuteSlashLine, MuteSlashScaleTransform, isMuted, animate, () => IsMuted);
        if (animate)
        {
            PlayMuteAnimation();
        }
        else
        {
            ResetIconTransforms(MuteIconScaleTransform, MuteIconRotateTransform, MuteIconTranslateTransform);
        }
    }

    private void ApplyDeafenState(bool isDeafened, bool animate)
    {
        UpdateSlashState(DeafenSlashLine, DeafenSlashScaleTransform, isDeafened, animate, () => IsDeafened);
        if (animate)
        {
            PlayDeafenAnimation();
        }
        else
        {
            ResetIconTransforms(DeafenIconScaleTransform, DeafenIconRotateTransform, DeafenIconTranslateTransform);
        }
    }

    private void PlayMuteAnimation()
    {
        AnimateKeyFrames(
            MuteIconTranslateTransform,
            TranslateTransform.XProperty,
            (0, 0),
            (-1.2, 70),
            (1.05, 150),
            (-0.65, 235),
            (0.3, 320),
            (0, 420));
        AnimateKeyFrames(
            MuteIconRotateTransform,
            RotateTransform.AngleProperty,
            (0, 0),
            (-7, 70),
            (5.5, 150),
            (-3.25, 235),
            (1.2, 320),
            (0, 420));
        AnimateKeyFrames(
            MuteIconScaleTransform,
            ScaleTransform.ScaleXProperty,
            (1, 0),
            (0.94, 90),
            (1.04, 225),
            (1, 420));
        AnimateKeyFrames(
            MuteIconScaleTransform,
            ScaleTransform.ScaleYProperty,
            (1, 0),
            (1.02, 90),
            (0.985, 225),
            (1, 420));
    }

    private void PlayDeafenAnimation()
    {
        AnimateKeyFrames(
            DeafenIconScaleTransform,
            ScaleTransform.ScaleXProperty,
            (1, 0),
            (1.18, 95),
            (0.965, 220),
            (1.055, 325),
            (1, 420));
        AnimateKeyFrames(
            DeafenIconScaleTransform,
            ScaleTransform.ScaleYProperty,
            (1, 0),
            (0.95, 95),
            (1.015, 220),
            (1, 420));
        AnimateKeyFrames(
            DeafenIconTranslateTransform,
            TranslateTransform.XProperty,
            (0, 0),
            (-0.45, 85),
            (0.4, 185),
            (-0.22, 290),
            (0, 420));
        AnimateKeyFrames(
            DeafenIconRotateTransform,
            RotateTransform.AngleProperty,
            (0, 0),
            (-4.5, 85),
            (3.2, 185),
            (-1.5, 290),
            (0, 420));
    }

    private void VisualizerTimerOnTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        var frameSeconds = _lastVisualizerTickUtc == DateTimeOffset.MinValue
            ? 1.0 / 60.0
            : Math.Clamp((now - _lastVisualizerTickUtc).TotalSeconds, 1.0 / 240.0, 1.0 / 20.0);

        _lastVisualizerTickUtc = now;

        AdvanceVoiceEnvelope(frameSeconds);
        RenderVoiceVisualizer();

        if (!IsConnected
            && !HasVoiceActivity
            && _targetPeak <= StopThreshold
            && _displayPeak <= StopThreshold
            && _peakPulse <= StopThreshold
            && _ambientEnergy <= StopThreshold)
        {
            _visualizerTimer.Stop();
            _lastVisualizerTickUtc = DateTimeOffset.MinValue;
            ResetVoiceVisualizerState();
            RenderVoiceVisualizer();
        }
    }

    private void UpdatePeakSignal(double peak)
    {
        var clampedPeak = Math.Clamp(peak, 0.0, 1.0);
        var energizedPeak = ShapePeakSignal(clampedPeak);
        var softenedPeak = (energizedPeak * 0.8) + (_lastLivePeak * 0.2);
        var rise = Math.Max(0.0, softenedPeak - _lastLivePeak);
        if (rise > PulseRiseThreshold)
        {
            var transient = Math.Clamp((rise * 2.9) + (softenedPeak * 0.46), 0.0, 0.88);
            _peakPulse = Math.Max(_peakPulse, transient);
            TryShiftVoicePalette(softenedPeak, rise);
        }

        _lastLivePeak = softenedPeak;
        _targetPeak = softenedPeak;
    }

    private void RefreshVoiceVisualizerState(bool forceReset)
    {
        if (forceReset)
        {
            _lastVisualizerTickUtc = DateTimeOffset.MinValue;
        }

        if (ShouldRunVoiceVisualizer())
        {
            if (!_visualizerTimer.IsEnabled)
            {
                _visualizerTimer.Start();
            }

            return;
        }

        _visualizerTimer.Stop();
        _lastVisualizerTickUtc = DateTimeOffset.MinValue;
        ResetVoiceVisualizerState();
        RenderVoiceVisualizer();
    }

    private void AdvanceVoiceEnvelope(double frameSeconds)
    {
        _paletteCooldownRemaining = Math.Max(0.0, _paletteCooldownRemaining - frameSeconds);
        if (_paletteBlend < 1.0)
        {
            _paletteBlend = MoveTowards(_paletteBlend, 1.0, PaletteBlendPerSecond * frameSeconds);
        }

        var floorTarget = IsConnected ? 0.026 : 0.0;
        var effectiveTarget = Math.Max(_targetPeak, floorTarget);
        var peakRate = _displayPeak < effectiveTarget
            ? PeakAttackPerSecond
            : PeakReleasePerSecond;
        _displayPeak = MoveTowards(_displayPeak, effectiveTarget, peakRate * frameSeconds);

        _peakPulse = MoveTowards(_peakPulse, 0.0, PulseReleasePerSecond * frameSeconds);

        var ambientTarget = IsConnected
            ? 0.035 + (_displayPeak * 0.28) + (_peakPulse * 0.16)
            : 0.0;
        if (HasVoiceActivity)
        {
            ambientTarget = Math.Max(ambientTarget, 0.11 + (_displayPeak * 0.14));
        }

        var ambientRate = _ambientEnergy < ambientTarget
            ? AmbientAttackPerSecond
            : AmbientReleasePerSecond;
        _ambientEnergy = MoveTowards(_ambientEnergy, ambientTarget, ambientRate * frameSeconds);
        _meshTime += frameSeconds * (1.35 + (_ambientEnergy * 1.15) + (_displayPeak * 1.55) + (_peakPulse * 2.05));
    }

    private void RenderVoiceVisualizer()
    {
        var width = LogoVisualizerCanvas.ActualWidth > 0 ? LogoVisualizerCanvas.ActualWidth : LogoVisualizerCanvas.Width;
        var height = LogoVisualizerCanvas.ActualHeight > 0 ? LogoVisualizerCanvas.ActualHeight : LogoVisualizerCanvas.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        VoiceMeshGlowPath.Data = CreateVoiceMeshGeometry(width, height, 0.16, 0.9, 0.35);
        VoiceMeshBackPath.Data = CreateVoiceMeshGeometry(width, height, 0.62, 0.72, 0.15);
        VoiceMeshFrontPath.Data = CreateVoiceMeshGeometry(width, height, 1.18, 1.0, 0.0);

        var connectedBlend = IsConnected ? 1.0 : 0.0;
        VoiceMeshGlowPath.Opacity = Math.Clamp(0.1 + (connectedBlend * 0.06) + (_ambientEnergy * 0.34) + (_displayPeak * 0.18) + (_peakPulse * 0.16), 0.0, 0.68);
        VoiceMeshBackPath.Opacity = Math.Clamp(0.2 + (connectedBlend * 0.08) + (_ambientEnergy * 0.38) + (_displayPeak * 0.18), 0.0, 0.82);
        VoiceMeshFrontPath.Opacity = Math.Clamp(0.38 + (connectedBlend * 0.1) + (_ambientEnergy * 0.24) + (_displayPeak * 0.42) + (_peakPulse * 0.24), 0.0, 1.0);
        VoiceMeshGlowPath.StrokeThickness = 1.8 + (_ambientEnergy * 0.7) + (_displayPeak * 0.45);
        VoiceMeshBackPath.StrokeThickness = 1.35 + (_ambientEnergy * 0.42) + (_displayPeak * 0.28);
        VoiceMeshFrontPath.StrokeThickness = 1.75 + (_displayPeak * 0.55) + (_peakPulse * 0.36);
        ApplyVoicePalette();

        var logoScale = 1.0 + (_displayPeak * 0.035) + (_peakPulse * 0.055);
        LogoMarkScaleTransform.ScaleX = logoScale;
        LogoMarkScaleTransform.ScaleY = logoScale;
        LogoMark.Opacity = Math.Clamp(0.9 + (_displayPeak * 0.07) + (_peakPulse * 0.05), 0.0, 1.0);
    }

    private Geometry CreateVoiceMeshGeometry(double width, double height, double phaseOffset, double layerAmplitude, double radialOffset)
    {
        var centerX = width * 0.5;
        var centerY = height * 0.5;
        var halfSize = Math.Min(width, height) * 0.5;
        var baseRadius = halfSize - 5.0 + radialOffset;
        var energy = Math.Clamp((_displayPeak * 0.8) + (_peakPulse * 0.48) + (_ambientEnergy * 0.32), 0.0, 1.0);
        var rippleEnergy = Math.Clamp((_ambientEnergy * 0.58) + (_displayPeak * 0.92) + (_peakPulse * 0.72), 0.0, 1.0);
        var maxExpansion = layerAmplitude * (0.72 + (rippleEnergy * 1.85));
        var maxContraction = layerAmplitude * (0.18 + (rippleEnergy * 0.58));
        var radiusFloor = Math.Max((halfSize - 8.4) * VisualizerRadiusScale, (baseRadius - maxContraction) * VisualizerRadiusScale);
        var radiusCeiling = (halfSize - 2.35) * VisualizerRadiusScale;
        var points = new List<Point>(MeshPointCount);
        var twoPi = Math.PI * 2.0;

        for (var index = 0; index < MeshPointCount; index++)
        {
            var angle = (index / (double)MeshPointCount) * twoPi;
            var primaryWave = Math.Sin((angle * 1.85) - (_meshTime * 1.75) + phaseOffset);
            var secondaryWave = Math.Sin((angle * 3.9) + (_meshTime * 2.45) - (phaseOffset * 0.7));
            var detailWave = Math.Sin((angle * 6.35) - (_meshTime * 3.4) + (phaseOffset * 1.35));
            var pulseWave = PositiveSin((angle * 2.3) + (_meshTime * 1.2) + (phaseOffset * 0.95), 1.35);
            var shape = (primaryWave * 0.58) + (secondaryWave * 0.28) + (detailWave * (0.08 + (energy * 0.08)));
            var outward = Math.Max(0.0, shape);
            var inward = Math.Max(0.0, -shape);
            var expansion = outward * maxExpansion;
            expansion += pulseWave * layerAmplitude * ((_peakPulse * 1.35) + (_displayPeak * 0.42));
            expansion += energy * layerAmplitude * 0.1;
            var contraction = inward * maxContraction;

            var rawRadius = baseRadius + expansion - contraction;
            var radius = Math.Clamp(rawRadius * VisualizerRadiusScale, radiusFloor, radiusCeiling);
            var point = new Point(
                centerX + (Math.Cos(angle) * radius),
                centerY + (Math.Sin(angle) * radius));
            points.Add(point);
        }

        return CreateClosedSplineGeometry(points);
    }

    private void ResetVoiceVisualizerState()
    {
        _meshTime = 0.0;
        _targetPeak = 0.0;
        _displayPeak = 0.0;
        _peakPulse = 0.0;
        _ambientEnergy = 0.0;
        _lastLivePeak = 0.0;
        _paletteBlend = 1.0;
        _paletteCooldownRemaining = 0.0;
        _currentPalette = VoicePalettes[0];
        _targetPalette = VoicePalettes[0];
    }

    private bool ShouldRunVoiceVisualizer()
    {
        return IsConnected
            || HasVoiceActivity
            || _targetPeak > StopThreshold
            || _displayPeak > StopThreshold
            || _peakPulse > StopThreshold
            || _ambientEnergy > StopThreshold;
    }

    private static void UpdateSlashState(
        Shape slash,
        ScaleTransform slashScale,
        bool isActive,
        bool animate,
        Func<bool> stateAccessor)
    {
        slash.BeginAnimation(OpacityProperty, null);
        slashScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        if (!animate)
        {
            slash.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
            slash.Opacity = isActive ? 1 : 0;
            slashScale.ScaleY = isActive ? 1 : 0.22;
            return;
        }

        slash.Visibility = Visibility.Visible;
        if (isActive)
        {
            slash.Opacity = 0;
            slashScale.ScaleY = 0.22;

            slash.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(1, SlashAnimationDuration)
                {
                    EasingFunction = CreateEase(),
                });
            slashScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, SlashAnimationDuration)
                {
                    EasingFunction = CreateEase(),
                });
            return;
        }

        var opacityAnimation = new DoubleAnimation(0, SlashAnimationDuration)
        {
            EasingFunction = CreateEase(),
        };
        opacityAnimation.Completed += (_, _) =>
        {
            if (!stateAccessor())
            {
                slash.Visibility = Visibility.Collapsed;
            }
        };

        slash.BeginAnimation(OpacityProperty, opacityAnimation);
        slashScale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.22, SlashAnimationDuration)
            {
                EasingFunction = CreateEase(),
            });
    }

    private static double PositiveSin(double value, double exponent)
    {
        return Math.Pow(Math.Max(0.0, Math.Sin(value)), exponent);
    }

    private void TryShiftVoicePalette(double energy, double rise)
    {
        if (_paletteCooldownRemaining > 0.0 || VoicePalettes.Length < 2)
        {
            return;
        }

        var aggressiveBeat = energy >= 0.58 || rise >= 0.085 || _peakPulse >= 0.45;
        var shiftChance = aggressiveBeat ? 0.95 : 0.45 + (energy * 0.35);
        if (_paletteRandom.NextDouble() > shiftChance)
        {
            return;
        }

        _currentPalette = GetInterpolatedPalette();
        _targetPalette = PickVoicePalette(aggressiveBeat);
        _paletteBlend = 0.0;
        _paletteCooldownRemaining = PaletteCooldownSeconds;
    }

    private void ApplyVoicePalette()
    {
        var palette = GetInterpolatedPalette();
        var aggression = Math.Clamp((_displayPeak * 0.56) + (_peakPulse * 0.82), 0.0, 1.0);
        var deepCool = LerpColor(palette.BackEnd, Color.FromRgb(0x6C, 0x88, 0xE8), 0.28 + (aggression * 0.12));
        var deepTeal = LerpColor(palette.GlowEnd, Color.FromRgb(0x6D, 0xCF, 0xE8), 0.24 + (aggression * 0.12));
        var deepWarm = LerpColor(palette.HotAccent, Color.FromRgb(0xFF, 0x8D, 0x61), 0.18 + (aggression * 0.22));
        var glowStart = LerpColor(palette.GlowStart, palette.HotAccent, aggression * 0.12);
        var glowArc = DeepenColor(LerpColor(palette.GlowEnd, deepTeal, 0.58), 0.06 + (aggression * 0.04));
        var glowEnd = DeepenColor(LerpColor(palette.GlowEnd, deepCool, 0.42), 0.08 + (aggression * 0.07));
        var backStart = LerpColor(palette.BackStart, palette.HotAccent, aggression * 0.08);
        var backCool = DeepenColor(LerpColor(palette.BackMid, deepTeal, 0.48), 0.04 + (aggression * 0.04));
        var backMid = LerpColor(palette.BackMid, palette.HotAccent, aggression * 0.14);
        var backWarm = LerpColor(palette.BackEnd, deepWarm, 0.22 + (aggression * 0.2));
        var backEnd = DeepenColor(LerpColor(palette.BackEnd, deepCool, 0.54), 0.09 + (aggression * 0.08));
        var frontStart = LerpColor(palette.FrontStart, palette.HotAccent, aggression * 0.1);
        var frontHighlight = LerpColor(palette.FrontStart, Color.FromRgb(0xFF, 0xFF, 0xFF), 0.24);
        var frontMid = LerpColor(palette.FrontMid, palette.HotAccent, aggression * 0.22);
        var frontWarm = LerpColor(palette.HotAccent, Color.FromRgb(0xFF, 0xB4, 0x79), 0.18 - (aggression * 0.06));
        var frontEnd = DeepenColor(LerpColor(palette.FrontEnd, deepCool, 0.48), 0.1 + (aggression * 0.08));

        UpdateGradientBrush(_voiceGlowBrush, glowStart, glowArc, glowEnd);
        UpdateGradientBrush(_voiceBackBrush, backStart, backCool, backMid, backWarm, backEnd);
        UpdateGradientBrush(_voiceFrontBrush, frontStart, frontHighlight, frontMid, frontWarm, frontEnd);
    }

    private VoicePalette GetInterpolatedPalette()
    {
        if (_paletteBlend >= 1.0)
        {
            return _targetPalette;
        }

        var blend = SmoothStep(_paletteBlend);
        return LerpPalette(_currentPalette, _targetPalette, blend);
    }

    private VoicePalette PickVoicePalette(bool aggressiveBeat)
    {
        var candidates = aggressiveBeat
            ? new[] { 2, 3, 5, 6 }
            : new[] { 0, 1, 2, 4, 6 };

        var nextPalette = _targetPalette;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var candidateIndex = candidates[_paletteRandom.Next(candidates.Length)];
            var candidate = VoicePalettes[candidateIndex];
            if (!candidate.Equals(_targetPalette))
            {
                nextPalette = candidate;
                break;
            }
        }

        return nextPalette;
    }

    private static LinearGradientBrush CreateGradientBrush(Point startPoint, Point endPoint, params Color[] colors)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = startPoint,
            EndPoint = endPoint,
        };

        if (colors.Length == 1)
        {
            brush.GradientStops.Add(new GradientStop(colors[0], 0.0));
            brush.GradientStops.Add(new GradientStop(colors[0], 1.0));
        }
        else
        {
            for (var index = 0; index < colors.Length; index++)
            {
                var offset = colors.Length == 1 ? 1.0 : index / (double)(colors.Length - 1);
                brush.GradientStops.Add(new GradientStop(colors[index], offset));
            }
        }

        return brush;
    }

    private static void UpdateGradientBrush(LinearGradientBrush brush, params Color[] colors)
    {
        if (brush.GradientStops.Count != colors.Length)
        {
            brush.GradientStops.Clear();
            for (var index = 0; index < colors.Length; index++)
            {
                var offset = colors.Length == 1 ? 1.0 : index / (double)(colors.Length - 1);
                brush.GradientStops.Add(new GradientStop(colors[index], offset));
            }

            return;
        }

        for (var index = 0; index < colors.Length; index++)
        {
            brush.GradientStops[index].Color = colors[index];
        }
    }

    private static VoicePalette LerpPalette(VoicePalette from, VoicePalette to, double amount)
    {
        return new VoicePalette(
            LerpColor(from.GlowStart, to.GlowStart, amount),
            LerpColor(from.GlowEnd, to.GlowEnd, amount),
            LerpColor(from.BackStart, to.BackStart, amount),
            LerpColor(from.BackMid, to.BackMid, amount),
            LerpColor(from.BackEnd, to.BackEnd, amount),
            LerpColor(from.FrontStart, to.FrontStart, amount),
            LerpColor(from.FrontMid, to.FrontMid, amount),
            LerpColor(from.FrontEnd, to.FrontEnd, amount),
            LerpColor(from.HotAccent, to.HotAccent, amount));
    }

    private static Color DeepenColor(Color color, double amount)
    {
        return LerpColor(color, Color.FromRgb(0x55, 0x67, 0xA8), Math.Clamp(amount, 0.0, 1.0));
    }

    private static Color LerpColor(Color from, Color to, double amount)
    {
        var blend = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromArgb(
            (byte)Math.Round(from.A + ((to.A - from.A) * blend)),
            (byte)Math.Round(from.R + ((to.R - from.R) * blend)),
            (byte)Math.Round(from.G + ((to.G - from.G) * blend)),
            (byte)Math.Round(from.B + ((to.B - from.B) * blend)));
    }

    private static double SmoothStep(double value)
    {
        var clamped = Math.Clamp(value, 0.0, 1.0);
        return clamped * clamped * (3.0 - (2.0 * clamped));
    }

    private static Geometry CreateClosedSplineGeometry(IReadOnlyList<Point> points)
    {
        if (points.Count < 3)
        {
            return Geometry.Empty;
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: true);
            for (var index = 0; index < points.Count; index++)
            {
                var p0 = points[(index - 1 + points.Count) % points.Count];
                var p1 = points[index];
                var p2 = points[(index + 1) % points.Count];
                var p3 = points[(index + 2) % points.Count];

                var control1 = new Point(
                    p1.X + ((p2.X - p0.X) / 6.0),
                    p1.Y + ((p2.Y - p0.Y) / 6.0));
                var control2 = new Point(
                    p2.X - ((p3.X - p1.X) / 6.0),
                    p2.Y - ((p3.Y - p1.Y) / 6.0));

                context.BezierTo(control1, control2, p2, isStroked: true, isSmoothJoin: true);
            }
        }

        if (geometry.CanFreeze)
        {
            geometry.Freeze();
        }

        return geometry;
    }

    private static double ShapePeakSignal(double peak)
    {
        if (peak <= VoiceNoiseFloor)
        {
            return 0.0;
        }

        var normalizedPeak = Math.Clamp((peak - VoiceNoiseFloor) / (1.0 - VoiceNoiseFloor), 0.0, 1.0);
        var liftedPeak = Math.Pow(normalizedPeak, VoiceSensitivityExponent);
        return Math.Clamp((liftedPeak * VoiceSensitivityGain) + (normalizedPeak * VoiceSensitivityBlend), 0.0, 1.0);
    }

    private static double MoveTowards(double current, double target, double amount)
    {
        if (current < target)
        {
            return Math.Min(target, current + amount);
        }

        return Math.Max(target, current - amount);
    }

    private static void ResetIconTransforms(
        ScaleTransform scaleTransform,
        RotateTransform rotateTransform,
        TranslateTransform translateTransform)
    {
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        rotateTransform.BeginAnimation(RotateTransform.AngleProperty, null);
        translateTransform.BeginAnimation(TranslateTransform.XProperty, null);

        scaleTransform.ScaleX = 1;
        scaleTransform.ScaleY = 1;
        rotateTransform.Angle = 0;
        translateTransform.X = 0;
    }

    private static void AnimateKeyFrames(
        DependencyObject target,
        DependencyProperty property,
        params (double value, int milliseconds)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(IconAnimationDuration),
        };

        foreach (var (value, milliseconds) in frames)
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(
                value,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)),
                CreateEase()));
        }

        (target as Animatable)?.BeginAnimation(property, animation);
    }

    private static IEasingFunction CreateEase()
    {
        return new CubicEase
        {
            EasingMode = EasingMode.EaseInOut,
        };
    }

    private static void ApplyRoundedShellClip(FrameworkElement element, double radius)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return;
        }

        var clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius);
        if (clip.CanFreeze)
        {
            clip.Freeze();
        }

        element.Clip = clip;
    }

    private readonly record struct VoicePalette(
        Color GlowStart,
        Color GlowEnd,
        Color BackStart,
        Color BackMid,
        Color BackEnd,
        Color FrontStart,
        Color FrontMid,
        Color FrontEnd,
        Color HotAccent);
}
