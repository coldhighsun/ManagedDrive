using ManagedDrive.App.Views;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests the icon table of <see cref="ConfirmKind"/>.
/// </summary>
public sealed class ConfirmKindTests
{
    /// <summary>
    /// Verifies that every kind has its own glyph, so the kinds can be told apart.
    /// </summary>
    [Fact]
    public void Describe_AllKinds_HaveDistinctGlyphs()
    {
        // Act
        var glyphs = Enum.GetValues<ConfirmKind>().Select(kind => ConfirmKindIcons.Get(kind).Glyph).ToList();

        // Assert
        Assert.Equal(glyphs.Count, glyphs.Distinct().Count());
    }

    /// <summary>
    /// Verifies that a warning uses the warning brush and an error the danger brush.
    /// </summary>
    [Fact]
    public void Describe_WarningAndError_UseTheirOwnBrushes()
    {
        // Assert
        Assert.Equal("AppWarning", ConfirmKindIcons.Get(ConfirmKind.Warning).BrushKey);
        Assert.Equal("AppDanger", ConfirmKindIcons.Get(ConfirmKind.Error).BrushKey);
    }
}
