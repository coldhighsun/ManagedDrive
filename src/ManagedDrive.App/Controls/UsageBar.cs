using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ManagedDrive.App.Controls;

/// <summary>
/// The filled part of a disk card's usage bar. It is a <see cref="System.Windows.Controls.Border"/> whose width follows
/// <see cref="Percent"/> through a horizontal scale that eases to each new value, so the bar glides instead of jumping.
/// The card puts it inside the track and sets its colour with a style.
/// </summary>
public sealed class UsageBar : System.Windows.Controls.Border
{
    /// <summary>
    /// How long the fill takes to reach a new value.
    /// </summary>
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Identifies the <see cref="Percent"/> dependency property.
    /// </summary>
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent),
        typeof(double),
        typeof(UsageBar),
        new PropertyMetadata(0.0, OnPercentChanged));

    /// <summary>
    /// The scale of the fill. Not frozen, so it can be animated.
    /// </summary>
    private readonly ScaleTransform _scale = new(0, 1);

    /// <summary>
    /// Initializes the bar: stretched over the track, growing from its left edge.
    /// </summary>
    public UsageBar()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        RenderTransformOrigin = new(0, 0.5);
        RenderTransform = _scale;
        _scale.ScaleX = UsageBarMath.ToScale(Percent);
    }

    /// <summary>
    /// Gets or sets the used percentage from 0 to 100.
    /// </summary>
    public double Percent
    {
        get => (double)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    /// <summary>
    /// Eases the fill to the new percentage; before the bar is loaded it jumps there, so a new card does not
    /// start by growing from empty.
    /// </summary>
    private static void OnPercentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (UsageBar)d;
        var target = UsageBarMath.ToScale((double)e.NewValue);

        if (!bar.IsLoaded)
        {
            bar._scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            bar._scale.ScaleX = target;
            return;
        }

        bar._scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, AnimationDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }
}
