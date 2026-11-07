using System.Net.Http;
using System.Security.Cryptography;

namespace ManagedDrive.App.Services;

/// <summary>
/// Outcome of <see cref="WinFspInstaller.InstallAsync"/>.
/// </summary>
public enum WinFspInstallResult
{
    /// <summary>The required WinFsp version is installed.</summary>
    Success,

    /// <summary>The install succeeded but Windows needs a restart before WinFsp is usable.</summary>
    RebootRequired,

    /// <summary>The user declined the elevation (UAC) prompt; nothing changed.</summary>
    Declined,

    /// <summary>Download, verification or installation failed; see <see cref="WinFspInstaller.LastError"/>.</summary>
    Failed,
}

/// <summary>
/// Downloads the required WinFsp installer, verifies its SHA256 and runs it silently with elevation.
/// The download and the process start are injected so the policy (verify before running, map exit codes,
/// never execute a file whose hash differs) is testable without network or UAC.
/// </summary>
/// <param name="downloadDirectory">User-private directory the MSI is stored in (never <c>%TEMP%</c>, which may be on a RAM disk).</param>
/// <param name="download">Downloads <see cref="WinFspPrerequisite.MsiDownloadUrl"/> to the given path.</param>
/// <param name="runMsi">Runs the MSI elevated and silently and returns the msiexec exit code; throws <see cref="System.ComponentModel.Win32Exception"/> with code 1223 when UAC is declined.</param>
/// <param name="verifyInstalled">Reads the installed WinFsp state after the install.</param>
/// <param name="bundledMsiPath">MSI shipped with the app (portable ZIP); copied and verified before any download is attempted.</param>
/// <param name="expectedSha256">Hex SHA256 the MSI must have; defaults to <see cref="WinFspPrerequisite.MsiSha256"/> (overridden only by tests).</param>
public sealed class WinFspInstaller(
    string downloadDirectory,
    Func<string, CancellationToken, Task> download,
    Func<string, CancellationToken, Task<int>> runMsi,
    Func<WinFspCheckResult> verifyInstalled,
    string? bundledMsiPath = null,
    string expectedSha256 = WinFspPrerequisite.MsiSha256)
{
    private const int ErrorCancelled = 1223;
    private const int ExitSuccess = 0;
    private const int ExitRebootRequired = 3010;
    private const int ExitRebootInitiated = 1641;
    private static readonly TimeSpan MsiTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Upper bound for the downloaded installer; the real MSI is a few MB, so anything larger is rejected before the hash check.
    /// </summary>
    private const long MaxDownloadBytes = 64L * 1024 * 1024;

    /// <summary>
    /// A short description of why the last <see cref="InstallAsync"/> returned <see cref="WinFspInstallResult.Failed"/>.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Creates the installer used by the app: downloads over HTTPS and starts <c>msiexec</c> through the UAC prompt.
    /// </summary>
    public static WinFspInstaller CreateDefault() => new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ManagedDrive", "updates"),
        DownloadFileAsync,
        RunMsiElevatedAsync,
        WinFspPrerequisite.Check,
        Path.Combine(AppContext.BaseDirectory, "prerequisites", WinFspPrerequisite.MsiFileName));

    /// <summary>
    /// Fetches, verifies and installs the required WinFsp version.
    /// </summary>
    /// <param name="cancellationToken">Cancels the download or the wait for the installer.</param>
    public async Task<WinFspInstallResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        LastError = null;
        var msiPath = Path.Combine(downloadDirectory, WinFspPrerequisite.MsiFileName);

        try
        {
            Directory.CreateDirectory(downloadDirectory);

            // Every candidate is hashed through a handle that stays open (read-shared, so nobody can write
            // to or delete the file) until msiexec has finished: a same-user process cannot swap the file
            // between the check and the elevated run. The copy in the user-private directory is what runs,
            // so a file in a writable app folder is covered too.
            var handle = await TryOpenVerifiedAsync(msiPath, cancellationToken);
            if (handle is null && bundledMsiPath is not null && File.Exists(bundledMsiPath))
            {
                File.Copy(bundledMsiPath, msiPath, overwrite: true);

                // A copy of a read-only source stays read-only, which would block the next overwrite or delete.
                File.SetAttributes(msiPath, FileAttributes.Normal);
                handle = await TryOpenVerifiedAsync(msiPath, cancellationToken);
            }

            if (handle is null)
            {
                await download(msiPath, cancellationToken);
                handle = await TryOpenVerifiedAsync(msiPath, cancellationToken);
            }

            if (handle is null)
            {
                TryDelete(msiPath);
                LastError = "The downloaded WinFsp installer failed the SHA256 check.";
                return WinFspInstallResult.Failed;
            }

            int exitCode;
            await using (handle)
            {
                try
                {
                    exitCode = await runMsi(msiPath, cancellationToken);
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
                {
                    return WinFspInstallResult.Declined;
                }
            }

            switch (exitCode)
            {
                case ExitSuccess:
                    break;
                case ExitRebootRequired or ExitRebootInitiated:
                    return WinFspInstallResult.RebootRequired;
                default:
                    LastError = $"The WinFsp installer exited with code {exitCode}.";
                    return WinFspInstallResult.Failed;
            }

            if (verifyInstalled().Status != WinFspStatus.Ok)
            {
                LastError = "WinFsp is still not at the required version after the install.";
                return WinFspInstallResult.Failed;
            }

            return WinFspInstallResult.Success;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or InvalidOperationException
                                       or TimeoutException
                                       || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // An OperationCanceledException not caused by our own token is an HttpClient timeout.
            LastError = ex.Message;
            return WinFspInstallResult.Failed;
        }
        finally
        {
            TryDelete(msiPath + ".part");
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> read-shared and returns the open handle when its SHA256 matches; a missing or
    /// different file yields <see langword="null"/> (and is deleted). Keep the handle open to keep the file unchangeable.
    /// </summary>
    private async Task<FileStream?> TryOpenVerifiedAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            if (string.Equals(Convert.ToHexString(hash), expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return stream;
            }
        }
        catch
        {
            // Never leave the file locked behind an exception (read error, cancellation).
            await stream.DisposeAsync();
            throw;
        }

        await stream.DisposeAsync();
        TryDelete(path);
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the hash check runs again before the file is ever executed.
        }
    }

    private static async Task DownloadFileAsync(string destination, CancellationToken cancellationToken)
    {
        // Written beside the target and moved into place, so an interrupted download never leaves a
        // truncated file under the final name.
        var partial = destination + ".part";
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        await using (var source = await client.GetStreamAsync(WinFspPrerequisite.MsiDownloadUrl, cancellationToken))
        await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaxDownloadBytes)
                {
                    throw new IOException("The WinFsp download is larger than expected.");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }

        File.Move(partial, destination, overwrite: true);
    }

    /// <summary>
    /// Runs <paramref name="wait"/> and converts running past <paramref name="timeout"/> into a
    /// <see cref="TimeoutException"/>; cancelling <paramref name="cancellationToken"/> still throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task WaitWithTimeoutAsync(
        Func<CancellationToken, Task> wait, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            await wait(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The WinFsp installer did not finish in time.");
        }
    }

    private static async Task<int> RunMsiElevatedAsync(string msiPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("msiexec.exe", $"/i \"{msiPath}\" /qn /norestart")
        {
            UseShellExecute = true,
            Verb = "runas",
        };

        using var process = Process.Start(startInfo)
                            ?? throw new IOException("Could not start msiexec.exe.");
        await WaitWithTimeoutAsync(process.WaitForExitAsync, MsiTimeout, cancellationToken);
        return process.ExitCode;
    }
}
