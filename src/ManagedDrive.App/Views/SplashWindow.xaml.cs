using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace ManagedDrive.App.Views;

/// <summary>
/// Startup splash shown while the saved disks are loaded in the background. Besides a status line
/// and a progress bar that the startup flow in <see cref="App"/> drives, it runs its own
/// decoration: drifting background circles, a pulsing logo and a scrolling feature list (all
/// skipped when Windows has animations turned off), a timer that ends its stay on top of other
/// windows, and a veto on close requests until <see cref="Dismiss"/> is called. Those parts have to
/// be kept in step with the startup flow in <see cref="App"/>.
/// </summary>
public partial class SplashWindow
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SplashWindow"/> class.
    /// </summary>
    /// <param name="showInTaskbar">
    /// Whether the splash has a taskbar button, so that a splash buried under other windows can be
    /// brought back. Off for a minimized start, which is meant to leave no trace but the tray icon.
    /// </param>
    public SplashWindow(bool showInTaskbar)
    {
        InitializeComponent();
        ShowInTaskbar = showInTaskbar;

        VersionText.Text = UpdateCheckService.GetRunningVersion();
        StatusText.Text = Loc.Get("Splash.Starting");
        FeatureViewport.OpacityMask = CreateEdgeFadeMask();
        Loaded += (_, _) =>
        {
            StartTopmostTimer();

            // Users who turned animations off in Windows get a still splash: no moving circles, no
            // pulsing logo, and one feature line instead of the scrolling list. The progress bar
            // keeps its own animation, which is how loading is shown.
            var animate = SystemParameters.ClientAreaAnimation;
            if (animate)
            {
                StartBackgroundAnimation();
            }

            StartFeatureList(animate);
        };
    }

    /// <summary>
    /// Builds the mask that fades the feature list out at the top and bottom of its viewport, with
    /// the same fraction the first scroll pass is positioned by (<see cref="SplashTicker.EdgeFadeFraction"/>).
    /// </summary>
    /// <returns>A vertical gradient from transparent through opaque back to transparent.</returns>
    private static LinearGradientBrush CreateEdgeFadeMask() => new(
        [
            new(Colors.Transparent, 0),
            new(Colors.Black, SplashTicker.EdgeFadeFraction),
            new(Colors.Black, 1 - SplashTicker.EdgeFadeFraction),
            new(Colors.Transparent, 1)
        ],
        new(0, 0),
        new(0, 1));

    /// <summary>
    /// Decides whether the splash is currently on top of other windows.
    /// </summary>
    private readonly SplashTopmostState _topmost = new();

    /// <summary>
    /// Ends staying on top once <see cref="SplashTopmostState.StayOnTopFor"/> has passed.
    /// </summary>
    private DispatcherTimer? _topmostTimer;

    /// <summary>
    /// Starts the timer after which the splash no longer stays on top.
    /// </summary>
    private void StartTopmostTimer()
    {
        _topmostTimer = new(DispatcherPriority.Normal, Dispatcher) { Interval = SplashTopmostState.StayOnTopFor };
        _topmostTimer.Tick += (_, _) => ReleaseTopmost();
        _topmostTimer.Start();
    }

    /// <summary>
    /// Stops the splash staying on top of other windows for good.
    /// </summary>
    public void ReleaseTopmost()
    {
        _topmostTimer?.Stop();
        _topmost.Release();
        Topmost = _topmost.IsTopmost;
    }

    /// <summary>
    /// Suspends or resumes staying on top, for a message box that has to appear above the splash.
    /// </summary>
    /// <param name="paused">Whether staying on top is suspended.</param>
    public void PauseTopmost(bool paused)
    {
        _topmost.Pause(paused);
        Topmost = _topmost.IsTopmost;
    }

    /// <summary>
    /// Whether the app has asked for the splash to close; until then a close request (Alt+F4) is
    /// refused, as the startup flow still owns the window and would later act on a closed one.
    /// </summary>
    private bool _dismissed;

    /// <summary>
    /// Closes the splash for good. Use this instead of <see cref="Window.Close"/>, which the window
    /// refuses unless it comes from here.
    /// </summary>
    public void Dismiss()
    {
        _dismissed = true;
        _topmostTimer?.Stop();
        Close();
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_dismissed)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Fills the feature list from the <c>Splash.Feature{n}</c> strings, starting at a random one.
    /// With <paramref name="animate"/> it scrolls upwards through the viewport, forever; without,
    /// a single random feature is shown, centred.
    /// </summary>
    /// <param name="animate">Whether the list scrolls.</param>
    private void StartFeatureList(bool animate)
    {
        var features = new List<string>();
        for (var n = 1; Application.Current.TryFindResource($"Splash.Feature{n}") is string text; n++)
        {
            features.Add(text);
        }

        if (features.Count == 0 || FeatureViewport.ActualHeight <= 0)
        {
            return;
        }

        var shown = SplashTicker.RotateFrom(features, Random.Shared.Next(features.Count));
        foreach (var feature in animate ? shown : shown.Take(1))
        {
            var line = new TextBlock
            {
                Text = feature,
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new(0, 0, 0, 14)
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, "AppForegroundMuted");
            FeatureList.Children.Add(line);
        }

        FeatureList.Width = FeatureViewport.ActualWidth;
        FeatureList.Measure(new(FeatureList.Width, double.PositiveInfinity));
        var contentHeight = FeatureList.DesiredSize.Height;
        var viewportHeight = FeatureViewport.ActualHeight;

        if (!animate)
        {
            Canvas.SetTop(FeatureList, Math.Max((viewportHeight - contentHeight) / 2, 0));
            return;
        }

        // The text is only translated, so it is rendered once and the bitmap moved (at the screen's
        // scale, to stay sharp).
        FeatureList.CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX);

        // The first pass starts with the list already in view, just below the top fade, so the area
        // is not empty while the splash opens; every later pass enters from the bottom.
        var initialOffset = SplashTicker.InitialOffset(viewportHeight);
        var firstPass = new DoubleAnimation(
            initialOffset,
            -contentHeight,
            SplashTicker.ScrollDuration(contentHeight, initialOffset, SplashTicker.PixelsPerSecond));
        firstPass.Completed += (_, _) => Drift(
            FeatureScroll,
            TranslateTransform.YProperty,
            viewportHeight,
            -contentHeight,
            SplashTicker.ScrollDuration(contentHeight, viewportHeight, SplashTicker.PixelsPerSecond),
            reverse: false);
        FeatureScroll.BeginAnimation(TranslateTransform.YProperty, firstPass);
    }

    /// <summary>
    /// Corner radius of the clip: the border's corner radius less its 1 px outline.
    /// </summary>
    private const double ClipCornerRadius = 9;

    /// <summary>
    /// Keeps the content, in particular the drifting circles, inside the window's rounded corners
    /// whatever the window's size, so the size is only declared once, in the XAML.
    /// </summary>
    /// <param name="sender">The clipped grid.</param>
    /// <param name="e">The size change data.</param>
    private void ClipHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ClipHost.Clip = new RectangleGeometry(
            new(0, 0, e.NewSize.Width, e.NewSize.Height), ClipCornerRadius, ClipCornerRadius);
    }

    /// <summary>
    /// Creates the background circles and starts the animations, with parameters picked by
    /// <see cref="SplashAnimationPlanner"/> so every start looks a little different.
    /// </summary>
    private void StartBackgroundAnimation()
    {
        foreach (var spec in SplashAnimationPlanner.CreateBlobs(Random.Shared, BlobCanvas.ActualWidth, BlobCanvas.ActualHeight))
        {
            // Positions may lie partly outside the area; the circles are clipped to the window.
            // Moved by a render transform rather than Canvas.Left/Top, which would run a layout
            // pass every frame, and cached as a bitmap so it is not re-rasterised every frame.
            var position = new TranslateTransform();
            var blob = new Ellipse
            {
                Width = spec.Diameter,
                Height = spec.Diameter,
                Opacity = spec.Opacity,
                RenderTransform = position,
                CacheMode = new BitmapCache(VisualTreeHelper.GetDpi(this).DpiScaleX)
            };
            blob.SetResourceReference(Shape.FillProperty, "AppPrimary");
            BlobCanvas.Children.Add(blob);

            Drift(position, TranslateTransform.XProperty, spec.StartX, spec.EndX, spec.DurationX);
            Drift(position, TranslateTransform.YProperty, spec.StartY, spec.EndY, spec.DurationY);
        }

        var pulse = SplashAnimationPlanner.CreateLogoPulse(Random.Shared);
        Drift(LogoScale, ScaleTransform.ScaleXProperty, 1.0, pulse.Scale, pulse.Period);
        Drift(LogoScale, ScaleTransform.ScaleYProperty, 1.0, pulse.Scale, pulse.Period);
    }

    /// <summary>
    /// Animates a property between two values, forever: back and forth with easing, or (without
    /// <paramref name="reverse"/>) repeatedly from the start value to the end value at constant speed.
    /// </summary>
    /// <param name="target">The object to animate.</param>
    /// <param name="property">The property to animate.</param>
    /// <param name="from">The starting value.</param>
    /// <param name="to">The value it moves to.</param>
    /// <param name="duration">How long one way takes.</param>
    /// <param name="reverse">Whether to move back again and ease the movement.</param>
    private static void Drift(IAnimatable target, DependencyProperty property, double from, double to, TimeSpan duration, bool reverse = true)
    {
        var animation = new DoubleAnimation(from, to, duration)
        {
            AutoReverse = reverse,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = reverse ? new SineEase { EasingMode = EasingMode.EaseInOut } : null
        };
        target.BeginAnimation(property, animation);
    }

    /// <summary>
    /// Lets the borderless splash be dragged by any point of its surface.
    /// </summary>
    /// <param name="sender">The window.</param>
    /// <param name="e">The mouse event data.</param>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // e.ButtonState is a snapshot from when the event was raised. On a busy UI thread the
        // button can be released before this runs, and DragMove then throws.
        if (e.ButtonState != MouseButtonState.Pressed || Mouse.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button went up between the check and the call: nothing to drag.
        }
    }

    /// <summary>
    /// Shows the loading progress as a fraction of the whole.
    /// </summary>
    /// <param name="fraction">The fraction loaded, from 0 to 1.</param>
    public void Report(double fraction)
    {
        LoadProgressBar.IsIndeterminate = false;
        LoadProgressBar.Value = Math.Clamp(fraction, 0.0, 1.0);
    }

    /// <summary>
    /// Replaces the status line under the logo.
    /// </summary>
    /// <param name="text">The text to show.</param>
    public void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}
