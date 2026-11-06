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
    [InlineData("a", "a")]
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
    [InlineData("dark")]
    public void Resolve_MissingOrUnknown_ReturnsDefault(string? saved)
    {
        Assert.Equal(StyleManager.DefaultStyle, StyleManager.Resolve(saved));
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
