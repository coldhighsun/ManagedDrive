namespace ManagedDrive.Benchmarks;

internal static class DriveLetterHelper
{
    public static string FindFreeMountPoint()
    {
        var used = DriveInfo.GetDrives().Select(d => d.Name[0]).ToHashSet();
        for (var c = 'D'; c <= 'Z'; c++)
        {
            if (!used.Contains(c))
                return $"{c}:";
        }

        throw new InvalidOperationException("No free drive letter available for benchmark RAM disk.");
    }
}