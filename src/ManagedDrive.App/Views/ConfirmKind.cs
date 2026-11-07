namespace ManagedDrive.App.Views;

/// <summary>
/// What a <see cref="ConfirmDialog"/> is about, which decides its icon and the color of it.
/// </summary>
public enum ConfirmKind
{
    /// <summary>A warning: something is overwritten, removed or cannot be undone. The default.</summary>
    Warning,

    /// <summary>A plain question with nothing at risk.</summary>
    Question,

    /// <summary>The report of something that went wrong.</summary>
    Error,
}

/// <summary>
/// The icon of each <see cref="ConfirmKind"/>.
/// </summary>
internal static class ConfirmKindIcons
{
    /// <summary>
    /// Gets the icon glyph (Segoe Fluent Icons) and the resource key of its brush for a kind.
    /// </summary>
    /// <param name="kind">The kind of dialog.</param>
    /// <returns>The glyph and the brush key.</returns>
    public static (string Glyph, string BrushKey) Get(ConfirmKind kind) => kind switch
    {
        ConfirmKind.Question => ("\uE897", "AppPrimary"),
        ConfirmKind.Error => ("\uEA39", "AppDanger"),
        _ => ("\uE7BA", "AppWarning"),
    };
}
