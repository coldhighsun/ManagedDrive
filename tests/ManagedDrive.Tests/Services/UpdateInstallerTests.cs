using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Assets;
using GitHubReleaseUpdater.Exceptions;
using GitHubReleaseUpdater.GitHub;
using GitHubReleaseUpdater.GitHub.Models;
using GitHubReleaseUpdater.Installation;
using GitHubReleaseUpdater.Verification;
using ManagedDrive.App.Models;
using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="UpdateInstaller"/> and the update model it works on, using an in-memory GitHub
/// client and a recording launcher so nothing touches the network or starts a process.
/// </summary>
public sealed class UpdateInstallerTests : IDisposable
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
    /// Download directory used by the installer under test, removed after each test.
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
    public async Task DownloadAndLaunchAsync_UpdateAvailable_PreparesAfterDownloadThenLaunchesSilently()
    {
        var launcher = new RecordingLauncher();
        var prepared = new List<bool>();
        var (installer, info) = await CreateAsync(launcher, prepare: () =>
        {
            prepared.Add(File.Exists(Path.Combine(_dir, InstallerName)));
            return null;
        });

        using (installer)
        {
            var launched = await installer.DownloadAndLaunchAsync(info, progress: null, CancellationToken.None);

            Assert.True(launched);
        }

        Assert.Equal([true], prepared);
        var (path, options) = Assert.Single(launcher.Launched);
        Assert.Equal(Path.Combine(_dir, InstallerName), path);
        Assert.Equal("/SILENT /SP- /NORESTART", options.Arguments);
        Assert.Equal(InstallerBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_PrepareThrows_DoesNotLaunch()
    {
        var launcher = new RecordingLauncher();
        var (installer, info) = await CreateAsync(launcher, prepare: () => throw new InvalidOperationException("TEMP not reset"));

        using (installer)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.DownloadAndLaunchAsync(info, null, CancellationToken.None));

            Assert.Equal("TEMP not reset", ex.Message);
        }

        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_UserDeclinesElevation_ReturnsFalse()
    {
        var launcher = new RecordingLauncher { Failure = new InstallerLaunchException("declined", "x.exe", isUserCancelled: true) };
        var (installer, info) = await CreateAsync(launcher);

        using (installer)
        {
            var launched = await installer.DownloadAndLaunchAsync(info, null, CancellationToken.None);

            Assert.False(launched);
        }
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_UserDeclinesElevation_RunsUndo()
    {
        var undone = 0;
        var launcher = new RecordingLauncher { Failure = new InstallerLaunchException("declined", "x.exe", isUserCancelled: true) };
        var (installer, info) = await CreateAsync(launcher, prepare: () => () => undone++);

        using (installer)
        {
            await installer.DownloadAndLaunchAsync(info, null, CancellationToken.None);
        }

        Assert.Equal(1, undone);
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_LaunchFailsForOtherReason_RunsUndoAndPropagates()
    {
        var undone = 0;
        var launcher = new RecordingLauncher { Failure = new InstallerLaunchException("boom", "x.exe", isUserCancelled: false) };
        var (installer, info) = await CreateAsync(launcher, prepare: () => () => undone++);

        using (installer)
        {
            await Assert.ThrowsAsync<InstallerLaunchException>(() => installer.DownloadAndLaunchAsync(info, null, CancellationToken.None));
        }

        Assert.Equal(1, undone);
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_InstallerStarts_DoesNotRunUndo()
    {
        var undone = 0;
        var (installer, info) = await CreateAsync(new RecordingLauncher(), prepare: () => () => undone++);

        using (installer)
        {
            await installer.DownloadAndLaunchAsync(info, null, CancellationToken.None);
        }

        Assert.Equal(0, undone);
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_FileSwappedBeforeLaunch_ThrowsAndRunsUndo()
    {
        var undone = 0;
        var launcher = new RecordingLauncher();
        var (installer, info) = await CreateAsync(launcher, prepare: () =>
        {
            File.WriteAllBytes(Path.Combine(_dir, InstallerName), [0xBA, 0xD]);
            return () => undone++;
        });

        using (installer)
        {
            await Assert.ThrowsAsync<GitHubReleaseUpdater.Exceptions.ChecksumMismatchException>(() => installer.DownloadAndLaunchAsync(info, null, CancellationToken.None));
        }

        Assert.Empty(launcher.Launched);
        Assert.Equal(1, undone);
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_LaunchFailsForOtherReason_Propagates()
    {
        var launcher = new RecordingLauncher { Failure = new InstallerLaunchException("boom", "x.exe", isUserCancelled: false) };
        var (installer, info) = await CreateAsync(launcher);

        using (installer)
        {
            await Assert.ThrowsAsync<InstallerLaunchException>(() => installer.DownloadAndLaunchAsync(info, null, CancellationToken.None));
        }
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_CancelledBeforeDownload_ThrowsAndDoesNotLaunch()
    {
        var launcher = new RecordingLauncher();
        var (installer, info) = await CreateAsync(launcher);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        using (installer)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.DownloadAndLaunchAsync(info, null, cts.Token));
        }

        Assert.Empty(launcher.Launched);
    }

    [Fact]
    public async Task DownloadAndLaunchAsync_OlderInstallersPresent_RemovesThemButKeepsPartialDownloads()
    {
        Directory.CreateDirectory(_dir);
        var oldInstaller = Path.Combine(_dir, "ManagedDrive-Setup-v1.0.0.exe");
        var partial = Path.Combine(_dir, "ManagedDrive-Setup-v3.0.0.exe.partial");
        await File.WriteAllBytesAsync(oldInstaller, [9], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(partial, [9], TestContext.Current.CancellationToken);
        var (installer, info) = await CreateAsync(new RecordingLauncher());

        using (installer)
        {
            await installer.DownloadAndLaunchAsync(info, null, CancellationToken.None);
        }

        Assert.False(File.Exists(oldInstaller));
        Assert.True(File.Exists(partial));
        Assert.True(File.Exists(Path.Combine(_dir, InstallerName)));
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
    public async Task CanInstall_DependsOnInstallerAssetBeingSelected(bool hasInstaller, bool expected)
    {
        var assets = hasInstaller ? new[] { InstallerName, PortableName } : [PortableName];
        var (installer, info) = await CreateAsync(new RecordingLauncher(), assets);

        using (installer)
        {
            Assert.Equal(expected, info.CanInstall);
        }
    }

    /// <summary>
    /// Builds an <see cref="UpdateInstaller"/> and the <see cref="UpdateInfo"/> for a release 2.0.0 that
    /// is newer than the 1.0.0 the fake updater reports as running.
    /// </summary>
    /// <param name="launcher">Receives the launch instead of starting a process.</param>
    /// <param name="assetNames">Assets on the release; defaults to the installer and the portable zip.</param>
    /// <param name="prepare">The before-install callback; defaults to a no-op.</param>
    private async Task<(UpdateInstaller Installer, UpdateInfo Info)> CreateAsync(RecordingLauncher launcher, string[]? assetNames = null, Func<Action?>? prepare = null)
    {
        var client = new FakeClient(Release(assetNames ?? [InstallerName, PortableName]));
        client.AssetBytes[InstallerName] = InstallerBytes;
        var updater = new ReleaseUpdater(new UpdaterOptions
        {
            Owner = "o",
            Repo = "r",
            CurrentVersion = "1.0.0",
            AssetSelector = new PatternAssetSelector(UpdateCheckService.InstallerAssetPattern),
            ChecksumProvider = NoChecksumProvider.Instance,
            InstallerLauncher = launcher,
        }, client);

        var check = await updater.CheckForUpdateAsync();
        Assert.True(check.IsUpdateAvailable);
        var info = new UpdateInfo("2.0.0", new("https://example.test/release"), "notes", check.SelectedAsset?.Size, check);
        return (new UpdateInstaller(updater, _dir, prepare ?? (() => null)), info);
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

        public Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitHubRelease?>(latest);

        public Task<IReadOnlyList<GitHubRelease>> ListReleasesAsync(string owner, string repo, int perPage = 30, int page = 1, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GitHubRelease>>([latest]);

        public Task<GitHubRelease?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken cancellationToken = default) =>
            Task.FromResult<GitHubRelease?>(latest);

        public Task<AssetStream> OpenAssetStreamAsync(GitHubAsset asset, CancellationToken cancellationToken = default)
        {
            var bytes = AssetBytes[asset.Name];
            return Task.FromResult(new AssetStream(new MemoryStream(bytes), bytes.Length, new MemoryStream()));
        }

        public Task<string> ReadAssetTextAsync(GitHubAsset asset, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
