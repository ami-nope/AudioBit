using System.Windows;
using System.Windows.Controls;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace AudioBit.UI;

public partial class GpuPeakMeterControl : UserControl
{
    private const double ReleasePerSecond = 1.5;
    private const double HoldReleasePerSecond = 2.2;
    private static readonly TimeSpan HoldLinger = TimeSpan.FromMilliseconds(800);

    public static readonly DependencyProperty PeakValueProperty = DependencyProperty.Register(
        nameof(PeakValue),
        typeof(double),
        typeof(GpuPeakMeterControl),
        new PropertyMetadata(0.0, OnMeterPropertyChanged));

    public static readonly DependencyProperty IsMutedProperty = DependencyProperty.Register(
        nameof(IsMuted),
        typeof(bool),
        typeof(GpuPeakMeterControl),
        new PropertyMetadata(false, OnMeterPropertyChanged));

    public static readonly DependencyProperty IsMonitoringProperty = DependencyProperty.Register(
        nameof(IsMonitoring),
        typeof(bool),
        typeof(GpuPeakMeterControl),
        new PropertyMetadata(false, OnMeterPropertyChanged));

    public static readonly DependencyProperty LaneOpacityProperty = DependencyProperty.Register(
        nameof(LaneOpacity),
        typeof(double),
        typeof(GpuPeakMeterControl),
        new PropertyMetadata(1.0, OnMeterPropertyChanged));

    public static readonly DependencyProperty ShowPeakHoldProperty = DependencyProperty.Register(
        nameof(ShowPeakHold),
        typeof(bool),
        typeof(GpuPeakMeterControl),
        new PropertyMetadata(true, OnMeterPropertyChanged));

    private double _displayPeak;
    private double _peakHold;
    private TimeSpan _holdRemaining;

    public GpuPeakMeterControl()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public double PeakValue
    {
        get => (double)GetValue(PeakValueProperty);
        set => SetValue(PeakValueProperty, value);
    }

    public bool IsMuted
    {
        get => (bool)GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public bool IsMonitoring
    {
        get => (bool)GetValue(IsMonitoringProperty);
        set => SetValue(IsMonitoringProperty, value);
    }

    public double LaneOpacity
    {
        get => (double)GetValue(LaneOpacityProperty);
        set => SetValue(LaneOpacityProperty, value);
    }

    public bool ShowPeakHold
    {
        get => (bool)GetValue(ShowPeakHoldProperty);
        set => SetValue(ShowPeakHoldProperty, value);
    }

    internal bool IsRenderActive => IsLoaded && IsVisible && IsMonitoring;

    internal void AdvanceFrame(TimeSpan frameDelta)
    {
        var targetPeak = Clamp(PeakValue);
        var releaseAmount = ReleasePerSecond * frameDelta.TotalSeconds;

        if (_displayPeak < targetPeak)
        {
            _displayPeak = targetPeak;
        }
        else
        {
            _displayPeak = Math.Max(targetPeak, _displayPeak - releaseAmount);
        }

        UpdatePeakHold(frameDelta);
        MeterSurface.InvalidateVisual();
    }

    private static void OnMeterPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GpuPeakMeterControl control)
        {
            control.SyncDisplayState();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncDisplayState();
        GpuMeterRenderLoop.Register(this);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        GpuMeterRenderLoop.Unregister(this);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        GpuMeterRenderLoop.NotifyStateChanged();
        MeterSurface.InvalidateVisual();
    }

    private void SyncDisplayState()
    {
        var targetPeak = Clamp(PeakValue);

        if (!IsMonitoring)
        {
            _displayPeak = targetPeak;
            _peakHold = targetPeak;
            _holdRemaining = TimeSpan.Zero;
        }
        else if (_displayPeak < targetPeak)
        {
            _displayPeak = targetPeak;
            _peakHold = Math.Max(_peakHold, targetPeak);
            _holdRemaining = HoldLinger;
        }

        if (!ShowPeakHold)
        {
            _peakHold = _displayPeak;
            _holdRemaining = TimeSpan.Zero;
        }

        GpuMeterRenderLoop.NotifyStateChanged();
        MeterSurface.InvalidateVisual();
    }

    private void UpdatePeakHold(TimeSpan frameDelta)
    {
        if (!ShowPeakHold)
        {
            _peakHold = _displayPeak;
            _holdRemaining = TimeSpan.Zero;
            return;
        }

        if (_displayPeak >= _peakHold)
        {
            _peakHold = _displayPeak;
            _holdRemaining = HoldLinger;
            return;
        }

        if (_holdRemaining > TimeSpan.Zero)
        {
            _holdRemaining -= frameDelta;
            if (_holdRemaining < TimeSpan.Zero)
            {
                _holdRemaining = TimeSpan.Zero;
            }

            return;
        }

        var holdReleaseAmount = HoldReleasePerSecond * frameDelta.TotalSeconds;
        _peakHold = Math.Max(_displayPeak, _peakHold - holdReleaseAmount);
    }

    private void MeterSurface_OnPaintSurface(object? sender, SKPaintGLSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.BackendRenderTarget;
        canvas.Clear(SKColors.Transparent);

        var width = info.Width;
        var height = info.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var laneAlpha = (byte)Math.Clamp(Math.Round(Clamp(LaneOpacity) * 255.0), 0, 255);
        var trackRect = new SKRect(0.5f, 0.5f, width - 0.5f, height - 0.5f);
        var trackRadius = MathF.Max(6f, height / 2f);

        using var trackFill = new SKPaint
        {
            Color = ApplyAlpha(IsMuted ? new SKColor(24, 32, 39) : new SKColor(17, 26, 34), laneAlpha),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        using var trackStroke = new SKPaint
        {
            Color = ApplyAlpha(IsMuted ? new SKColor(52, 66, 78) : new SKColor(42, 58, 69), laneAlpha),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
        };

        canvas.DrawRoundRect(trackRect, trackRadius, trackRadius, trackFill);
        canvas.DrawRoundRect(trackRect, trackRadius, trackRadius, trackStroke);

        var inset = 3f;
        var contentRect = new SKRect(
            trackRect.Left + inset,
            trackRect.Top + inset,
            trackRect.Right - inset,
            trackRect.Bottom - inset);

        var segmentGap = MathF.Max(2f, height * 0.14f);
        var segmentCount = Math.Max(18, (int)Math.Round((contentRect.Width + segmentGap) / 14f));
        var segmentWidth = (contentRect.Width - ((segmentCount - 1) * segmentGap)) / segmentCount;
        if (segmentWidth <= 0)
        {
            return;
        }

        var trackSegmentColor = ApplyAlpha(IsMuted ? new SKColor(38, 48, 58) : new SKColor(31, 42, 51), laneAlpha);
        var activeStartColor = IsMuted ? new SKColor(109, 132, 148) : new SKColor(46, 220, 255);
        var activeEndColor = IsMuted ? new SKColor(139, 157, 169) : new SKColor(87, 237, 140);
        var litSegments = Clamp(_displayPeak) * segmentCount;

        using var segmentPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };

        for (var index = 0; index < segmentCount; index++)
        {
            var left = contentRect.Left + (index * (segmentWidth + segmentGap));
            var segmentRect = new SKRect(left, contentRect.Top, left + segmentWidth, contentRect.Bottom);
            var radius = MathF.Max(2.5f, segmentRect.Height / 2f);

            segmentPaint.Color = trackSegmentColor;
            canvas.DrawRoundRect(segmentRect, radius, radius, segmentPaint);

            var fillProgress = Math.Clamp(litSegments - index, 0.0, 1.0);
            if (fillProgress <= 0.0)
            {
                continue;
            }

            var fillRect = new SKRect(
                segmentRect.Left,
                segmentRect.Top,
                segmentRect.Left + (float)(segmentWidth * fillProgress),
                segmentRect.Bottom);

            if (fillRect.Right <= fillRect.Left)
            {
                continue;
            }

            segmentPaint.Color = ApplyAlpha(
                Lerp(activeStartColor, activeEndColor, index / Math.Max(1f, segmentCount - 1f)),
                laneAlpha);
            canvas.DrawRoundRect(fillRect, radius, radius, segmentPaint);
        }

        if (!ShowPeakHold)
        {
            return;
        }

        var holdX = contentRect.Left + (float)(Clamp(_peakHold) * contentRect.Width);
        holdX = Math.Clamp(holdX, contentRect.Left, contentRect.Right);

        using var holdPaint = new SKPaint
        {
            Color = ApplyAlpha(IsMuted ? new SKColor(173, 187, 197) : new SKColor(244, 251, 255), laneAlpha),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            StrokeCap = SKStrokeCap.Round,
        };

        canvas.DrawLine(holdX, contentRect.Top, holdX, contentRect.Bottom, holdPaint);
    }

    private static double Clamp(double value)
    {
        return Math.Clamp(value, 0.0, 1.0);
    }

    private static SKColor ApplyAlpha(SKColor color, byte alpha)
    {
        return color.WithAlpha((byte)(color.Alpha * (alpha / 255f)));
    }

    private static SKColor Lerp(SKColor start, SKColor end, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new SKColor(
            (byte)(start.Red + ((end.Red - start.Red) * amount)),
            (byte)(start.Green + ((end.Green - start.Green) * amount)),
            (byte)(start.Blue + ((end.Blue - start.Blue) * amount)),
            255);
    }
}
