namespace ManagedDrive.Core.Mounting;

/// <summary>
/// What a save does when the disk's capacity grew while it wrote the image: the capacity can
/// change without waiting for the save lock, so data the larger capacity let in may be in an image
/// whose header still names the smaller one, which fails to load. Pure, so the order of the steps
/// is tested without a mounted disk.
/// </summary>
internal static class SaveCapacityFix
{
    /// <summary>
    /// Brings the header of the image just written up to a capacity raised during the save. A
    /// capacity lowered meanwhile leaves a larger header, which loads, so only growth is patched.
    /// When the patch fails the disk is marked dirty before returning, so it still needs saving if
    /// the caller's next save fails as well.
    /// </summary>
    /// <param name="savedCapacity">The capacity written into the header.</param>
    /// <param name="currentCapacity">The disk's capacity now.</param>
    /// <param name="tryPatch">Writes a capacity into the image's header; returns <c>false</c> when it could not.</param>
    /// <param name="markDirty">Marks the disk as needing a save.</param>
    /// <returns><c>true</c> when the header is fine as it is or was patched; <c>false</c> when the image must be written again.</returns>
    internal static bool TryFixHeader(ulong savedCapacity, ulong currentCapacity, Func<ulong, bool> tryPatch, Action markDirty)
    {
        ArgumentNullException.ThrowIfNull(tryPatch);
        ArgumentNullException.ThrowIfNull(markDirty);

        if (currentCapacity <= savedCapacity)
        {
            return true;
        }

        if (tryPatch(currentCapacity))
        {
            return true;
        }

        markDirty();
        return false;
    }

    /// <summary>
    /// Runs save passes until one leaves an image whose header matches the capacity. Each pass
    /// writes the capacity current when it starts, so another pass is needed only if the capacity
    /// grows again while it runs and its header cannot be patched.
    /// </summary>
    /// <param name="pass">Writes the image once; returns <c>false</c> when it must be written again.</param>
    /// <param name="beforeRepeat">Called before each pass after the first.</param>
    /// <returns>The number of passes run.</returns>
    internal static int SaveUntilHeaderMatches(Func<bool> pass, Action beforeRepeat)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(beforeRepeat);

        var passes = 1;
        while (!pass())
        {
            beforeRepeat();
            passes++;
        }

        return passes;
    }
}
