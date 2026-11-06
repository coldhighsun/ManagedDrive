using ManagedDrive.App.Themes;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="StyleManager"/>'s pure resolution of a saved style choice.
/// </summary>
public sealed class StyleManagerTests
{
    /// <summary>
    /// Verifies that supported tags resolve to themselves, ignoring case.
    /// </summary>
    [Theory]
    [InlineData("b", "b")]
    [InlineData("C", "c")]
    public void Resolve_SupportedTag_ReturnsLowercaseTag(string saved, string expected)
    {
        Assert.Equal(expected, StyleManager.Resolve(saved));
    }

    /// <summary>
    /// Verifies that a missing or unknown choice falls back to the default style.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("z")]
    [InlineData("a")]
    [InlineData("dark")]
    public void Resolve_MissingOrUnknown_ReturnsDefault(string? saved)
    {
        Assert.Equal(StyleManager.DefaultStyle, StyleManager.Resolve(saved));
    }

    /// <summary>
    /// Verifies that the choice to persist is null for the default style and for anything that resolves
    /// to it, and the tag of the style otherwise.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("b", null)]
    [InlineData("a", null)]
    [InlineData("z", null)]
    [InlineData("c", "c")]
    [InlineData("C", "c")]
    public void Normalize_SavedChoice_IsNullForTheDefault(string? saved, string? expected)
    {
        Assert.Equal(expected, StyleManager.Normalize(saved));
    }

    /// <summary>
    /// Verifies which styles bring their own palette: both selectable styles do; the retired style A,
    /// whose colors are the theme's, does not.
    /// </summary>
    [Theory]
    [InlineData("a", false)]
    [InlineData("b", true)]
    [InlineData("c", true)]
    public void HasPalette_ReflectsShippedPalettes(string tag, bool expected)
    {
        Assert.Equal(expected, StyleManager.HasPalette(tag));
    }

    /// <summary>
    /// Verifies that the default style is one the application ships.
    /// </summary>
    [Fact]
    public void DefaultStyle_IsSupported()
    {
        Assert.Contains(StyleManager.DefaultStyle, StyleManager.SupportedStyles);
    }
}
