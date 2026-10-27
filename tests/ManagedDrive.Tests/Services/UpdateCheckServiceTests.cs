using System.Security.Cryptography;
using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.AutoUpdate;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.GitHub;
using GitHubReleaseUpdater.GitHub.Models;
using GitHubReleaseUpdater.Installation;
using GitHubReleaseUpdater.Verification;
using GitHubReleaseUpdater.Versioning;
using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for the installation flow of <see cref="UpdateCheckService"/> on top of GitHubReleaseUpdater's
/// controller, using an in-memory GitHub client and a recording launcher so nothing touches the network or
/// starts a process.
/// </summary>
public sealed class UpdateCheckServiceTests : IDisposable
{
    /// <summary>
    /// Name of the installer asset on the fake release.
    /// </summary>
    private const string InstallerName = "ManagedDrive-Setup-v2.0.0.exe";

    /// <summary>
    /// Name of the portable zip asset on the fake release.
    /// </summary>
    private const string PortableName = "ManagedDrive-v2.0.0-win-x64-portable.zip";

    /// <summary>
    /// Download directory used by the service under test, removed after each test.
    /// </summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "md-update-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Content served for <see cref="InstallerName"/>.
    /// </summary>
    private static readonly byte[] InstallerBytes = [1, 2, 3, 4, 5];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_UpdateAvailable_PreparesAfterDownloadThenLaunchesSilently()
    {
        var launcher = new RecordingLauncher();
        var prepared = new List<bool>();
        using var service = await CreateAsync(launcher, prepare: () =>
        {
            prepared.Add(File.Exists(Path.Combine(_dir, InstallerName)));
            return null;
        });

        var launched = await service.InstallAsync(CancellationToken.None);

        Assert.True(launched);
        Assert.Equal([true], prepared);
        var (path, options) = Assert.Single(launcher.Launched);
        Assert.Equal(Path.Combine(_dir, InstallerName), path);
        Assert.Equal("/SILENT /SP- /NORESTART", options.Arguments);
        Assert.Equal(InstallerBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallAsync_PrepareThrows_ReportsFailureAndDoesNotLaunch()
    {
        var launcher = new RecordingLauncher();
        using var service = await CreateAsync(launcher, prepare: () => throw new InvalidOperationException("TEMP not reset"));

        var launched = await service.InstallAsync(CancellationToken.None);

        Assert.False(launched);
        Assert.Equal("TEMP not reset", service.LastInstallError?.Message);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task InstallAsync_UserDeclinesElevation_ReturnsFalseWithoutFailureAndRunsUndo()
    {
        var undone = 0;
        var launcher = new RecordingLauncher { Failure = new InstallerLaunchException("declined", "x.exe", isUserCancelled: true) };
        using var service = await CreateAsync(launcher, prepare: () => () => undone++);

        var launched = await service.InstallAsync(CancellationToken.None);

        Assert.False(launched);
        Assert.Null(service.LastInstallError);
        Assert.Equal(1, undone);
    }

    [Fact]
    public async Task InstallAsync_LaunchFailsForOtherReason_ReportsFailureAndRunsUndo()
    {
        var undone = 0;
        var launcher = new RecordingLauncher { Failure = new InstallerLaunchException("boom", "x.exe", isUserCancelled: false) };
        using var service = await CreateAsync(launcher, prepare: () => () => undone++);

        var launched = await service.InstallAsync(CancellationToken.None);

        Assert.False(launched);
        Assert.IsType<InstallerLaunchException>(service.LastInstallError);
        Assert.Equal(1, undone);
    }

    [Fact]
    public async Task InstallAsync_InstallerStarts_DoesNotRunUndo()
    {
        var undone = 0;
        using var service = await CreateAsync(new RecordingLauncher(), prepare: () => () => undone++);

        await service.InstallAsync(CancellationToken.None);

        Assert.Equal(0, undone);
    }

    [Fact]
    public async Task InstallAsync_ChecksumMismatch_ReportsFailureAndDoesNotLaunch()
    {
        var launcher = new RecordingLauncher();
        using var service = await CreateAsync(launcher, checksum: new string('0', 64));

        var launched = await service.InstallAsync(CancellationToken.None);

        Assert.False(launched);
        Assert.IsType<ChecksumMismatchException>(service.LastInstallError);
        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task InstallAsync_CancelledBeforeDownload_ThrowsAndLeavesTempAlone()
    {
        var prepared = 0;
        var launcher = new RecordingLauncher();
        using var service = await CreateAsync(launcher, prepare: () =>
        {
            prepared++;
            return null;
        });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InstallAsync(cts.Token));

        Assert.Empty(launcher.Launched);
        Assert.Equal(0, prepared);
    }

    [Fact]
    public async Task InstallAsync_CancelledDuringDownload_ThrowsAndDoesNotLaunchOrTouchTemp()
    {
        var prepared = 0;
        var launcher = new RecordingLauncher();
        using var cts = new CancellationTokenSource();
        using var service = await CreateAsync(launcher, prepare: () =>
        {
            prepared++;
            return null;
        }, onDownload: cts.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InstallAsync(cts.Token));

        Assert.Empty(launcher.Launched);
        Assert.Equal(0, prepared);
        Assert.Null(service.LastInstallError);
    }

    [Fact]
    public async Task InstallAsync_AlreadyRunning_Throws()
    {
        using var gate = new SemaphoreSlim(0);
        using var service = await CreateAsync(new RecordingLauncher(), onDownload: () => gate.Wait(TestContext.Current.CancellationToken));
        var first = Task.Run(() => service.InstallAsync(CancellationToken.None), TestContext.Current.CancellationToken);
        await SpinWait(() => service.Controller.State == UpdateState.Installing);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InstallAsync(CancellationToken.None));

        gate.Release();
        await first;
    }

    [Fact]
    public async Task InstallAsync_TempNotOnRamDisk_SkipsPreparation()
    {
        var prepared = 0;
        var launcher = new RecordingLauncher();
        using var service = await CreateAsync(launcher, prepare: () =>
        {
            prepared++;
            return null;
        }, tempOnRamDisk: false);

        var launched = await service.InstallAsync(CancellationToken.None);

        Assert.True(launched);
        Assert.Equal(0, prepared);
    }

    [Fact]
    public async Task AutomaticCheck_UpdateFound_NeverStartsTheInstaller()
    {
        var launcher = new RecordingLauncher();
        using var service = await CreateAsync(launcher, check: false);

        await service.Controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.NotNull(service.Controller.Available);
        Assert.Empty(launcher.Launched);
        Assert.False(File.Exists(Path.Combine(_dir, InstallerName)));
    }

    [Fact]
    public async Task AutomaticCheck_UpdateFound_RaisesUpdateAnnouncedOnce()
    {
        using var service = await CreateAsync(new RecordingLauncher(), check: false);
        var announced = new List<string>();
        service.UpdateAnnounced += update => announced.Add(update.Version.ToString());

        await service.Controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);
        await service.Controller.CheckAsync(manual: false, TestContext.Current.CancellationToken);

        Assert.Equal(["2.0.0"], announced);
    }

    [Fact]
    public async Task SettingsLastCheckStore_RoundTripsLastCheckAndSkippedVersion()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsLastCheckStore(new SettingsStore(path));
        var checkedAt = new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero);

        await store.SetLastCheckedAtAsync(checkedAt, TestContext.Current.CancellationToken);
        await store.SetSkippedVersionAsync(SemanticVersion.Parse("2.0.0"), TestContext.Current.CancellationToken);

        Assert.Equal(checkedAt, await store.GetLastCheckedAtAsync(TestContext.Current.CancellationToken));
        Assert.Equal("2.0.0", (await store.GetSkippedVersionAsync(TestContext.Current.CancellationToken))?.ToString());

        await store.ClearSkippedVersionAsync(TestContext.Current.CancellationToken);

        Assert.Null(await store.GetSkippedVersionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InstallAsync_OlderInstallersPresent_RemovesThem()
    {
        Directory.CreateDirectory(_dir);
        var oldInstaller = Path.Combine(_dir, "ManagedDrive-Setup-v1.0.0.exe");
        var stalePartial = Path.Combine(_dir, "ManagedDrive-Setup-v3.0.0.exe.partial");
        await File.WriteAllBytesAsync(oldInstaller, [9], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(stalePartial, [9], TestContext.Current.CancellationToken);
        using var service = await CreateAsync(new RecordingLauncher());

        await service.InstallAsync(CancellationToken.None);

        Assert.False(File.Exists(oldInstaller));
        Assert.False(File.Exists(stalePartial));
        Assert.True(File.Exists(Path.Combine(_dir, InstallerName)));
    }

    [Fact]
    public async Task ManualCheck_UpdateFound_OffersItWithoutInstalling()
    {
        var launcher = new RecordingLauncher();
        using var service = await CreateAsync(launcher);

        Assert.Equal("2.0.0", service.Controller.Available?.Version.ToString());
        Assert.Empty(launcher.Launched);
        Assert.False(File.Exists(Path.Combine(_dir, InstallerName)));
    }

    [Fact]
    public void InstallerAssetPattern_ReleaseWithInstallerAndPortableZip_SelectsInstaller()
    {
        var release = Release(InstallerName, PortableName);
        var selector = new PatternAssetSelector(UpdateCheckService.InstallerAssetPattern);

        var selected = selector.Select(release);

        Assert.Equal(InstallerName, selected?.Name);
    }

    [Fact]
    public void InstallerAssetPattern_ReleaseWithoutInstaller_SelectsNothing()
    {
        var selector = new PatternAssetSelector(UpdateCheckService.InstallerAssetPattern);

        var selected = selector.Select(Release(PortableName));

        Assert.Null(selected);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task HasInstaller_DependsOnInstallerAssetBeingSelected(bool hasInstaller, bool expected)
    {
        var assets = hasInstaller ? new[] { InstallerName, PortableName } : [PortableName];
        using var service = await CreateAsync(new RecordingLauncher(), assets);

        Assert.Equal(expected, service.Controller.HasInstaller);
    }

    /// <summary>
    /// Builds an <see cref="UpdateCheckService"/> whose controller has already found release 2.0.0, which is
    /// newer than the 1.0.0 the fake updater reports as running.
    /// </summary>
    /// <param name="launcher">Receives the launch instead of starting a process.</param>
    /// <param name="assetNames">Assets on the release; defaults to the installer and the portable zip.</param>
    /// <param name="prepare">The before-install callback; defaults to a no-op.</param>
    /// <param name="tempOnRamDisk">Whether TEMP counts as being on a RAM disk, which is when the callback runs.</param>
    /// <param name="checksum">SHA-256 the release claims for the installer; defaults to the real one.</param>
    /// <param name="check">Whether to run a manual check first so <see cref="UpdateController.Available"/> is set.</param>
    /// <param name="onDownload">Runs when the installer download is opened.</param>
    private async Task<UpdateCheckService> CreateAsync(RecordingLauncher launcher, string[]? assetNames = null, Func<Action?>? prepare = null, string? checksum = null, bool check = true, Action? onDownload = null, bool tempOnRamDisk = true)
    {
        var client = new FakeClient(Release(assetNames ?? [InstallerName, PortableName])) { OnOpen = onDownload };
        client.AssetBytes[InstallerName] = InstallerBytes;
        var updater = new ReleaseUpdater(new UpdaterOptions
        {
            Owner = "o",
            Repo = "r",
            CurrentVersion = "1.0.0",
            AssetSelector = new PatternAssetSelector(UpdateCheckService.InstallerAssetPattern),
            ChecksumProvider = new StaticChecksumProvider(checksum ?? Convert.ToHexString(SHA256.HashData(InstallerBytes))),
            InstallerLauncher = launcher,
        }, client);

        var service = new UpdateCheckService(updater, _dir, () => true, () => tempOnRamDisk, () => prepare?.Invoke() ?? (() => { }));
        if (check)
        {
            await service.Controller.CheckAsync(manual: true);
            Assert.NotNull(service.Controller.Available);
        }

        return service;
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> holds, failing after a few seconds.
    /// </summary>
    private static async Task SpinWait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition was not reached in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Builds a 2.0.0 release holding the given assets.
    /// </summary>
    private static GitHubRelease Release(params string[] assetNames) => new()
    {
        Id = 1,
        TagName = "v2.0.0",
        HtmlUrl = "https://example.test/release",
        Assets = assetNames.Select((name, index) => new GitHubAsset
        {
            Id = index + 1,
            Name = name,
            Size = InstallerBytes.Length,
            BrowserDownloadUrl = $"https://example.test/{name}",
            ApiUrl = $"https://example.test/api/{name}",
        }).ToArray(),
    };

    /// <summary>
    /// <see cref="IInstallerLauncher"/> that records launches, optionally failing them.
    /// </summary>
    private sealed class RecordingLauncher : IInstallerLauncher
    {
        /// <summary>
        /// Every (path, options) pair launched, in order.
        /// </summary>
        public List<(string Path, InstallerLaunchOptions Options)> Launched { get; } = [];

        /// <summary>
        /// Thrown by <see cref="Launch"/> when set.
        /// </summary>
        public Exception? Failure { get; init; }

        public InstallerLaunchResult Launch(string installerPath, InstallerLaunchOptions options)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Launched.Add((installerPath, options));
            return new InstallerLaunchResult(installerPath, null);
        }
    }

    /// <summary>
    /// In-memory <see cref="IGitHubReleaseClient"/> serving one release and canned asset bytes.
    /// </summary>
    /// <param name="latest">The release returned as the latest one.</param>
    private sealed class FakeClient(GitHubRelease latest) : IGitHubReleaseClient
    {
        /// <summary>
        /// Asset content by asset name.
        /// </summary>
        public Dictionary<string, byte[]> AssetBytes { get; } = [];

        /// <summary>
        /// Runs each time an asset is opened.
        /// </summary>
        public Action? OnOpen { get; init; }

        public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitHubRelease?>(latest);

        public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitHubRelease>>([latest]);

        public Task<GitHubRelease?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitHubRelease?>(latest);

        public Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default)
        {
            OnOpen?.Invoke();
            var bytes = AssetBytes[asset.Name];
            return Task.FromResult(new AssetStream(new MemoryStream(bytes), bytes.Length, new MemoryStream()));
        }

        public Task<string> ReadAssetTextAsync(GitHubAsset asset, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
