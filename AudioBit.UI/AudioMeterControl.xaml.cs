using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;

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

    private bool _hasAnimatedIn;

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
        if (_hasAnimatedIn)
        {
            AnimateOpacity(CardOpacity);
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

    private void RootGrid_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        HoverOverlay.BeginAnimation(OpacityProperty, CreateAnimation(0.0, 1.0, 120));
        CardTranslateTransform.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(CardTranslateTransform.Y, -2.0, 120));
    }

    private void RootGrid_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        HoverOverlay.BeginAnimation(OpacityProperty, CreateAnimation(HoverOverlay.Opacity, 0.0, 120));
        CardTranslateTransform.BeginAnimation(TranslateTransform.YProperty, CreateAnimation(CardTranslateTransform.Y, 0.0, 120));
    }

    private void AnimateMeter(double targetPeak)
    {
        var peakAnimation = new DoubleAnimation
        {
            To = Math.Clamp(targetPeak, 0.0, 1.0),
            Duration = TimeSpan.FromMilliseconds(80),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        MeterScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, peakAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateOpacity(double targetOpacity)
    {
        if (!_hasAnimatedIn)
        {
            return;
        }

        CardBorder.BeginAnimation(OpacityProperty, CreateAnimation(CardBorder.Opacity, targetOpacity, 120));
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
}
