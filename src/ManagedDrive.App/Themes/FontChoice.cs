namespace ManagedDrive.App.Themes;

/// <summary>
/// One entry of a font picker.
/// </summary>
/// <param name="Source">The font's name to save.</param>
/// <param name="DisplayName">The font's name to show.</param>
public readonly record struct FontChoice(string Source, string DisplayName);
