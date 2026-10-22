using System.Windows.Controls;
using System.Windows.Interop;

namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Gives a borderless window the operating system's own rounded corners. The window must not use
/// <c>AllowsTransparency</c>: DWM then clips the whole window, and its content, to the rounded
/// shape, instead of the app drawing a rounded border whose anti-aliased edge leaves a seam.
/// </summary>
internal static class WindowCornerHelper
{
    /// <summary>
    /// The first Windows build (Windows 11) whose DWM supports
    /// <see cref="WindowNativeMethods.DwmwaWindowCornerPreference"/>.
    /// </summary>
    private const int MinimumBuild = 22000;

    /// <summary>
    /// Attached property that gives a window the native rounded corners. It is set by
    /// <c>BorderlessWindowStyle</c> in <c>Themes/AppTheme.xaml</c>, so a window gets the corners by
    /// using that style (or one based on it) and no window has to call <see cref="Apply"/> itself.
    /// Setting it back to <see langword="false"/> takes the corners away again.
    /// </summary>
    public static readonly DependencyProperty UseNativeCornersProperty = DependencyProperty.RegisterAttached(
        "UseNativeCorners",
        typeof(bool),
        typeof(WindowCornerHelper),
        new PropertyMetadata(false, OnUseNativeCornersChanged));

    /// <summary>
    /// Gets whether the running Windows version rounds window corners and draws the border and shadow
    /// of a borderless window itself.
    /// </summary>
    public static bool IsSupportedByThisOs => IsSupported(Environment.OSVersion.Version.Build);

    /// <summary>
    /// Tells whether a Windows build supports DWM-drawn rounded corners.
    /// </summary>
    /// <param name="osBuild">The Windows build number.</param>
    /// <returns><see langword="true"/> from Windows 11 (build 22000) onwards.</returns>
    public static bool IsSupported(int osBuild) => osBuild >= MinimumBuild;

    /// <summary>
    /// Gets <see cref="UseNativeCornersProperty"/>.
    /// </summary>
    /// <param name="element">The element, normally a window.</param>
    /// <returns>Whether the element gets the native rounded corners.</returns>
    public static bool GetUseNativeCorners(DependencyObject element) =>
        (bool)element.GetValue(UseNativeCornersProperty);

    /// <summary>
    /// Sets <see cref="UseNativeCornersProperty"/>.
    /// </summary>
    /// <param name="element">The element, normally a window.</param>
    /// <param name="value">Whether the element gets the native rounded corners.</param>
    public static void SetUseNativeCorners(DependencyObject element, bool value) =>
        element.SetValue(UseNativeCornersProperty, value);

    /// <summary>
    /// Asks DWM to round <paramref name="window"/>'s corners once its native handle exists. A
    /// <c>WindowStyle="None"</c> window has no caption style, which Windows 11 needs before it
    /// rounds a window on its own, so the preference has to be set explicitly; DWM then also draws
    /// the border and the shadow. Windows 10 does not know the attribute: the window stays square
    /// and gets no system border, so the window draws a 1 px outline of its own instead.
    /// </summary>
    /// <param name="window">The window to round.</param>
    /// <param name="nativeCornersSupported">
    /// Whether this Windows version supports the corner attribute; see <see cref="IsSupportedByThisOs"/>.
    /// </param>
    internal static void Apply(Window window, bool nativeCornersSupported)
    {
        if (!nativeCornersSupported)
        {
            window.BorderThickness = new(1);
            window.SetResourceReference(Control.BorderBrushProperty, "AppDivider");
            return;
        }

        var helper = new WindowInteropHelper(window);
        if (helper.Handle != IntPtr.Zero)
        {
            SetCorner(helper.Handle, WindowNativeMethods.DwmwcpRound);
            return;
        }

        // There is no handle yet. Set the corners when there is one, unless the property was
        // switched off in the meantime.
        window.SourceInitialized += (_, _) =>
        {
            if (GetUseNativeCorners(window))
            {
                SetCorner(helper.Handle, WindowNativeMethods.DwmwcpRound);
            }
        };
    }

    /// <summary>
    /// Takes the corners <see cref="Apply"/> gave a window away again: on Windows 11 the corner
    /// preference goes back to the system default (a window that has no handle yet has nothing to
    /// undo, see <see cref="Apply"/>), on Windows 10 the outline is removed.
    /// </summary>
    /// <param name="window">The window to take the corners from.</param>
    /// <param name="nativeCornersSupported">
    /// Whether this Windows version supports the corner attribute; see <see cref="IsSupportedByThisOs"/>.
    /// </param>
    internal static void Remove(Window window, bool nativeCornersSupported)
    {
        if (!nativeCornersSupported)
        {
            window.ClearValue(Control.BorderThicknessProperty);
            window.ClearValue(Control.BorderBrushProperty);
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            SetCorner(handle, WindowNativeMethods.DwmwcpDefault);
        }
    }

    /// <summary>
    /// Gives a window the corners when the property is switched on, and takes them away when it is
    /// switched off.
    /// </summary>
    /// <param name="element">The element the property changed on.</param>
    /// <param name="e">The change.</param>
    private static void OnUseNativeCornersChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not Window window)
        {
            return;
        }

        if (e.NewValue is true)
        {
            Apply(window, IsSupportedByThisOs);
        }
        else
        {
            Remove(window, IsSupportedByThisOs);
        }
    }

    /// <summary>
    /// Sets <c>DWMWA_WINDOW_CORNER_PREFERENCE</c> on the given window handle. The return value is
    /// ignored on purpose: the call is only made where it is supported.
    /// </summary>
    /// <param name="handle">The native window handle.</param>
    /// <param name="preference">The <c>DWMWCP_*</c> value.</param>
    private static void SetCorner(nint handle, int preference) =>
        WindowNativeMethods.SetDwmAttribute(handle, WindowNativeMethods.DwmwaWindowCornerPreference, preference);
}
