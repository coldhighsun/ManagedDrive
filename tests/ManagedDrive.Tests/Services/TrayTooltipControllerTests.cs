using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ManagedDrive.App.Localization;
using ManagedDrive.App.Services;
using ManagedDrive.App.Views;

namespace ManagedDrive.Tests.Services;

/// <summary>
/// Tests for the content and size handling of <see cref="TrayTooltipController"/>'s popup.
/// </summary>
public class TrayTooltipControllerTests
{
    /// <summary>
    /// Verifies that new popup content has a valid desired size before the popup lays it out.
    /// </summary>
    [Fact]
    public void ReplaceContent_NewContent_IsMeasuredBeforeLayout()
    {
        RunSta(() =>
        {
            // Arrange
            var popup = new Popup();

            // Act
            TrayTooltipController.ReplaceContent(popup, () => new Border { Width = 120, Height = 40 });

            // Assert
            var child = Assert.IsType<Border>(popup.Child);
            Assert.Equal(new System.Windows.Size(120, 40), TrayTooltipController.GetContentSize(child));
        });
    }

    /// <summary>
    /// Verifies that closing the popup also releases its content.
    /// </summary>
    [Fact]
    public void ClosePopup_OpenWithContent_ClosesAndDropsContent()
    {
        RunSta(() =>
        {
            // Arrange
            var popup = new Popup { Child = new Border() };

            // Act
            TrayTooltipController.ClosePopup(popup);

            // Assert
            Assert.False(popup.IsOpen);
            Assert.Null(popup.Child);
        });
    }

    /// <summary>
    /// Verifies that the desired size is used while the content has not been laid out yet.
    /// </summary>
    [Fact]
    public void GetContentSize_NotYetLaidOut_FallsBackToDesiredSize()
    {
        RunSta(() =>
        {
            // Arrange
            var content = new Border { Width = 120, Height = 40 };
            content.Measure(new(double.PositiveInfinity, double.PositiveInfinity));

            // Act
            var size = TrayTooltipController.GetContentSize(content);

            // Assert
            Assert.Equal(0, content.ActualWidth);
            Assert.Equal(new System.Windows.Size(120, 40), size);
        });
    }

    /// <summary>
    /// Verifies that the actual size is used once the content has been laid out.
    /// </summary>
    [Fact]
    public void GetContentSize_LaidOut_UsesActualSize()
    {
        RunSta(() =>
        {
            // Arrange
            var content = new Border { Width = 120, Height = 40 };
            content.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            content.Arrange(new(0, 0, 200, 80));

            // Act
            var size = TrayTooltipController.GetContentSize(content);

            // Assert
            Assert.Equal(new System.Windows.Size(content.ActualWidth, content.ActualHeight), size);
            Assert.True(size.Width > 0);
        });
    }

    /// <summary>
    /// Verifies that a tooltip view created after a palette swap resolves the new palette's colors.
    /// </summary>
    [Fact]
    public void ReplaceContent_AfterPaletteSwap_NewTooltipViewUsesTheNewPalette()
    {
        RunSta(() =>
        {
            // Arrange
            // The popup is the logical parent of its content, so its resources stand in for the
            // application resources that ThemeManager swaps, without creating a process-wide
            // Application that other tests assume does not exist.
            var popup = new Popup();
            var light = LoadPalette("AppTheme.Colors.Light.xaml");
            var dark = LoadPalette("AppTheme.Colors.Dark.xaml");
            popup.Resources.MergedDictionaries.Add(light);

            // Act
            TrayTooltipController.ReplaceContent(popup, () => new TrayTooltipView());
            var lightSurface = SurfaceColor(popup);

            popup.Resources.MergedDictionaries.Remove(light);
            popup.Resources.MergedDictionaries.Add(dark);
            TrayTooltipController.ReplaceContent(popup, () => new TrayTooltipView());
            var darkSurface = SurfaceColor(popup);

            // Assert
            Assert.Equal(((SolidColorBrush)light["AppSurface"]).Color, lightSurface);
            Assert.Equal(((SolidColorBrush)dark["AppSurface"]).Color, darkSurface);
            Assert.NotEqual(lightSurface, darkSurface);
        });
    }

    /// <summary>
    /// Verifies that the tooltip for a bottom-right taskbar icon stays inside
    /// the work area.
    /// </summary>
    [Fact]
    public void ComputePopupOrigin_IconAtBottomRight_StaysInsideTheWorkArea()
    {
        var workArea = new Rect(1920, 0, 1280, 1040);

        var origin = TrayTooltipController.ComputePopupOrigin(workArea, new(3150, 1050), new(300, 100));

        Assert.Equal(workArea.Right - 300 - 4, origin.X);
        Assert.Equal(workArea.Bottom - 100 - 16, origin.Y);
    }

    /// <summary>
    /// Verifies that an icon at the top edge pins the tooltip below the work-area top.
    /// </summary>
    [Fact]
    public void ComputePopupOrigin_IconAtTopEdge_PinsTooltipToTop()
    {
        var workArea = new Rect(0, 40, 1920, 1040);

        var origin = TrayTooltipController.ComputePopupOrigin(workArea, new(960, 10), new(300, 100));

        Assert.Equal(810, origin.X);
        Assert.Equal(workArea.Top + 16, origin.Y);
    }

    /// <summary>
    /// Verifies that an icon in the middle of the work area puts the tooltip just above it.
    /// </summary>
    [Fact]
    public void ComputePopupOrigin_IconMidScreen_PutsTooltipAboveIcon()
    {
        var origin = TrayTooltipController.ComputePopupOrigin(new(0, 0, 1920, 1080), new(960, 500), new(300, 100));

        Assert.Equal(new System.Windows.Point(810, 384), origin);
    }

    /// <summary>
    /// Verifies that every show builds a new view, refreshes the data context first, and that
    /// hiding drops the content.
    /// </summary>
    [Fact]
    public void ShowTooltip_ShownTwice_BuildsANewViewEachTimeAndHideDropsIt()
    {
        RunSta(() =>
        {
            // Arrange
            var source = new FakeHoverSource();
            var refreshed = 0;
            var context = new object();
            using var controller = new TrayTooltipController(context, () => refreshed++, source);

            // Act
            controller.ShowTooltip();
            var first = controller.Content;
            controller.HideTooltip();
            var afterHide = controller.Content;
            controller.ShowTooltip();
            var second = controller.Content;

            // Assert
            Assert.Equal(2, refreshed);
            Assert.IsType<TrayTooltipView>(first);
            Assert.Null(afterHide);
            Assert.IsType<TrayTooltipView>(second);
            Assert.NotSame(first, second);
            Assert.Same(context, ((TrayTooltipView)second!).DataContext);
        });
    }

    /// <summary>
    /// Verifies that opening the tray context menu closes the tooltip and drops its content.
    /// </summary>
    [Fact]
    public void ContextMenuOpening_TooltipShown_ClosesAndDropsContent()
    {
        RunSta(() =>
        {
            // Arrange
            var source = new FakeHoverSource();
            using var controller = new TrayTooltipController(new object(), () => { }, source);
            controller.ShowTooltip();

            // Act
            source.RaiseContextMenuOpening();

            // Assert
            Assert.False(controller.IsOpen);
            Assert.Null(controller.Content);
        });
    }

    /// <summary>
    /// Verifies that hovering schedules a show, and that no new show is scheduled during the
    /// cooldown that follows a hide.
    /// </summary>
    [Fact]
    public void MouseMoved_AfterHide_DoesNotScheduleAShowDuringTheCooldown()
    {
        RunSta(() =>
        {
            // Arrange
            var source = new FakeHoverSource();
            using var controller = new TrayTooltipController(new object(), () => { }, source);

            // Act
            source.RaiseMouseMoved(new(100, 100));
            var scheduledOnHover = controller.IsShowPending;
            source.RaiseContextMenuOpening();
            var scheduledAfterHide = controller.IsShowPending;
            source.RaiseMouseMoved(new(100, 100));
            var scheduledDuringCooldown = controller.IsShowPending;

            // Assert
            Assert.True(scheduledOnHover);
            Assert.False(scheduledAfterHide);
            Assert.False(scheduledDuringCooldown);
        });
    }

    /// <summary>
    /// Verifies that hovering does not schedule a show while the context menu is open.
    /// </summary>
    [Fact]
    public void MouseMoved_ContextMenuOpen_DoesNotScheduleAShow()
    {
        RunSta(() =>
        {
            // Arrange
            var source = new FakeHoverSource();
            using var controller = new TrayTooltipController(new object(), () => { }, source);
            source.IsContextMenuVisible = true;

            // Act
            source.RaiseMouseMoved(new(100, 100));

            // Assert
            Assert.False(controller.IsShowPending);
        });
    }

    /// <summary>
    /// Verifies that a disposed controller closes the tooltip and ignores further hover and
    /// context-menu notifications.
    /// </summary>
    [Fact]
    public void Dispose_TooltipShown_ClosesAndStopsListeningToTheSource()
    {
        RunSta(() =>
        {
            // Arrange
            var source = new FakeHoverSource();
            var controller = new TrayTooltipController(new object(), () => { }, source);
            controller.ShowTooltip();

            var subscribedBefore = HandlersOn(LanguageManager.Instance, controller);

            // Act
            controller.Dispose();
            source.RaiseMouseMoved(new(100, 100));

            // Assert
            Assert.Equal(1, subscribedBefore);
            Assert.Equal(0, HandlersOn(LanguageManager.Instance, controller));
            Assert.False(controller.IsOpen);
            Assert.Null(controller.Content);
            Assert.False(controller.IsShowPending);
            Assert.Equal(0, source.MouseMovedSubscribers);
            Assert.Equal(0, source.ContextMenuOpeningSubscribers);
        });
    }

    /// <summary>
    /// A hover source the test raises events on by hand.
    /// </summary>
    private sealed class FakeHoverSource : ITrayHoverSource
    {
        /// <inheritdoc />
        public event Action<System.Drawing.Point>? MouseMoved;

        /// <inheritdoc />
        public event Action? ContextMenuOpening;

        /// <summary>
        /// Gets the number of handlers currently subscribed to <see cref="MouseMoved"/>.
        /// </summary>
        public int MouseMovedSubscribers => MouseMoved?.GetInvocationList().Length ?? 0;

        /// <summary>
        /// Gets the number of handlers currently subscribed to <see cref="ContextMenuOpening"/>.
        /// </summary>
        public int ContextMenuOpeningSubscribers => ContextMenuOpening?.GetInvocationList().Length ?? 0;

        /// <summary>
        /// Raises <see cref="MouseMoved"/>.
        /// </summary>
        /// <param name="point">The reported screen position.</param>
        public void RaiseMouseMoved(System.Drawing.Point point) => MouseMoved?.Invoke(point);

        /// <summary>
        /// Raises <see cref="ContextMenuOpening"/>.
        /// </summary>
        public void RaiseContextMenuOpening() => ContextMenuOpening?.Invoke();

        /// <inheritdoc />
        public bool IsContextMenuVisible { get; set; }
    }

    /// <summary>
    /// Counts the <see cref="LanguageManager.LanguageChanged"/> handlers whose target is
    /// <paramref name="target"/>. The event cannot be raised in a test (it needs an Application),
    /// so the subscription is inspected through the event's backing field.
    /// </summary>
    /// <param name="manager">The language manager to inspect.</param>
    /// <param name="target">The object whose handlers are counted.</param>
    /// <returns>The number of handlers bound to <paramref name="target"/>.</returns>
    private static int HandlersOn(LanguageManager manager, object target)
    {
        var field = typeof(LanguageManager).GetField(nameof(LanguageManager.LanguageChanged), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var handler = (Delegate?)field?.GetValue(manager);
        return handler?.GetInvocationList().Count(d => ReferenceEquals(d.Target, target)) ?? 0;
    }

    /// <summary>
    /// Loads a theme palette from the copy placed next to the test assembly.
    /// </summary>
    /// <param name="fileName">The palette file name, such as <c>AppTheme.Colors.Dark.xaml</c>.</param>
    /// <returns>The loaded resource dictionary.</returns>
    private static ResourceDictionary LoadPalette(string fileName) =>
        new() { Source = new(Path.Combine(AppContext.BaseDirectory, "Themes", fileName), UriKind.Absolute) };

    /// <summary>
    /// Gets the background color the popup's tooltip view currently resolves for its outer border.
    /// </summary>
    /// <param name="popup">The popup holding a <see cref="TrayTooltipView"/>.</param>
    /// <returns>The border's background color.</returns>
    private static Color SurfaceColor(Popup popup)
    {
        var view = Assert.IsType<TrayTooltipView>(popup.Child);
        var border = Assert.IsType<Border>(view.Content);
        return Assert.IsType<SolidColorBrush>(border.Background).Color;
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a dedicated STA thread, as WPF elements require, and
    /// rethrows any exception on the calling thread.
    /// </summary>
    /// <param name="action">The work to run.</param>
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("STA test body failed.", error);
        }
    }
}
