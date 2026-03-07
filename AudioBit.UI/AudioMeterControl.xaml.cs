using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AudioBit.UI;

public partial class AudioMeterControl : UserControl
{
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

    private readonly double[] _barBias =
    [
        0.22,
        0.37,
        0.52,
        0.76,
        0.58,
        0.88,
        0.49,
        0.69,
        0.43,
        0.27,
    ];

    private bool _hasAnimatedIn;
    private double _barPhase;
    private ScaleTransform[]? _meterTransforms;

    public AudioMeterControl()
    {
        InitializeComponent();
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

    private static void OnPeakValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AudioMeterControl control)
        {
            control.AnimateMeter((double)e.NewValue);
        }
    }

    private static void OnCardOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AudioMeterControl control)
        {
            control.AnimateOpacity((double)e.NewValue);
        }
    }

    private void RootGrid_OnLoaded(object sender, RoutedEventArgs e)
    {
        EnsureMeterTransforms();
        ApplyRoundedCardClip();

        if (_hasAnimatedIn)
        {
            AnimateOpacity(CardOpacity);
            AnimateMeter(PeakValue);
            return;
        }

        _hasAnimatedIn = true;

        var fadeAnimation = new DoubleAnimation
        {
            From = 0.0,
            To = CardOpacity,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        var moveAnimation = new DoubleAnimation
        {
            From = 10.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        CardBorder.BeginAnimation(OpacityProperty, fadeAnimation);
        CardTranslateTransform.BeginAnimation(TranslateTransform.YProperty, moveAnimation);
        AnimateMeter(PeakValue);
    }

    private void RootGrid_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyRoundedCardClip();
    }

    private void RootGrid_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        HoverOverlay.BeginAnimation(OpacityProperty, CreateAnimation(HoverOverlay.Opacity, 1.0, 120));
        CardTranslateTransform.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(CardTranslateTransform.Y, -2.0, 120));
    }

    private void RootGrid_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        HoverOverlay.BeginAnimation(OpacityProperty, CreateAnimation(HoverOverlay.Opacity, 0.0, 120));
        CardTranslateTransform.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(CardTranslateTransform.Y, 0.0, 120));
    }

    private void AnimateMeter(double targetPeak)
    {
        EnsureMeterTransforms();
        if (_meterTransforms is null)
        {
            return;
        }

        var clampedPeak = Math.Clamp(targetPeak, 0.0, 1.0);
        _barPhase += 0.62;

        for (var index = 0; index < _meterTransforms.Length; index++)
        {
            var wave = (Math.Sin(_barPhase + (index * 0.77)) + 1.0) * 0.11;
            var targetScale = Math.Clamp((clampedPeak * 1.1) + _barBias[index] - 0.45 + wave, 0.12, 1.0);

            _meterTransforms[index].BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation
                {
                    To = targetScale,
                    Duration = TimeSpan.FromMilliseconds(90),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                },
                HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void AnimateOpacity(double targetOpacity)
    {
        if (!_hasAnimatedIn)
        {
            return;
        }

        CardBorder.BeginAnimation(OpacityProperty, CreateAnimation(CardBorder.Opacity, targetOpacity, 120));
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
        ];
    }

    private static DoubleAnimation CreateAnimation(double from, double to, int durationMilliseconds)
    {
        return new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
    }

    private void ApplyRoundedCardClip()
    {
        if (CardBorder.ActualWidth <= 0 || CardBorder.ActualHeight <= 0)
        {
            return;
        }

        var clip = new RectangleGeometry(new Rect(0, 0, CardBorder.ActualWidth, CardBorder.ActualHeight), 20, 20);
        if (clip.CanFreeze)
        {
            clip.Freeze();
        }

        CardBorder.Clip = clip;
    }
}
