using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

public sealed class EnvRestoreGroupTextTests
{
    /// <summary>
    /// Restoring everything may touch TEMP and TMP.
    /// </summary>
    [Fact]
    public void AffectsTemp_AllGroups_IsTrue()
    {
        Assert.True(EnvRestoreGroupText.AffectsTemp(null));
    }

    /// <summary>
    /// The temp group, which the grouping fills with TEMP and TMP, may touch them.
    /// </summary>
    [Fact]
    public void AffectsTemp_TempGroup_IsTrue()
    {
        var group = Assert.Single(EnvRestoreGroups.Build(["TEMP", "tmp"]));

        Assert.True(EnvRestoreGroupText.AffectsTemp(group));
    }

    /// <summary>
    /// A cache group leaves TEMP and TMP alone, so its dialog needs no note about them.
    /// </summary>
    [Fact]
    public void AffectsTemp_NodeGroup_IsFalse()
    {
        Assert.False(EnvRestoreGroupText.AffectsTemp(new("node", ["npm_config_cache", "YARN_CACHE_FOLDER"])));
    }

    /// <summary>
    /// Without a TEMP note the question follows the body after a blank line.
    /// </summary>
    [Fact]
    public void ComposeConfirmBody_NoTempNote_PutsTheQuestionAfterABlankLine()
    {
        var text = EnvRestoreGroupText.ComposeConfirmBody("Body.", null, "Continue?");

        Assert.Equal("Body.\n\nContinue?", text);
    }

    /// <summary>
    /// The TEMP note joins the body's paragraph and stays before the question.
    /// </summary>
    [Fact]
    public void ComposeConfirmBody_WithTempNote_KeepsTheNoteBeforeTheQuestion()
    {
        var text = EnvRestoreGroupText.ComposeConfirmBody("Body.", "Note.", "Continue?");

        Assert.Equal("Body. Note.\n\nContinue?", text);
    }
}
