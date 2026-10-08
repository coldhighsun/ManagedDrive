namespace ManagedDrive.App.Controls;

/// <summary>
/// Theme resource keys of the colours used by the space-usage treemap, its legend and its lists.
/// </summary>
public static class SpaceBrushKeys
{
    /// <summary>
    /// The key of the block that stands for several small items.
    /// </summary>
    public const string Merged = "AppSpaceMerged";

    /// <summary>
    /// The key of a directory.
    /// </summary>
    public const string Folder = "AppSpaceFolder";

    /// <summary>
    /// The key of the colour of a category.
    /// </summary>
    /// <param name="category">The category.</param>
    /// <returns>For example <c>AppSpaceImage</c>.</returns>
    public static string For(FileCategory category) => $"AppSpace{category}";

    /// <summary>
    /// The key of the colour of a file name or extension.
    /// </summary>
    /// <param name="fileNameOrExtension">A file name such as <c>a.PNG</c>, or just an extension such as <c>.png</c>.</param>
    /// <returns>The key of the category <see cref="FileCategories.Of"/> picks.</returns>
    public static string For(string fileNameOrExtension) => For(FileCategories.Of(fileNameOrExtension));
}
