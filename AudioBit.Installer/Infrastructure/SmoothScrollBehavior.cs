using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace AudioBit.Installer.Infrastructure;

public static class SmoothScrollBehavior
{
    private const double DefaultWheelStep = 22.0;
    private const double DefaultSmoothingDuration = 205.0;
    private const double StopThreshold = 0.05;
    private const double SettleThreshold = 0.35;
    private const double VelocityThreshold = 2.0;
    private const double TouchpadBurstThresholdMilliseconds = 14.0;

    private static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller",
        typeof(SmoothScrollController),
        typeof(SmoothScrollBehavior),
        new PropertyMetadata(null));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(SmoothScrollBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty WheelStepProperty = DependencyProperty.RegisterAttached(
        "WheelStep",
        typeof(double),
        typeof(SmoothScrollBehavior),
        new PropertyMetadata(DefaultWheelStep));

    public static readonly DependencyProperty SmoothingDurationProperty = DependencyProperty.RegisterAttached(
        "SmoothingDuration",
        typeof(double),
        typeof(SmoothScrollBehavior),
        new PropertyMetadata(DefaultSmoothingDuration));

    public static bool GetIsEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        element.SetValue(IsEnabledProperty, value);
    }

    public static double GetWheelStep(DependencyObject element)
    {
        return (double)element.GetValue(WheelStepProperty);
    }

    public static void SetWheelStep(DependencyObject element, double value)
    {
        element.SetValue(WheelStepProperty, value);
    }

    public static double GetSmoothingDuration(DependencyObject element)
    {
        return (double)element.GetValue(SmoothingDurationProperty);
    }

    public static void SetSmoothingDuration(DependencyObject element, double value)
    {
        element.SetValue(SmoothingDurationProperty, value);
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            var controller = new SmoothScrollController(element);
            element.SetValue(ControllerProperty, controller);
            return;
        }

        if (element.GetValue(ControllerProperty) is SmoothScrollController existingController)
        {
            existingController.Dispose();
            element.ClearValue(ControllerProperty);
        }
    }

    private enum ScrollAxis
    {
        Vertical,
        Horizontal,
    }

    private enum ScrollInputKind
    {
        MouseWheel,
        Touchpad,
        Command,
    }

    private sealed class AxisAnimationState
    {
        public double CurrentOffset { get; set; }

        public double TargetOffset { get; set; }

        public double Velocity { get; set; }

        public ScrollInputKind LastInputKind { get; set; } = ScrollInputKind.MouseWheel;

        public bool IsAnimating { get; set; }
    }

    private sealed class SmoothScrollController : IDisposable
    {
        private readonly FrameworkElement _element;
        private readonly MouseWheelEventHandler _previewMouseWheelHandler;
        private readonly AxisAnimationState _verticalState = new();
        private readonly AxisAnimationState _horizontalState = new();

        private ScrollViewer? _scrollViewer;
        private bool _isRenderingHooked;
        private int _applyDepth;
        private long _lastRenderTimestamp;
        private long _lastWheelTimestamp;
        private int _lastWheelDelta;

        public SmoothScrollController(FrameworkElement element)
        {
            _element = element;
            _previewMouseWheelHandler = OnPreviewMouseWheel;

            _element.Loaded += OnLoaded;
            _element.Unloaded += OnUnloaded;
            _element.AddHandler(UIElement.PreviewMouseWheelEvent, _previewMouseWheelHandler, true);
            CommandManager.AddPreviewExecutedHandler(_element, OnPreviewExecuted);

            if (_element.IsLoaded)
            {
                AttachScrollViewer();
            }
        }

        public void Dispose()
        {
            DetachScrollViewer();
            StopRendering();

            _element.Loaded -= OnLoaded;
            _element.Unloaded -= OnUnloaded;
            _element.RemoveHandler(UIElement.PreviewMouseWheelEvent, _previewMouseWheelHandler);
            CommandManager.RemovePreviewExecutedHandler(_element, OnPreviewExecuted);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            AttachScrollViewer();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            DetachScrollViewer();
            StopRendering();
        }

        private void AttachScrollViewer()
        {
            if (_scrollViewer is not null)
            {
                return;
            }

            _scrollViewer = _element as ScrollViewer ?? FindDescendant<ScrollViewer>(_element);
            if (_scrollViewer is null)
            {
                return;
            }

            _scrollViewer.ScrollChanged += OnScrollChanged;
            SyncStatesToViewer();
        }

        private void DetachScrollViewer()
        {
            if (_scrollViewer is null)
            {
                return;
            }

            _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer = null;
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_scrollViewer is null)
            {
                AttachScrollViewer();
            }

            if (_scrollViewer is null || e.Delta == 0)
            {
                return;
            }

            if (e.OriginalSource is DependencyObject source && FindAncestor<Slider>(source) is not null)
            {
                return;
            }

            var axis = ResolveWheelAxis();
            if (GetScrollableExtent(axis) <= StopThreshold)
            {
                axis = axis == ScrollAxis.Vertical ? ScrollAxis.Horizontal : ScrollAxis.Vertical;
                if (GetScrollableExtent(axis) <= StopThreshold)
                {
                    return;
                }
            }

            var inputKind = IsTouchpadLike(e.Delta) ? ScrollInputKind.Touchpad : ScrollInputKind.MouseWheel;
            var distance = ResolveWheelDistance(e.Delta, inputKind);
            if (Math.Abs(distance) <= StopThreshold)
            {
                return;
            }

            var baseOffset = ResolveQueuedTargetBase(axis, inputKind);
            var nextTarget = ClampQueuedWheelTarget(axis, baseOffset - distance, inputKind);
            QueueAnimation(axis, nextTarget, inputKind);
            e.Handled = true;
        }

        private void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
        {
            if (_scrollViewer is null)
            {
                AttachScrollViewer();
            }

            if (_scrollViewer is null)
            {
                return;
            }

            if (e.OriginalSource is not DependencyObject source || !IsDescendantOf(_scrollViewer, source))
            {
                return;
            }

            if (!TryResolveCommandTarget(e.Command, e.Parameter, out var axis, out var targetOffset))
            {
                return;
            }

            QueueAnimation(axis, ClampOffset(axis, targetOffset), ScrollInputKind.Command);
            e.Handled = true;
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_scrollViewer is null)
            {
                return;
            }

            ClampTargetsToExtent();

            if (_applyDepth > 0)
            {
                return;
            }

            var extentChanged = Math.Abs(e.ExtentHeightChange) > StopThreshold || Math.Abs(e.ExtentWidthChange) > StopThreshold;
            var viewportChanged = Math.Abs(e.ViewportHeightChange) > StopThreshold || Math.Abs(e.ViewportWidthChange) > StopThreshold;
            if (extentChanged || viewportChanged || _scrollViewer.IsMouseCaptureWithin || HasExternalDrift())
            {
                SyncStatesToViewer();
                return;
            }

            if (!HasActiveAnimations())
            {
                SyncStatesToViewer();
            }
        }

        private void QueueAnimation(ScrollAxis axis, double targetOffset, ScrollInputKind inputKind)
        {
            if (_scrollViewer is null)
            {
                return;
            }

            var state = GetState(axis);
            var currentOffset = GetCurrentOffset(axis);
            var clampedTarget = ClampOffset(axis, targetOffset);
            var delta = clampedTarget - currentOffset;

            if (Math.Abs(delta) <= StopThreshold)
            {
                ApplyOffset(axis, clampedTarget);
                SyncAxisToOffset(state, clampedTarget);
                return;
            }

            state.CurrentOffset = currentOffset;
            state.TargetOffset = clampedTarget;
            state.LastInputKind = inputKind;

            if (!state.IsAnimating)
            {
                state.Velocity = 0.0;
            }

            if (Math.Sign(state.Velocity) != 0 && Math.Sign(state.Velocity) != Math.Sign(delta))
            {
                state.Velocity *= 0.35;
            }

            state.Velocity += Math.Sign(delta) * ResolveInitialVelocity(Math.Abs(delta), inputKind);
            state.IsAnimating = true;
            EnsureRendering();
        }

        private void EnsureRendering()
        {
            if (_isRenderingHooked)
            {
                return;
            }

            _isRenderingHooked = true;
            _lastRenderTimestamp = 0;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopRendering()
        {
            StopAxis(_verticalState);
            StopAxis(_horizontalState);
            _lastRenderTimestamp = 0;

            if (!_isRenderingHooked)
            {
                return;
            }

            _isRenderingHooked = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (_scrollViewer is null)
            {
                StopRendering();
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var frameSeconds = _lastRenderTimestamp == 0
                ? 1.0 / 60.0
                : Math.Clamp((now - _lastRenderTimestamp) / (double)Stopwatch.Frequency, 1.0 / 240.0, 1.0 / 20.0);
            _lastRenderTimestamp = now;

            var hasVerticalAnimation = UpdateAxis(ScrollAxis.Vertical, frameSeconds);
            var hasHorizontalAnimation = UpdateAxis(ScrollAxis.Horizontal, frameSeconds);
            if (!hasVerticalAnimation && !hasHorizontalAnimation)
            {
                StopRendering();
            }
        }

        private bool UpdateAxis(ScrollAxis axis, double frameSeconds)
        {
            if (_scrollViewer is null)
            {
                return false;
            }

            var state = GetState(axis);
            if (!state.IsAnimating)
            {
                return false;
            }

            var currentOffset = GetCurrentOffset(axis);
            state.CurrentOffset = currentOffset;

            var smoothTime = ResolveSmoothTimeSeconds(state.LastInputKind);
            var maxSpeed = ResolveMaxSpeed(axis, smoothTime, state.LastInputKind);
            var velocity = state.Velocity;
            var nextOffset = SmoothDamp(currentOffset, state.TargetOffset, ref velocity, smoothTime, maxSpeed, frameSeconds);
            state.Velocity = velocity;
            nextOffset = ClampOffset(axis, nextOffset);

            if (nextOffset <= StopThreshold || nextOffset >= GetScrollableExtent(axis) - StopThreshold)
            {
                state.Velocity = 0.0;
            }

            ApplyOffset(axis, nextOffset);
            state.CurrentOffset = nextOffset;

            if (Math.Abs(state.TargetOffset - nextOffset) <= SettleThreshold && Math.Abs(state.Velocity) <= VelocityThreshold)
            {
                ApplyOffset(axis, state.TargetOffset);
                SyncAxisToOffset(state, state.TargetOffset);
                return false;
            }

            return true;
        }

        private void ApplyOffset(ScrollAxis axis, double offset)
        {
            if (_scrollViewer is null)
            {
                return;
            }

            _applyDepth++;
            try
            {
                if (axis == ScrollAxis.Vertical)
                {
                    _scrollViewer.ScrollToVerticalOffset(offset);
                }
                else
                {
                    _scrollViewer.ScrollToHorizontalOffset(offset);
                }
            }
            finally
            {
                _applyDepth--;
            }
        }

        private double ResolveSmoothTimeSeconds(ScrollInputKind inputKind)
        {
            var baseSeconds = Math.Clamp(GetSmoothingDuration(_element) / 1000.0, 0.16, 0.34);
            return inputKind switch
            {
                ScrollInputKind.Touchpad => baseSeconds * 0.84,
                ScrollInputKind.Command => baseSeconds * 0.92,
                _ => baseSeconds * 0.88,
            };
        }

        private double ResolveInitialVelocity(double distance, ScrollInputKind inputKind)
        {
            var wheelStep = Math.Max(8.0, GetWheelStep(_element));
            var velocity = Math.Max(distance * 9.5, wheelStep * 26.0);

            if (inputKind == ScrollInputKind.Touchpad)
            {
                velocity *= 0.74;
            }
            else if (inputKind == ScrollInputKind.Command)
            {
                velocity *= 1.08;
            }

            return Math.Clamp(velocity, wheelStep * 18.0, wheelStep * 96.0);
        }

        private double ResolveMaxSpeed(ScrollAxis axis, double smoothTime, ScrollInputKind inputKind)
        {
            if (_scrollViewer is null)
            {
                return 0.0;
            }

            var wheelStep = Math.Max(8.0, GetWheelStep(_element));
            var viewport = axis == ScrollAxis.Vertical ? _scrollViewer.ViewportHeight : _scrollViewer.ViewportWidth;
            var baseSpeed = Math.Max((viewport / Math.Max(0.12, smoothTime)) * 1.2, wheelStep * 82.0);

            if (inputKind == ScrollInputKind.Touchpad)
            {
                baseSpeed *= 0.86;
            }
            else if (inputKind == ScrollInputKind.Command)
            {
                baseSpeed *= 1.1;
            }

            return Math.Clamp(baseSpeed, wheelStep * 42.0, wheelStep * 220.0);
        }

        private double ResolveWheelDistance(int delta, ScrollInputKind inputKind)
        {
            var wheelStep = Math.Max(8.0, GetWheelStep(_element));
            if (inputKind == ScrollInputKind.Touchpad)
            {
                return delta * Math.Max(0.78, wheelStep / 18.0);
            }

            return (delta / (double)Mouse.MouseWheelDeltaForOneLine) * wheelStep * 1.55;
        }

        private double ResolveQueuedTargetBase(ScrollAxis axis, ScrollInputKind inputKind)
        {
            var currentOffset = GetCurrentOffset(axis);
            var state = GetState(axis);
            if (!state.IsAnimating || inputKind == ScrollInputKind.Command)
            {
                return currentOffset;
            }

            var carryDistance = state.TargetOffset - currentOffset;
            var retainFactor = inputKind == ScrollInputKind.Touchpad ? 0.28 : 0.55;
            return currentOffset + (carryDistance * retainFactor);
        }

        private double ClampQueuedWheelTarget(ScrollAxis axis, double targetOffset, ScrollInputKind inputKind)
        {
            if (_scrollViewer is null || inputKind == ScrollInputKind.Command)
            {
                return targetOffset;
            }

            var currentOffset = GetCurrentOffset(axis);
            var viewport = axis == ScrollAxis.Vertical ? _scrollViewer.ViewportHeight : _scrollViewer.ViewportWidth;
            var viewportDistance = viewport > StopThreshold
                ? viewport
                : Math.Max(180.0, GetWheelStep(_element) * 12.0);
            var maxQueuedDistance = inputKind == ScrollInputKind.Touchpad
                ? viewportDistance * 0.48
                : viewportDistance * 0.88;
            var queuedDistance = targetOffset - currentOffset;

            return currentOffset + Math.Clamp(queuedDistance, -maxQueuedDistance, maxQueuedDistance);
        }

        private ScrollAxis ResolveWheelAxis()
        {
            if (_scrollViewer is not null
                && (Keyboard.Modifiers & ModifierKeys.Shift) != 0
                && _scrollViewer.ScrollableWidth > StopThreshold)
            {
                return ScrollAxis.Horizontal;
            }

            return ScrollAxis.Vertical;
        }

        private bool TryResolveCommandTarget(ICommand command, object? parameter, out ScrollAxis axis, out double targetOffset)
        {
            axis = ScrollAxis.Vertical;
            targetOffset = 0.0;

            if (_scrollViewer is null)
            {
                return false;
            }

            var verticalBase = _verticalState.IsAnimating ? _verticalState.TargetOffset : _scrollViewer.VerticalOffset;
            var horizontalBase = _horizontalState.IsAnimating ? _horizontalState.TargetOffset : _scrollViewer.HorizontalOffset;
            var lineStep = Math.Max(12.0, GetWheelStep(_element) * 1.45);

            if (command == ScrollBar.LineDownCommand)
            {
                axis = ScrollAxis.Vertical;
                targetOffset = verticalBase + lineStep;
                return true;
            }

            if (command == ScrollBar.LineUpCommand)
            {
                axis = ScrollAxis.Vertical;
                targetOffset = verticalBase - lineStep;
                return true;
            }

            if (command == ScrollBar.PageDownCommand)
            {
                axis = ScrollAxis.Vertical;
                targetOffset = verticalBase + (_scrollViewer.ViewportHeight * 0.92);
                return true;
            }

            if (command == ScrollBar.PageUpCommand)
            {
                axis = ScrollAxis.Vertical;
                targetOffset = verticalBase - (_scrollViewer.ViewportHeight * 0.92);
                return true;
            }

            if (command == ScrollBar.LineRightCommand)
            {
                axis = ScrollAxis.Horizontal;
                targetOffset = horizontalBase + lineStep;
                return true;
            }

            if (command == ScrollBar.LineLeftCommand)
            {
                axis = ScrollAxis.Horizontal;
                targetOffset = horizontalBase - lineStep;
                return true;
            }

            if (command == ScrollBar.PageRightCommand)
            {
                axis = ScrollAxis.Horizontal;
                targetOffset = horizontalBase + (_scrollViewer.ViewportWidth * 0.92);
                return true;
            }

            if (command == ScrollBar.PageLeftCommand)
            {
                axis = ScrollAxis.Horizontal;
                targetOffset = horizontalBase - (_scrollViewer.ViewportWidth * 0.92);
                return true;
            }

            if (command == ScrollBar.ScrollToTopCommand)
            {
                axis = ScrollAxis.Vertical;
                targetOffset = 0.0;
                return true;
            }

            if (command == ScrollBar.ScrollToBottomCommand)
            {
                axis = ScrollAxis.Vertical;
                targetOffset = _scrollViewer.ScrollableHeight;
                return true;
            }

            if (command == ScrollBar.ScrollToLeftEndCommand)
            {
                axis = ScrollAxis.Horizontal;
                targetOffset = 0.0;
                return true;
            }

            if (command == ScrollBar.ScrollToRightEndCommand)
            {
                axis = ScrollAxis.Horizontal;
                targetOffset = _scrollViewer.ScrollableWidth;
                return true;
            }

            if (command == ScrollBar.ScrollToVerticalOffsetCommand || command == ScrollBar.DeferScrollToVerticalOffsetCommand)
            {
                axis = ScrollAxis.Vertical;
                return TryGetOffset(parameter, out targetOffset);
            }

            if (command == ScrollBar.ScrollToHorizontalOffsetCommand || command == ScrollBar.DeferScrollToHorizontalOffsetCommand)
            {
                axis = ScrollAxis.Horizontal;
                return TryGetOffset(parameter, out targetOffset);
            }

            return false;
        }

        private static bool TryGetOffset(object? parameter, out double offset)
        {
            switch (parameter)
            {
                case double doubleValue:
                    offset = doubleValue;
                    return true;
                case float floatValue:
                    offset = floatValue;
                    return true;
                case decimal decimalValue:
                    offset = (double)decimalValue;
                    return true;
                case int intValue:
                    offset = intValue;
                    return true;
                case string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue):
                    offset = parsedValue;
                    return true;
                default:
                    offset = 0.0;
                    return false;
            }
        }

        private void ClampTargetsToExtent()
        {
            if (_scrollViewer is null)
            {
                return;
            }

            _verticalState.TargetOffset = ClampOffset(ScrollAxis.Vertical, _verticalState.TargetOffset);
            _horizontalState.TargetOffset = ClampOffset(ScrollAxis.Horizontal, _horizontalState.TargetOffset);
        }

        private void SyncStatesToViewer()
        {
            if (_scrollViewer is null)
            {
                return;
            }

            SyncAxisToOffset(_verticalState, _scrollViewer.VerticalOffset);
            SyncAxisToOffset(_horizontalState, _scrollViewer.HorizontalOffset);
        }

        private bool HasExternalDrift()
        {
            if (_scrollViewer is null)
            {
                return false;
            }

            return (_verticalState.IsAnimating && Math.Abs(_scrollViewer.VerticalOffset - _verticalState.CurrentOffset) > 0.8)
                || (_horizontalState.IsAnimating && Math.Abs(_scrollViewer.HorizontalOffset - _horizontalState.CurrentOffset) > 0.8);
        }

        private bool HasActiveAnimations()
        {
            return _verticalState.IsAnimating || _horizontalState.IsAnimating;
        }

        private AxisAnimationState GetState(ScrollAxis axis)
        {
            return axis == ScrollAxis.Vertical ? _verticalState : _horizontalState;
        }

        private double GetCurrentOffset(ScrollAxis axis)
        {
            if (_scrollViewer is null)
            {
                return 0.0;
            }

            return axis == ScrollAxis.Vertical ? _scrollViewer.VerticalOffset : _scrollViewer.HorizontalOffset;
        }

        private double ClampOffset(ScrollAxis axis, double offset)
        {
            return Math.Clamp(offset, 0.0, GetScrollableExtent(axis));
        }

        private double GetScrollableExtent(ScrollAxis axis)
        {
            if (_scrollViewer is null)
            {
                return 0.0;
            }

            return axis == ScrollAxis.Vertical ? _scrollViewer.ScrollableHeight : _scrollViewer.ScrollableWidth;
        }

        private bool IsTouchpadLike(int delta)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedMilliseconds = _lastWheelTimestamp == 0
                ? double.MaxValue
                : (now - _lastWheelTimestamp) * 1000.0 / Stopwatch.Frequency;
            var sameDirection = Math.Sign(delta) == Math.Sign(_lastWheelDelta);
            var isTouchpadLike = Math.Abs(delta) < Mouse.MouseWheelDeltaForOneLine
                || (Math.Abs(delta) <= Mouse.MouseWheelDeltaForOneLine && sameDirection && elapsedMilliseconds <= TouchpadBurstThresholdMilliseconds);

            _lastWheelTimestamp = now;
            _lastWheelDelta = delta;
            return isTouchpadLike;
        }

        private static void SyncAxisToOffset(AxisAnimationState state, double offset)
        {
            state.CurrentOffset = offset;
            state.TargetOffset = offset;
            state.Velocity = 0.0;
            state.IsAnimating = false;
        }

        private static void StopAxis(AxisAnimationState state)
        {
            state.Velocity = 0.0;
            state.IsAnimating = false;
        }

        private static double SmoothDamp(
            double current,
            double target,
            ref double currentVelocity,
            double smoothTime,
            double maxSpeed,
            double deltaTime)
        {
            smoothTime = Math.Max(0.0001, smoothTime);
            deltaTime = Math.Max(0.0001, deltaTime);

            var omega = 2.0 / smoothTime;
            var x = omega * deltaTime;
            var exp = 1.0 / (1.0 + x + (0.48 * x * x) + (0.235 * x * x * x));
            var change = current - target;
            var originalTarget = target;

            var maxChange = maxSpeed * smoothTime;
            change = Math.Clamp(change, -maxChange, maxChange);
            target = current - change;

            var temp = (currentVelocity + (omega * change)) * deltaTime;
            currentVelocity = (currentVelocity - (omega * temp)) * exp;

            var output = target + ((change + temp) * exp);
            if ((originalTarget - current > 0.0) == (output > originalTarget))
            {
                output = originalTarget;
                currentVelocity = 0.0;
            }

            return output;
        }

        private static bool IsDescendantOf(DependencyObject ancestor, DependencyObject descendant)
        {
            for (DependencyObject? current = descendant; current is not null; current = GetParent(current))
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

        private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
        {
            for (DependencyObject? current = child; current is not null; current = GetParent(current))
            {
                if (current is T typedCurrent)
                {
                    return typedCurrent;
                }
            }

            return null;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var index = 0; index < childCount; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                if (child is T typedChild)
                {
                    return typedChild;
                }

                var descendant = FindDescendant<T>(child);
                if (descendant is not null)
                {
                    return descendant;
                }
            }

            return null;
        }
    }
}

