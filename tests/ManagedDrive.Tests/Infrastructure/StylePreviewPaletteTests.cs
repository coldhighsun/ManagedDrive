using System.Windows.Media;
using ManagedDrive.App.Infrastructure;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="StylePreviewPalette"/>: which colours a style preview uses.
/// </summary>
public sealed class StylePreviewPaletteTests
{
    /// <summary>
    /// Verifies that the minimal style is recognised by its tag and everything else is the dashboard.
    /// </summary>
    [Theory]
    [InlineData("c", true)]
    [InlineData("C", true)]
    [InlineData("b", false)]
    [InlineData("a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsMinimal_Tag_OnlyCIsMinimal(string? style, bool expected)
    {
        Assert.Equal(expected, StylePreviewPalette.IsMinimal(style));
    }

    /// <summary>
    /// Verifies that the light and dark palettes of a style differ in their background.
    /// </summary>
    [Theory]
    [InlineData("b")]
    [InlineData("c")]
    public void For_LightAndDark_HaveDifferentBackgrounds(string style)
    {
        var light = StylePreviewPalette.For(style, dark: false);
        var dark = StylePreviewPalette.For(style, dark: true);

        Assert.NotEqual(light.Background, dark.Background);
    }

    /// <summary>
    /// Verifies that the two styles look different in the same theme.
    /// </summary>
    [Fact]
    public void For_DashboardAndMinimal_HaveDifferentPrimaryColours()
    {
        var dashboard = StylePreviewPalette.For("b", dark: false);
        var minimal = StylePreviewPalette.For("c", dark: false);

        Assert.NotEqual(dashboard.Primary, minimal.Primary);
    }

    /// <summary>
    /// Verifies that an unknown style gets the dashboard colours.
    /// </summary>
    [Fact]
    public void For_UnknownStyle_FallsBackToDashboard()
    {
        Assert.Same(StylePreviewPalette.For("b", dark: true), StylePreviewPalette.For("zzz", dark: true));
    }

    /// <summary>
    /// Verifies that the dashboard icons are colourful and the minimal ones are a single colour.
    /// </summary>
    [Fact]
    public void For_Accents_DashboardIsColourfulMinimalIsMonochrome()
    {
        var dashboard = StylePreviewPalette.For("b", dark: false).Accents;
        var minimal = StylePreviewPalette.For("c", dark: false).Accents;

        Assert.True(dashboard.Distinct().Count() > 1);
        Assert.Single(minimal.Distinct());
    }

    /// <summary>
    /// Verifies that every palette has the four accent colours the preview draws.
    /// </summary>
    [Theory]
    [InlineData("b", false)]
    [InlineData("b", true)]
    [InlineData("c", false)]
    [InlineData("c", true)]
    public void For_EveryPalette_HasFourAccents(string style, bool dark)
    {
        Assert.Equal(4, StylePreviewPalette.For(style, dark).Accents.Count);
    }
}
