using System.ComponentModel;
using System.Security.Cryptography;
using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="WinFspInstaller"/>'s policy: verify before running, map exit codes, never execute a
/// file whose hash differs.
/// </summary>
public sealed class WinFspInstallerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wfi-" + Guid.NewGuid().ToString("N"));

    private int _downloads;
    private int _runs;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>
    /// A download whose hash differs is rejected, deleted and never run.
    /// </summary>
    [Fact]
    public async Task InstallAsync_HashMismatch_FailsWithoutRunning()
    {
        var installer = Create(writeValidFile: false, exitCode: 0);

        var result = await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.Equal(WinFspInstallResult.Failed, result);
        Assert.Equal(1, _downloads);
        Assert.Equal(0, _runs);
        Assert.False(File.Exists(Path.Combine(_dir, WinFspPrerequisite.MsiFileName)));
    }

    /// <summary>
    /// Exit codes map to results.
    /// </summary>
    [Theory]
    [InlineData(0, WinFspInstallResult.Success)]
    [InlineData(3010, WinFspInstallResult.RebootRequired)]
    [InlineData(1603, WinFspInstallResult.Failed)]
    public async Task InstallAsync_MapsExitCode(int exitCode, WinFspInstallResult expected)
    {
        var installer = Create(writeValidFile: true, exitCode);

        Assert.Equal(expected, await installer.InstallAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Declining UAC yields <see cref="WinFspInstallResult.Declined"/>.
    /// </summary>
    [Fact]
    public async Task InstallAsync_UacDeclined_ReturnsDeclined()
    {
        var installer = Create(writeValidFile: true, exitCode: 0, throwUac: true);

        Assert.Equal(WinFspInstallResult.Declined, await installer.InstallAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A cached file with the right hash is reused without downloading again.
    /// </summary>
    [Fact]
    public async Task InstallAsync_CachedValidFile_SkipsDownload()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, WinFspPrerequisite.MsiFileName), ValidBytes, TestContext.Current.CancellationToken);
        var installer = Create(writeValidFile: true, exitCode: 0);

        Assert.Equal(WinFspInstallResult.Success, await installer.InstallAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, _downloads);
        Assert.Equal(1, _runs);
    }

    /// <summary>
    /// A valid MSI shipped with the app is used without downloading.
    /// </summary>
    [Fact]
    public async Task InstallAsync_BundledValidFile_SkipsDownload()
    {
        Directory.CreateDirectory(_dir);
        var bundled = Path.Combine(_dir, "bundled.msi");
        await File.WriteAllBytesAsync(bundled, ValidBytes, TestContext.Current.CancellationToken);
        var installer = Create(writeValidFile: true, exitCode: 0, bundled: bundled);

        Assert.Equal(WinFspInstallResult.Success, await installer.InstallAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, _downloads);
    }

    /// <summary>
    /// A tampered bundled MSI is ignored and the download is used instead.
    /// </summary>
    [Fact]
    public async Task InstallAsync_BundledBadFile_FallsBackToDownload()
    {
        Directory.CreateDirectory(_dir);
        var bundled = Path.Combine(_dir, "bundled.msi");
        await File.WriteAllBytesAsync(bundled, [7, 7], TestContext.Current.CancellationToken);
        var installer = Create(writeValidFile: true, exitCode: 0, bundled: bundled);

        Assert.Equal(WinFspInstallResult.Success, await installer.InstallAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, _downloads);
    }

    /// <summary>
    /// A timeout raised by the download (not by the caller's token) is a failure, not an exception.
    /// </summary>
    [Fact]
    public async Task InstallAsync_DownloadTimeout_Fails()
    {
        var installer = new WinFspInstaller(
            _dir,
            (_, _) => throw new TaskCanceledException("timeout"),
            (_, _) => Task.FromResult(0),
            () => new(WinFspStatus.Ok, WinFspPrerequisite.RequiredVersion));

        Assert.Equal(WinFspInstallResult.Failed, await installer.InstallAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A successful msiexec run that leaves the wrong version behind is a failure.
    /// </summary>
    [Fact]
    public async Task InstallAsync_StillWrongVersionAfterInstall_Fails()
    {
        var installer = new WinFspInstaller(
            _dir,
            async (path, ct) => await File.WriteAllBytesAsync(path, ValidBytes, ct),
            (_, _) => Task.FromResult(0),
            () => new(WinFspStatus.VersionMismatch, new Version(2, 2, 1)),
            expectedSha256: Convert.ToHexString(SHA256.HashData(ValidBytes)));

        Assert.Equal(WinFspInstallResult.Failed, await installer.InstallAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Exit code 1641 (reboot initiated) maps to <see cref="WinFspInstallResult.RebootRequired"/>.
    /// </summary>
    [Fact]
    public async Task InstallAsync_RebootInitiated_RequiresReboot() =>
        Assert.Equal(
            WinFspInstallResult.RebootRequired,
            await Create(writeValidFile: true, exitCode: 1641).InstallAsync(TestContext.Current.CancellationToken));

    /// <summary>
    /// The verified MSI cannot be modified while the installer runs (no swap between hash check and execution).
    /// </summary>
    [Fact]
    public async Task InstallAsync_FileIsLockedAgainstWritesWhileInstalling()
    {
        var msiPath = Path.Combine(_dir, WinFspPrerequisite.MsiFileName);
        var writeBlocked = false;
        var installer = new WinFspInstaller(
            _dir,
            async (path, ct) => await File.WriteAllBytesAsync(path, ValidBytes, ct),
            (path, _) =>
            {
                try
                {
                    using var probe = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                }
                catch (IOException)
                {
                    writeBlocked = true;
                }

                return Task.FromResult(0);
            },
            () => new(WinFspStatus.Ok, WinFspPrerequisite.RequiredVersion),
            expectedSha256: Convert.ToHexString(SHA256.HashData(ValidBytes)));

        await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.True(writeBlocked);
        Assert.True(File.Exists(msiPath));
    }

    /// <summary>
    /// A leftover partial download is cleaned up.
    /// </summary>
    [Fact]
    public async Task InstallAsync_PartialFileIsRemoved()
    {
        Directory.CreateDirectory(_dir);
        var part = Path.Combine(_dir, WinFspPrerequisite.MsiFileName + ".part");
        await File.WriteAllBytesAsync(part, [1], TestContext.Current.CancellationToken);
        var installer = Create(writeValidFile: true, exitCode: 0);

        await installer.InstallAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(part));
    }

    /// <summary>
    /// A wait that outlives the timeout becomes a <see cref="TimeoutException"/>; the caller's own cancellation is not swallowed.
    /// </summary>
    [Fact]
    public async Task WaitWithTimeoutAsync_TimeoutAndCancellation()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => WinFspInstaller.WaitWithTimeoutAsync(
            ct => Task.Delay(Timeout.Infinite, ct), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WinFspInstaller.WaitWithTimeoutAsync(
            ct => Task.Delay(Timeout.Infinite, ct), TimeSpan.FromSeconds(30), cts.Token));
    }

    private WinFspInstaller Create(bool writeValidFile, int exitCode, bool throwUac = false, string? bundled = null) => new(
        _dir,
        async (path, ct) =>
        {
            _downloads++;
            await File.WriteAllBytesAsync(path, writeValidFile ? ValidBytes : [9, 9, 9], ct);
        },
        (_, _) =>
        {
            _runs++;
            return throwUac ? throw new Win32Exception(1223) : Task.FromResult(exitCode);
        },
        () => new(WinFspStatus.Ok, WinFspPrerequisite.RequiredVersion),
        bundled,
        Convert.ToHexString(SHA256.HashData(ValidBytes)));

    private static byte[] ValidBytes => [1, 2, 3];
}
