namespace ManagedDrive.Core.Mounting;

/// <summary>
/// The order in which a capacity change publishes the new capacity and moves the file system's
/// ceiling, so a save running meanwhile never writes a header smaller than what the disk may hold,
/// and the published capacity never names one that usage could still exceed.
/// </summary>
internal static class CapacityChangeSequence
{
    /// <summary>
    /// Changes the capacity from <paramref name="oldCapacity"/> to <paramref name="newCapacity"/>.
    /// Growth publishes first and raises the ceiling after it, taking the published value back if
    /// the ceiling refuses, so a save running meanwhile never writes data the larger ceiling let in
    /// under a header that still names the smaller capacity; a shrink lowers the ceiling first and
    /// publishes only once it was accepted, so the options never name a capacity usage could still
    /// exceed.
    /// </summary>
    /// <param name="oldCapacity">The capacity in effect now.</param>
    /// <param name="newCapacity">The requested capacity.</param>
    /// <param name="publish">Publishes a capacity in the disk's options.</param>
    /// <param name="setCeiling">Moves the file system's ceiling; returns <c>false</c> when usage exceeds it.</param>
    /// <returns><c>true</c> when the change was made; <c>false</c>, with nothing changed, when it was refused.</returns>
    internal static bool TryApply(ulong oldCapacity, ulong newCapacity, Action<ulong> publish, Func<ulong, bool> setCeiling)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(setCeiling);

        if (newCapacity > oldCapacity)
        {
            publish(newCapacity);
            if (setCeiling(newCapacity))
            {
                return true;
            }

            // Not expected (usage never exceeds the old, smaller capacity); undone so nothing changed.
            publish(oldCapacity);
            return false;
        }

        if (!setCeiling(newCapacity))
        {
            return false;
        }

        publish(newCapacity);
        return true;
    }
}
