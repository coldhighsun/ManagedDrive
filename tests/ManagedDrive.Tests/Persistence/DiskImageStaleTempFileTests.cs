namespace ManagedDrive.Tests;

public sealed class DiskImageStaleTempFileTests
{
    [Fact]
    public void DeleteStaleTempFile_TempFileExists_DeletesItAndKeepsTheImage()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var imagePath = Path.Combine(directory.FullName, "disk.mdr");
            File.WriteAllText(imagePath, "image");
            File.WriteAllText(imagePath + ".tmp", "partial");

            var deleted = DiskImageSerializer.DeleteStaleTempFile(imagePath);

            Assert.True(deleted);
            Assert.False(File.Exists(imagePath + ".tmp"));
            Assert.True(File.Exists(imagePath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DeleteStaleTempFile_NoTempFile_ReturnsFalse()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");

        Assert.False(DiskImageSerializer.DeleteStaleTempFile(imagePath));
    }

    [Fact]
    public void DeleteStaleTempFile_TempFileBeingWritten_LeavesItAlone()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var imagePath = Path.Combine(directory.FullName, "disk.mdr");
            using var locked = new FileStream(imagePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.Read);

            var deleted = Record.Exception(() => DiskImageSerializer.DeleteStaleTempFile(imagePath));

            Assert.Null(deleted);
            Assert.True(File.Exists(imagePath + ".tmp"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
