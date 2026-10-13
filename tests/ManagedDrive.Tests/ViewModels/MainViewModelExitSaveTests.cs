using ManagedDrive.App.Services;
using ManagedDrive.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests how <see cref="MainViewModel"/> turns the parallel exit-save reports into overlay text.
/// Without an Application the localized strings resolve to their keys, so the keys are compared.
/// </summary>
public sealed class MainViewModelExitSaveTests : IDisposable
{
    /// <summary>
    /// Per-test temporary directory holding the settings file.
    /// </summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());

    /// <summary>
    /// Removes the temporary directory.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Several disks are summarised, not named, and the text does not change with the reporting disk.
    /// </summary>
    [Fact]
    public void MultipleDisks_ShowTheSummaryTextWhicheverDiskReports()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.BeginExitSave(new Dictionary<string, ulong> { ["R:"] = 1000, ["S:"] = 3000 });

        viewModel.ReportExitSaveProgress("R:", 0.5);
        viewModel.ShowExitSaveProgress();
        var afterFirst = viewModel.ExitSaveStatusText;
        viewModel.ReportExitSaveProgress("S:", 0.5);
        viewModel.ShowExitSaveProgress();

        Assert.Equal("Msg.ExitSavingDisks", afterFirst);
        Assert.Equal(afterFirst, viewModel.ExitSaveStatusText);
    }

    /// <summary>
    /// One disk keeps its own name.
    /// </summary>
    [Fact]
    public void SingleDisk_ShowsItsName()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.BeginExitSave(new Dictionary<string, ulong> { ["R:"] = 1000 });

        viewModel.ReportExitSaveProgress("R:", 0.5);
        viewModel.ShowExitSaveProgress();

        Assert.Equal("Msg.ExitSavingDisk", viewModel.ExitSaveStatusText);
        Assert.Equal(0.5, viewModel.ExitSaveProgress, 3);
    }

    /// <summary>
    /// A report for a disk that is not being written, or before the save began, changes nothing.
    /// </summary>
    [Fact]
    public void UnknownDiskOrNoSave_IsIgnored()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);

        viewModel.ReportExitSaveProgress("R:", 0.5);
        viewModel.ShowExitSaveProgress();
        viewModel.BeginExitSave(new Dictionary<string, ulong> { ["R:"] = 1000 });
        viewModel.ReportExitSaveProgress("S:", 0.5);
        viewModel.ShowExitSaveProgress();

        Assert.Equal(0.0, viewModel.ExitSaveProgress);
    }

    /// <summary>
    /// Completing the save fills the bar even if a disk never reported.
    /// </summary>
    [Fact]
    public void CompleteExitSave_FillsTheBar()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.BeginExitSave(new Dictionary<string, ulong> { ["R:"] = 1000, ["S:"] = 1000 });
        viewModel.ReportExitSaveProgress("R:", 1.0);
        viewModel.ShowExitSaveProgress();

        viewModel.CompleteExitSave();

        Assert.Equal(1.0, viewModel.ExitSaveProgress);
    }

    /// <summary>
    /// A disk whose save failed is not claimed as done, and neither is anything when disposing threw.
    /// </summary>
    [Fact]
    public void CompleteExitSave_DoesNotClaimFailures()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.BeginExitSave(new Dictionary<string, ulong> { ["R:"] = 1000, ["S:"] = 1000 });
        viewModel.ReportExitSaveProgress("R:", 0.4);
        viewModel.MarkExitSaveFailed("R:");
        viewModel.ReportExitSaveProgress("R:", 1.0);

        viewModel.CompleteExitSave();

        Assert.True(viewModel.ExitSaveProgress < 1.0);

        viewModel.BeginExitSave(new Dictionary<string, ulong> { ["R:"] = 1000 });
        viewModel.CompleteExitSave(markAllFinished: false);

        Assert.True(viewModel.ExitSaveProgress < 1.0);
    }

    /// <summary>
    /// A failure reported before the final saves began (e.g. a periodic auto-save) does not mark
    /// the disk, so a final save that then succeeds is shown as finished.
    /// </summary>
    [Fact]
    public void FailureBeforeBeginExitSave_IsIgnored()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.MarkExitSaveFailed("R:");
        viewModel.BeginExitSave(new Dictionary<string, ulong> { ["R:"] = 1000 });

        viewModel.ReportExitSaveProgress("R:", 1.0);
        viewModel.CompleteExitSave();

        Assert.Equal(1.0, viewModel.ExitSaveProgress);
        Assert.Equal("Msg.ExitSavingFinishing", viewModel.ExitSaveStatusText);
    }

    /// <summary>
    /// With every disk done but the save still wrapping up, the text says so instead of 'N/N done'.
    /// </summary>
    [Fact]
    public void AllDisksDone_ShowsTheFinishingText()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.BeginExitSave(new Dictionary<string, ulong> { ["R:"] = 1000, ["S:"] = 1000 });

        viewModel.ReportExitSaveProgress("R:", 1.0);
        viewModel.ReportExitSaveProgress("S:", 1.0);
        viewModel.ShowExitSaveProgress();

        Assert.Equal("Msg.ExitSavingFinishing", viewModel.ExitSaveStatusText);
        Assert.True(viewModel.ExitSaveProgress < 1.0);
    }

    /// <summary>
    /// When every disk is finished or failed and one failed, the overlay says so instead of 'k/N'.
    /// </summary>
    [Fact]
    public void EndedWithFailures_ShowsTheFailureText()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.BeginExitSave(new Dictionary<string, ulong> { ["R:"] = 1000, ["S:"] = 1000 });
        viewModel.ReportExitSaveProgress("R:", 0.4);
        viewModel.MarkExitSaveFailed("R:");
        viewModel.ReportExitSaveProgress("S:", 1.0);

        viewModel.ShowExitSaveProgress();

        Assert.Equal("Msg.ExitSavingFailed", viewModel.ExitSaveStatusText);
    }

    /// <summary>
    /// With nothing to write, completing keeps the generic text instead of naming zero disks.
    /// </summary>
    [Fact]
    public void CompleteExitSave_WithNoDisksToWrite_KeepsTheGenericText()
    {
        using var mountManager = new MountManager();
        using var viewModel = CreateViewModel(mountManager);
        viewModel.IsExiting = true;
        viewModel.BeginExitSave(new Dictionary<string, ulong>());

        viewModel.CompleteExitSave();

        Assert.Equal("Msg.ExitSaving", viewModel.ExitSaveStatusText);
        Assert.Equal(string.Empty, viewModel.ExitSaveDetailText);
        Assert.Equal(1.0, viewModel.ExitSaveProgress);
    }

    /// <summary>
    /// Both language files define the summary text with the same placeholders.
    /// </summary>
    [Theory]
    [InlineData("Strings.en-US.xaml")]
    [InlineData("Strings.zh-CN.xaml")]
    public void SummaryString_ExistsInEveryLanguageWithBothPlaceholders(string file)
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", file));

        var match = System.Text.RegularExpressions.Regex.Match(xaml, "x:Key=\"Msg.ExitSavingDisks\">([^<]*)<");

        Assert.True(match.Success);
        Assert.Contains("{0}", match.Groups[1].Value);
        Assert.Contains("{1}", match.Groups[1].Value);
    }

    /// <summary>
    /// Both language files define the finishing and failure texts, the latter with both placeholders.
    /// </summary>
    [Theory]
    [InlineData("Strings.en-US.xaml")]
    [InlineData("Strings.zh-CN.xaml")]
    public void FinishingAndFailureStrings_ExistInEveryLanguage(string file)
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Localization", file));

        Assert.Contains("x:Key=\"Msg.ExitSavingFinishing\"", xaml);
        var failed = System.Text.RegularExpressions.Regex.Match(xaml, "x:Key=\"Msg.ExitSavingFailed\">([^<]*)<");
        Assert.True(failed.Success);
        Assert.Contains("{0}", failed.Groups[1].Value);
        Assert.Contains("{1}", failed.Groups[1].Value);
    }

    private MainViewModel CreateViewModel(MountManager mountManager)
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        return new MainViewModel(mountManager, store, NullLogger<MainViewModel>.Instance, store.Load().Disks);
    }
}
