using ManagedDrive.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace ManagedDrive.Tests;

public sealed class SessionEndingSaveHandlerTests
{
    private static SessionEndingSaveHandler CreateHandler(Action? beforeSave) => new(
        new MountManager(),
        () => IntPtr.Zero,
        NullLogger<SessionEndingSaveHandler>.Instance,
        beforeSave);

    private static readonly SessionEndingEventArgs Args = new(SessionEndReasons.SystemShutdown);

    [Fact]
    public void OnSessionEnding_RunsTheBeforeSaveStepOnce()
    {
        var calls = 0;

        CreateHandler(() => calls++).OnSessionEnding(this, Args);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void OnSessionEnding_BeforeSaveThrows_DoesNotPropagate()
    {
        var handler = CreateHandler(() => throw new InvalidOperationException("registry unavailable"));

        var exception = Record.Exception(() => handler.OnSessionEnding(this, Args));

        Assert.Null(exception);
    }

    [Fact]
    public void OnSessionEnding_WithoutABeforeSaveStep_Completes()
    {
        var exception = Record.Exception(() => CreateHandler(null).OnSessionEnding(this, Args));

        Assert.Null(exception);
    }
}

public sealed class TempDirCompatCheckerMountPointTests
{
    [Theory]
    [InlineData(@"R:\Temp", true)]
    [InlineData(@"r:\", true)]
    [InlineData(@"C:\mnt\ramdisk\Temp", true)]
    [InlineData(@"C:\mnt\ramdisk2\Temp", false)]
    [InlineData(@"C:\Users\USER\AppData\Local\Temp", false)]
    [InlineData(@"S:\Temp", false)]
    public void IsPathOnAnyMountPoint_Path_MatchesDriveLettersAndDirectoryMountPoints(string path, bool expected)
    {
        var result = TempDirCompatChecker.IsPathOnAnyMountPoint(path, ["R:", @"C:\mnt\ramdisk"]);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsPathOnAnyMountPoint_NoMountPoints_ReturnsFalse()
    {
        Assert.False(TempDirCompatChecker.IsPathOnAnyMountPoint(@"R:\Temp", []));
    }
}
