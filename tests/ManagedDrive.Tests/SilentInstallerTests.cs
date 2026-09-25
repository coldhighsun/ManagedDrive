using ManagedDrive.WingetExtension;
using YamlDotNet.Core;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for how <see cref="SilentInstaller"/> classifies the exceptions it handles.
/// </summary>
public sealed class SilentInstallerTests
{
    /// <summary>
    /// An installer type with no known default switches means the installer can't be run
    /// directly, so the request falls back to a plain <c>winget install</c>; every other failure
    /// doesn't.
    /// </summary>
    /// <param name="exceptionType">The exception type thrown.</param>
    /// <param name="expected">Whether to fall back.</param>
    [Theory]
    [InlineData(typeof(NotSupportedException), true)]
    [InlineData(typeof(InvalidOperationException), false)]
    [InlineData(typeof(IOException), false)]
    public void ShouldFallBack_ExceptionType_FallsBackOnlyForUnsupportedInstaller(Type exceptionType, bool expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "test")!;

        var shouldFallBack = SilentInstaller.ShouldFallBack(exception);

        Assert.Equal(expected, shouldFallBack);
    }

    /// <summary>
    /// An unusable or unreadable manifest or download directory is reported as a failure rather
    /// than crashing the process.
    /// </summary>
    /// <param name="exceptionType">The exception type thrown.</param>
    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(YamlException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void IsInstallFailure_ManifestOrFileSystemError_ReturnsTrue(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "test")!;

        var isInstallFailure = SilentInstaller.IsInstallFailure(exception);

        Assert.True(isInstallFailure);
    }

    /// <summary>
    /// A fallback case isn't also counted as a failure, and an unexpected exception (a bug) is
    /// left to crash the process.
    /// </summary>
    /// <param name="exceptionType">The exception type thrown.</param>
    [Theory]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(NullReferenceException))]
    public void IsInstallFailure_OtherException_ReturnsFalse(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "test")!;

        var isInstallFailure = SilentInstaller.IsInstallFailure(exception);

        Assert.False(isInstallFailure);
    }
}
