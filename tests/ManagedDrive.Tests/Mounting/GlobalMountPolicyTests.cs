using ManagedDrive.Service;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for the helper service's publish/unpublish authorization rules and registry decoding.
/// </summary>
public sealed class GlobalMountPolicyTests
{
    /// <summary>
    /// The device path the tests publish.
    /// </summary>
    private const string Device = @"\Device\Volume{11111111-2222-3333-4444-555555555555}";

    /// <summary>
    /// A second, different device path.
    /// </summary>
    private const string OtherDevice = @"\Device\Volume{66666666-7777-8888-9999-000000000000}";

    /// <summary>
    /// SID of the requesting user.
    /// </summary>
    private const string Caller = "S-1-5-21-1-2-3-1001";

    /// <summary>
    /// SID of a different user.
    /// </summary>
    private const string OtherUser = "S-1-5-21-1-2-3-1002";

    /// <summary>
    /// A letter defined by something other than the service (e.g. the system drive) must not be
    /// redirected.
    /// </summary>
    [Fact]
    public void DecidePublish_LetterDefinedGloballyButNotRecorded_RejectsLetterInUse()
    {
        var decision = GlobalMountPolicy.DecidePublish(Device, Caller, recorded: null, deviceExists: true, letterTargets: [OtherDevice]);

        Assert.Equal(PublishDecision.RejectLetterInUse, decision);
    }

    /// <summary>
    /// A letter with no definition and no record is published.
    /// </summary>
    [Fact]
    public void DecidePublish_FreeLetter_Creates()
    {
        var decision = GlobalMountPolicy.DecidePublish(Device, Caller, recorded: null, deviceExists: true, letterTargets: []);

        Assert.Equal(PublishDecision.Create, decision);
    }

    /// <summary>
    /// A device that isn't present is rejected before anything else is considered.
    /// </summary>
    [Fact]
    public void DecidePublish_DeviceMissing_RejectsDeviceMissing()
    {
        var decision = GlobalMountPolicy.DecidePublish(Device, Caller, recorded: null, deviceExists: false, letterTargets: []);

        Assert.Equal(PublishDecision.RejectDeviceMissing, decision);
    }

    /// <summary>
    /// A letter whose current definitions could not be queried fails closed, as if it were in use.
    /// </summary>
    [Fact]
    public void DecidePublish_LetterTargetsUnknown_RejectsLetterInUse()
    {
        var decision = GlobalMountPolicy.DecidePublish(Device, Caller, recorded: null, deviceExists: true, letterTargets: null);

        Assert.Equal(PublishDecision.RejectLetterInUse, decision);
    }

    /// <summary>
    /// A letter another user published can't be taken over, even for the same device.
    /// </summary>
    [Fact]
    public void DecidePublish_PublishedByAnotherUser_RejectsPublishedByOther()
    {
        var recorded = new PublishedMount(Device, OtherUser);

        var decision = GlobalMountPolicy.DecidePublish(Device, Caller, recorded, deviceExists: true, letterTargets: [Device]);

        Assert.Equal(PublishDecision.RejectPublishedByOther, decision);
    }

    /// <summary>
    /// The caller's own letter can't be repointed while its current device is still present.
    /// </summary>
    [Fact]
    public void DecidePublish_CallerPublishedLetterForAnotherLiveDevice_RejectsPublishedByOther()
    {
        var recorded = new PublishedMount(OtherDevice, Caller);

        var decision = GlobalMountPolicy.DecidePublish(Device, Caller, recorded, deviceExists: true, letterTargets: [OtherDevice]);

        Assert.Equal(PublishDecision.RejectPublishedByOther, decision);
    }

    /// <summary>
    /// Re-publishing the caller's existing mapping succeeds without a second definition; the
    /// device path compares case-insensitively.
    /// </summary>
    [Fact]
    public void DecidePublish_SameMappingBySameUser_IsAlreadyPublished()
    {
        var recorded = new PublishedMount(Device.ToUpperInvariant(), Caller);

        var decision = GlobalMountPolicy.DecidePublish(Device, Caller, recorded, deviceExists: true, letterTargets: [Device]);

        Assert.Equal(PublishDecision.AlreadyPublished, decision);
    }

    /// <summary>
    /// A recorded mapping whose current target no longer matches (something else took over the
    /// letter) is rejected as in use, rather than reported as already published.
    /// </summary>
    [Fact]
    public void DecidePublish_RecordedMappingButLetterPointsElsewhere_RejectsLetterInUse()
    {
        var recorded = new PublishedMount(Device, Caller);

        var decision = GlobalMountPolicy.DecidePublish(Device, Caller, recorded, deviceExists: true, letterTargets: [OtherDevice]);

        Assert.Equal(PublishDecision.RejectLetterInUse, decision);
    }

    /// <summary>
    /// A recorded mapping whose definition was removed externally is recreated.
    /// </summary>
    [Fact]
    public void DecidePublish_RecordedMappingWhoseDefinitionWasRemoved_Creates()
    {
        var recorded = new PublishedMount(Device, Caller);

        var decision = GlobalMountPolicy.DecidePublish(Device, Caller, recorded, deviceExists: true, letterTargets: []);

        Assert.Equal(PublishDecision.Create, decision);
    }

    /// <summary>
    /// A record written before owners were stored can be managed by any caller.
    /// </summary>
    [Fact]
    public void DecidePublish_LegacyOwnerlessRecordForSameDevice_IsAlreadyPublished()
    {
        var recorded = new PublishedMount(Device, OwnerSid: null);

        var decision = GlobalMountPolicy.DecidePublish(Device, Caller, recorded, deviceExists: true, letterTargets: [Device]);

        Assert.Equal(PublishDecision.AlreadyPublished, decision);
    }

    /// <summary>
    /// Another user's letter can't be removed.
    /// </summary>
    [Fact]
    public void DecideUnpublish_PublishedByAnotherUser_RejectsNotOwner()
    {
        var decision = GlobalMountPolicy.DecideUnpublish(Caller, new PublishedMount(Device, OtherUser));

        Assert.Equal(UnpublishDecision.RejectNotOwner, decision);
    }

    /// <summary>
    /// The caller's own letter is removed; the SID compares case-insensitively.
    /// </summary>
    [Fact]
    public void DecideUnpublish_PublishedByCaller_Removes()
    {
        var decision = GlobalMountPolicy.DecideUnpublish(Caller, new PublishedMount(Device, Caller.ToLowerInvariant()));

        Assert.Equal(UnpublishDecision.Remove, decision);
    }

    /// <summary>
    /// A record without an owner can be removed by any caller.
    /// </summary>
    [Fact]
    public void DecideUnpublish_LegacyOwnerlessRecord_Removes()
    {
        var decision = GlobalMountPolicy.DecideUnpublish(Caller, new PublishedMount(Device, OwnerSid: null));

        Assert.Equal(UnpublishDecision.Remove, decision);
    }

    /// <summary>
    /// Unpublishing a letter with no record reports that nothing was recorded.
    /// </summary>
    [Fact]
    public void DecideUnpublish_NothingRecorded_ReturnsNothingRecorded()
    {
        var decision = GlobalMountPolicy.DecideUnpublish(Caller, recorded: null);

        Assert.Equal(UnpublishDecision.NothingRecorded, decision);
    }

    /// <summary>
    /// The new definition being the letter's only one confirms the publication.
    /// </summary>
    [Fact]
    public void IsSoleDefinition_OnlyTheNewDefinition_ReturnsTrue()
    {
        Assert.True(GlobalMountPolicy.IsSoleDefinition([Device.ToUpperInvariant()], Device));
    }

    /// <summary>
    /// A definition someone else created between the check and the creation, stacked under or
    /// over the new one, makes the publication be withdrawn.
    /// </summary>
    [Fact]
    public void IsSoleDefinition_StackedOnAnotherDefinition_ReturnsFalse()
    {
        Assert.False(GlobalMountPolicy.IsSoleDefinition([Device, OtherDevice], Device));
        Assert.False(GlobalMountPolicy.IsSoleDefinition([OtherDevice, Device], Device));
    }

    /// <summary>
    /// A letter whose definitions can't be read, or that doesn't point at the device, fails closed.
    /// </summary>
    [Fact]
    public void IsSoleDefinition_UnknownOrDifferentTargets_ReturnsFalse()
    {
        Assert.False(GlobalMountPolicy.IsSoleDefinition(null, Device));
        Assert.False(GlobalMountPolicy.IsSoleDefinition([], Device));
        Assert.False(GlobalMountPolicy.IsSoleDefinition([OtherDevice], Device));
    }

    /// <summary>
    /// A plain string value, as written before owners were stored, has no owner.
    /// </summary>
    [Fact]
    public void ParseRegistryValue_LegacyString_HasNoOwner()
    {
        var mount = GlobalMountManager.ParseRegistryValue(Device);

        Assert.Equal(new PublishedMount(Device, null), mount);
    }

    /// <summary>
    /// A multi-string value carries the device and the owner.
    /// </summary>
    [Fact]
    public void ParseRegistryValue_MultiStringWithOwner_ReturnsDeviceAndOwner()
    {
        var mount = GlobalMountManager.ParseRegistryValue(new[] { Device, Caller });

        Assert.Equal(new PublishedMount(Device, Caller), mount);
    }

    /// <summary>
    /// An empty owner string counts as no owner.
    /// </summary>
    [Fact]
    public void ParseRegistryValue_MultiStringWithEmptyOwner_HasNoOwner()
    {
        var mount = GlobalMountManager.ParseRegistryValue(new[] { Device, string.Empty });

        Assert.Equal(new PublishedMount(Device, null), mount);
    }

    /// <summary>
    /// Values of an unexpected type or shape are ignored.
    /// </summary>
    [Fact]
    public void ParseRegistryValue_UnexpectedType_ReturnsNull()
    {
        Assert.Null(GlobalMountManager.ParseRegistryValue(42));
        Assert.Null(GlobalMountManager.ParseRegistryValue(Array.Empty<string>()));
    }

    /// <summary>
    /// An administrator may change global drive letters, even through a UAC-filtered token whose
    /// Administrators group is deny-only (the caller passes deny-only groups too).
    /// </summary>
    [Fact]
    public void MayChangeGlobalMounts_CallerInAdministrators_ReturnsTrue()
    {
        Assert.True(GlobalMountPolicy.MayChangeGlobalMounts(["S-1-1-0", "S-1-5-32-544"], allowNonAdmins: false));
    }

    /// <summary>
    /// A standard user may not change global drive letters by default.
    /// </summary>
    [Fact]
    public void MayChangeGlobalMounts_StandardUser_ReturnsFalse()
    {
        Assert.False(GlobalMountPolicy.MayChangeGlobalMounts(["S-1-1-0", "S-1-5-32-545", "S-1-5-11"], allowNonAdmins: false));
    }

    /// <summary>
    /// Once an administrator opens global drive letters up to every user, a standard user may
    /// change them too.
    /// </summary>
    [Fact]
    public void MayChangeGlobalMounts_StandardUserWithNonAdminsAllowed_ReturnsTrue()
    {
        Assert.True(GlobalMountPolicy.MayChangeGlobalMounts(["S-1-1-0", "S-1-5-32-545"], allowNonAdmins: true));
    }
}
