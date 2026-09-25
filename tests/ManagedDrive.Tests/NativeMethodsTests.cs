using System.Security.Claims;
using System.Security.Principal;
using ManagedDrive.Service;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for how the helper service decodes <c>QueryDosDevice</c> results.
/// </summary>
public sealed class NativeMethodsTests
{
    /// <summary>
    /// Win32 <c>ERROR_FILE_NOT_FOUND</c>: the name has no definition.
    /// </summary>
    private const int ErrorFileNotFound = 2;

    /// <summary>
    /// Win32 <c>ERROR_ACCESS_DENIED</c>, standing in for any unexpected failure.
    /// </summary>
    private const int ErrorAccessDenied = 5;

    /// <summary>
    /// The null-separated targets are returned in order, current definition first.
    /// </summary>
    [Fact]
    public void ParseQueryDosDeviceResult_StackedDefinitions_ReturnsEveryTarget()
    {
        var buffer = new char[64];
        const string raw = "\\Device\\A\0\\Device\\B\0\0";
        raw.CopyTo(0, buffer, 0, raw.Length);

        var targets = NativeMethods.ParseQueryDosDeviceResult(buffer, (uint)raw.Length, lastError: 0);

        Assert.Equal(["\\Device\\A", "\\Device\\B"], targets);
    }

    /// <summary>
    /// A name that doesn't exist is reported as having no definitions.
    /// </summary>
    [Fact]
    public void ParseQueryDosDeviceResult_NotFound_ReturnsEmpty()
    {
        var targets = NativeMethods.ParseQueryDosDeviceResult(new char[16], 0, ErrorFileNotFound);

        Assert.NotNull(targets);
        Assert.Empty(targets);
    }

    /// <summary>
    /// Any other failure is unknown rather than "undefined", so callers can fail closed.
    /// </summary>
    [Fact]
    public void ParseQueryDosDeviceResult_OtherError_ReturnsNull()
    {
        var targets = NativeMethods.ParseQueryDosDeviceResult(new char[16], 0, ErrorAccessDenied);

        Assert.Null(targets);
    }

    /// <summary>
    /// Win32 <c>ERROR_PATH_NOT_FOUND</c>: part of the path (here: the device) doesn't exist.
    /// </summary>
    private const int ErrorPathNotFound = 3;

    /// <summary>
    /// "File not found" and "path not found" both mean the device is gone.
    /// </summary>
    [Fact]
    public void IsDeviceNotFoundError_FileOrPathNotFound_ReturnsTrue()
    {
        Assert.True(NativeMethods.IsDeviceNotFoundError(ErrorFileNotFound));
        Assert.True(NativeMethods.IsDeviceNotFoundError(ErrorPathNotFound));
    }

    /// <summary>
    /// Any other failure (e.g. access denied, a busy file system) does not mean the device is
    /// gone: <see cref="NativeMethods.DeviceExists"/> must not treat it as absent, or a live
    /// device that merely failed to open would have its drive letter reclaimed.
    /// </summary>
    [Fact]
    public void IsDeviceNotFoundError_OtherError_ReturnsFalse()
    {
        Assert.False(NativeMethods.IsDeviceNotFoundError(ErrorAccessDenied));
    }

    /// <summary>
    /// Deny-only groups (e.g. Administrators in an unelevated administrator's token) are included
    /// alongside enabled ones, and the user's own SID is not mistaken for a group.
    /// </summary>
    [Fact]
    public void GetGroupSids_CurrentIdentity_ReturnsEnabledAndDenyOnlyGroups()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var denyOnly = identity.Claims.Where(c => c.Type == ClaimTypes.DenyOnlySid).Select(c => c.Value).ToList();

        var groups = NativeMethods.GetGroupSids(identity);

        Assert.Contains("S-1-1-0", groups);
        Assert.All(denyOnly, sid => Assert.Contains(sid, groups));
        Assert.DoesNotContain(identity.User!.Value, groups);
    }
}
