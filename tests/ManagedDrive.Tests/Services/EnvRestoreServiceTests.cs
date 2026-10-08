using ManagedDrive.App.Models;
using ManagedDrive.App.Services;
using Microsoft.Win32;

namespace ManagedDrive.Tests;

public sealed class EnvRestoreServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());
    private readonly FakeUserEnvironment _env = new();
    private List<EnvRedirectBackup> _backups = [];
    private EnvRestoreResult _tempResult = EnvRestoreResult.Restored;
    private int _tempRestores;
    private bool _tempOnKnownDisk;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private UserEnvironmentRedirector CreateRedirector() => new(
        _env,
        () => _backups,
        list =>
        {
            _backups = list;
            return true;
        });

    private EnvRestoreService CreateService(UserEnvironmentRedirector redirector) => new(
        redirector,
        () =>
        {
            _tempRestores++;
            return _tempResult;
        },
        () => _tempOnKnownDisk);

    private static void Redirect(UserEnvironmentRedirector redirector, string mountPoint, params string[] variables) =>
        redirector.ApplyDiskEffects(new()
        {
            MountPoint = mountPoint,
            CapacityBytes = 1024 * 1024,
            EnvRedirects = [.. variables.Select(variable => new EnvRedirect { Variable = variable, SubPath = "c" })],
        });

    [Fact]
    public void GetGroups_NothingRedirected_OffersNothing()
    {
        var service = CreateService(CreateRedirector());

        Assert.Empty(service.GetGroups());
    }

    [Fact]
    public void GetGroups_TempOnADiskWithoutBackup_OffersTemp()
    {
        _tempOnKnownDisk = true;
        var service = CreateService(CreateRedirector());

        var group = Assert.Single(service.GetGroups());

        Assert.Equal("temp", group.Id);
    }

    [Fact]
    public void GetGroups_TempHasABackup_DoesNotAskWhetherTempIsOnAKnownDisk()
    {
        var redirector = CreateRedirector();
        Redirect(redirector, Path.Combine(_root, "d"), "TEMP", "TMP");
        var asked = false;
        var service = new EnvRestoreService(redirector, () => EnvRestoreResult.Restored, () => asked = true);

        var group = Assert.Single(service.GetGroups());

        Assert.Equal("temp", group.Id);
        Assert.False(asked);
    }

    [Fact]
    public void GetGroups_NodeCacheRedirected_OffersOnlyNode()
    {
        var redirector = CreateRedirector();
        Redirect(redirector, Path.Combine(_root, "d"), "npm_config_cache");

        var groups = CreateService(redirector).GetGroups();

        Assert.Equal(["node"], groups.Select(g => g.Id));
    }

    [Fact]
    public void Restore_NodeGroup_RestoresOnlyItsVariables()
    {
        var redirector = CreateRedirector();
        Redirect(redirector, Path.Combine(_root, "d"), "npm_config_cache", "PIP_CACHE_DIR");
        var service = CreateService(redirector);
        var node = service.GetGroups().Single(g => g.Id == "node");

        var result = service.Restore(node);

        Assert.Equal(EnvRestoreResult.Restored, result);
        Assert.False(_env.Values.ContainsKey("npm_config_cache"));
        Assert.True(_env.Values.ContainsKey("PIP_CACHE_DIR"));
        Assert.Equal(["PIP_CACHE_DIR"], _backups.Select(b => b.Variable));
        Assert.Equal(0, _tempRestores);
    }

    [Fact]
    public void Restore_TempGroup_GoesThroughTheTempDelegate()
    {
        var redirector = CreateRedirector();
        Redirect(redirector, Path.Combine(_root, "d"), "PIP_CACHE_DIR");
        var service = CreateService(redirector);
        _tempResult = EnvRestoreResult.NothingToRestore;

        var result = service.Restore(new EnvRestoreGroup("temp", ["TEMP", "TMP"]));

        Assert.Equal(EnvRestoreResult.NothingToRestore, result);
        Assert.Equal(1, _tempRestores);
        Assert.True(_env.Values.ContainsKey("PIP_CACHE_DIR"));
    }

    [Fact]
    public void Restore_AllGroups_RestoresEveryGroupAndTemp()
    {
        var redirector = CreateRedirector();
        Redirect(redirector, Path.Combine(_root, "d"), "npm_config_cache", "PIP_CACHE_DIR", "CUSTOM_CACHE");
        var service = CreateService(redirector);

        var result = service.Restore(null);

        Assert.Equal(EnvRestoreResult.Restored, result);
        Assert.Empty(_env.Values);
        Assert.Empty(_backups);
        Assert.Equal(1, _tempRestores);
    }

    [Fact]
    public void Restore_NothingRecordedForTheGroup_ReportsNothingToRestore()
    {
        var service = CreateService(CreateRedirector());
        var node = new EnvRestoreGroup("node", ["npm_config_cache"]);

        var result = service.Restore(node);

        Assert.Equal(EnvRestoreResult.NothingToRestore, result);
    }

    [Theory]
    [InlineData(EnvRestoreResult.Restored, EnvRestoreResult.Failed, EnvRestoreResult.Failed)]
    [InlineData(EnvRestoreResult.NothingToRestore, EnvRestoreResult.Restored, EnvRestoreResult.Restored)]
    [InlineData(EnvRestoreResult.NothingToRestore, EnvRestoreResult.NothingToRestore, EnvRestoreResult.NothingToRestore)]
    public void Combine_MixedResults_FailureWinsThenRestored(EnvRestoreResult first, EnvRestoreResult second, EnvRestoreResult expected)
    {
        var combined = EnvRestoreService.Combine([first, second]);

        Assert.Equal(expected, combined);
    }

    [Fact]
    public void Combine_NoResults_IsNothingToRestore()
    {
        Assert.Equal(EnvRestoreResult.NothingToRestore, EnvRestoreService.Combine([]));
    }
}
