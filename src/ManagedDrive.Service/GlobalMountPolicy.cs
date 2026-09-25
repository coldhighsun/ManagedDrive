namespace ManagedDrive.Service;

/// <summary>
/// A global symlink the service has recorded as published.
/// </summary>
/// <param name="DevicePath">The NT device path the symlink targets.</param>
/// <param name="OwnerSid">
/// SID of the user who published it, or <c>null</c> for an entry written before owners were
/// recorded.
/// </param>
internal sealed record PublishedMount(string DevicePath, string? OwnerSid);

/// <summary>
/// What <see cref="GlobalMountManager.Publish"/> should do with a request.
/// </summary>
internal enum PublishDecision
{
    /// <summary>
    /// Define the global symlink and record it.
    /// </summary>
    Create,

    /// <summary>
    /// The caller already published this exact letter → device mapping; succeed without
    /// stacking a second definition.
    /// </summary>
    AlreadyPublished,

    /// <summary>
    /// The requested device isn't present.
    /// </summary>
    RejectDeviceMissing,

    /// <summary>
    /// The letter is already defined system-wide by something this service didn't publish
    /// (the system drive, a physical or network drive, ...).
    /// </summary>
    RejectLetterInUse,

    /// <summary>
    /// The letter is published by this service for another user or another device that is still present.
    /// </summary>
    RejectPublishedByOther,
}

/// <summary>
/// What <see cref="GlobalMountManager.Unpublish"/> should do with a request.
/// </summary>
internal enum UnpublishDecision
{
    /// <summary>
    /// Remove the recorded symlink.
    /// </summary>
    Remove,

    /// <summary>
    /// Nothing is recorded for the letter; treat it as already gone.
    /// </summary>
    NothingRecorded,

    /// <summary>
    /// The letter was published by another user.
    /// </summary>
    RejectNotOwner,
}

/// <summary>
/// Pure authorization rules for publish/unpublish requests. Any authenticated local user can
/// reach the service's pipe and the service acts as LocalSystem, so these rules are what stop a
/// standard user from redirecting an existing drive letter (e.g. the system drive) for every
/// session and service to a volume they control, or from removing another user's published letter.
/// </summary>
internal static class GlobalMountPolicy
{
    /// <summary>
    /// SID of the <c>BUILTIN\Administrators</c> group.
    /// </summary>
    private const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>
    /// Whether a caller may publish or unpublish global drive letters at all. A global letter is
    /// visible to every session and service on the machine, so by default only administrators may
    /// change them. Membership counts even when UAC has filtered the group to deny-only: the app
    /// normally runs unelevated, and an administrator could elevate anyway.
    /// </summary>
    /// <param name="callerGroupSids">
    /// SIDs of the caller's groups, both enabled and deny-only.
    /// </param>
    /// <param name="allowNonAdmins">
    /// Whether an administrator has opened global drive letters up to every user.
    /// </param>
    /// <returns><c>true</c> if the caller may change global drive letters.</returns>
    public static bool MayChangeGlobalMounts(IEnumerable<string> callerGroupSids, bool allowNonAdmins) =>
        allowNonAdmins || callerGroupSids.Contains(AdministratorsSid, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decides how to handle a publish request for a letter whose stale record (if any) has
    /// already been purged.
    /// </summary>
    /// <param name="devicePath">The requested target device.</param>
    /// <param name="callerSid">SID of the requesting user.</param>
    /// <param name="recorded">The live record for the letter, or <c>null</c> if none.</param>
    /// <param name="deviceExists">Whether <paramref name="devicePath"/> is present.</param>
    /// <param name="letterTargets">
    /// The letter's current global definitions, current one first (empty if it has none), or
    /// <c>null</c> if they could not be queried.
    /// </param>
    /// <returns>The action to take.</returns>
    public static PublishDecision DecidePublish(
        string devicePath, string callerSid, PublishedMount? recorded, bool deviceExists, IReadOnlyList<string>? letterTargets)
    {
        if (!deviceExists)
        {
            return PublishDecision.RejectDeviceMissing;
        }

        if (recorded is not null)
        {
            if (!string.Equals(recorded.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase) ||
                !CanManage(recorded, callerSid))
            {
                return PublishDecision.RejectPublishedByOther;
            }

            // Already published only if the letter still resolves to the device; our own
            // definition may have been removed behind our back (recreate it then), or replaced
            // or covered by someone else's (refuse, as for any letter in use).
            return letterTargets switch
            {
                [var current, ..] when string.Equals(current, devicePath, StringComparison.OrdinalIgnoreCase) =>
                    PublishDecision.AlreadyPublished,
                [] => PublishDecision.Create,
                _ => PublishDecision.RejectLetterInUse,
            };
        }

        // Fails closed: a letter whose definitions can't be queried counts as in use.
        return letterTargets is [] ? PublishDecision.Create : PublishDecision.RejectLetterInUse;
    }

    /// <summary>
    /// Decides how to handle an unpublish request.
    /// </summary>
    /// <param name="callerSid">SID of the requesting user.</param>
    /// <param name="recorded">The record for the letter, or <c>null</c> if none.</param>
    /// <returns>The action to take.</returns>
    public static UnpublishDecision DecideUnpublish(string callerSid, PublishedMount? recorded)
    {
        if (recorded is null)
        {
            return UnpublishDecision.NothingRecorded;
        }

        return CanManage(recorded, callerSid) ? UnpublishDecision.Remove : UnpublishDecision.RejectNotOwner;
    }

    /// <summary>
    /// Whether the definition just created for a letter is its only one. The earlier
    /// "letter is free" check and the creation aren't atomic, and a DOS-device definition stacks
    /// on top of (or under) any other instead of failing, so this is re-checked afterwards to
    /// catch a definition someone else created in between.
    /// </summary>
    /// <param name="targets">
    /// The letter's definitions after the creation, current one first, or <c>null</c> if they
    /// could not be queried.
    /// </param>
    /// <param name="devicePath">The device the letter was just pointed at.</param>
    /// <returns>
    /// <c>true</c> if <paramref name="devicePath"/> is the letter's single definition; otherwise
    /// <c>false</c>, and the new definition should be removed again.
    /// </returns>
    public static bool IsSoleDefinition(IReadOnlyList<string>? targets, string devicePath) =>
        targets is [var only] && string.Equals(only, devicePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="callerSid"/> may re-publish or remove <paramref name="recorded"/>.
    /// Entries from before owners were recorded have no owner; any caller may manage those, so an
    /// upgrade doesn't strand a letter published by the previous version (a stale one is reclaimed by
    /// reconciliation anyway once its device goes away).
    /// </summary>
    /// <param name="recorded">The recorded publication.</param>
    /// <param name="callerSid">SID of the requesting user.</param>
    /// <returns><c>true</c> if the caller owns the entry or it has no recorded owner.</returns>
    private static bool CanManage(PublishedMount recorded, string callerSid) =>
        recorded.OwnerSid is null || string.Equals(recorded.OwnerSid, callerSid, StringComparison.OrdinalIgnoreCase);
}
