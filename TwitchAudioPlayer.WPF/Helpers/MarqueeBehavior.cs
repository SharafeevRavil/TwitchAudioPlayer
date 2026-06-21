using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TwitchAudioPlayer.WPF.Helpers;

public static class MarqueeBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(MarqueeBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not TextBlock textBlock)
            return;

        if ((bool)args.NewValue)
        {
            textBlock.Loaded += OnLayoutChanged;
            textBlock.SizeChanged += OnLayoutChanged;
            DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                .AddValueChanged(textBlock, OnTextChanged);
            if (textBlock.Parent is FrameworkElement parent)
                parent.SizeChanged += OnLayoutChanged;
            Schedule(textBlock);
        }
        else
        {
            textBlock.Loaded -= OnLayoutChanged;
            textBlock.SizeChanged -= OnLayoutChanged;
            DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                .RemoveValueChanged(textBlock, OnTextChanged);
            if (textBlock.Parent is FrameworkElement parent)
                parent.SizeChanged -= OnLayoutChanged;
            Reset(textBlock);
        }
    }

    private static void OnTextChanged(object? sender, EventArgs args)
    {
        if (sender is TextBlock textBlock && GetIsEnabled(textBlock))
            Schedule(textBlock);
    }

    private static void OnLayoutChanged(object sender, EventArgs args)
    {
        var textBlock = sender as TextBlock ?? (sender as FrameworkElement)?.FindVisualChild<TextBlock>();
        if (textBlock is not null && GetIsEnabled(textBlock))
            Schedule(textBlock);
    }

    private static void Schedule(TextBlock textBlock) =>
        textBlock.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() => Restart(textBlock)));

    private static void Restart(TextBlock textBlock)
    {
        if (!textBlock.IsLoaded || textBlock.Parent is not FrameworkElement viewport)
            return;

        var transform = textBlock.RenderTransform as TranslateTransform;
        if (transform is null)
        {
            transform = new TranslateTransform();
            textBlock.RenderTransform = transform;
        }

        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.X = 0;
        textBlock.ClearValue(FrameworkElement.WidthProperty);
        textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = textBlock.DesiredSize.Width;
        var availableWidth = viewport.ActualWidth;
        if (availableWidth <= 0 || textWidth <= availableWidth + 1)
            return;

        textBlock.Width = textWidth;
        var distance = textWidth - availableWidth;
        var hold = TimeSpan.FromSeconds(1.25);
        var travel = TimeSpan.FromSeconds(Math.Clamp(distance / 38d, 3.5, 18));
        var resetHold = TimeSpan.FromSeconds(0.75);
        var animation = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(hold)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(hold + travel)));
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(hold + travel + resetHold)));
        transform.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private static void Reset(TextBlock textBlock)
    {
        if (textBlock.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
        }
        textBlock.ClearValue(FrameworkElement.WidthProperty);
    }

    private static T? FindVisualChild<T>(this DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T result)
                return result;
            if (FindVisualChild<T>(child) is { } nested)
                return nested;
        }
        return null;
    }
}
