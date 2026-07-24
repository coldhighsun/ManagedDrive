using ManagedDrive.Core;

namespace ManagedDrive.Tests;

public sealed class CreateDiskOptionsBuilderTests
{
    [Fact]
    public void Build_ValidInMemoryDisk_Succeeds()
    {
        var result = CreateDiskOptionsBuilder.Build(ValidCreateInput());

        Assert.True(result.Success);
        Assert.NotNull(result.Options);
        Assert.Equal("Z:", result.Options!.MountPoint);
        Assert.Equal(2UL * 1024 * 1024 * 1024, result.Options.CapacityBytes);
        Assert.Equal("Data", result.Options.VolumeLabel);
        Assert.Null(result.Options.PersistImagePath);
        Assert.False(result.PasswordChanged);
    }

    [Fact]
    public void Build_NoMountPoint_ReturnsNoDriveLetter()
    {
        var result = CreateDiskOptionsBuilder.Build(ValidCreateInput() with { MountPoint = null });

        Assert.Equal(CreateDiskValidationError.NoDriveLetter, result.Error);
        Assert.Null(result.Options);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5000)]
    public void Build_CapacityOutOfRange_ReturnsBadCapacity(int capacityValue)
    {
        var result = CreateDiskOptionsBuilder.Build(ValidCreateInput() with { CapacityValue = capacityValue });

        Assert.Equal(CreateDiskValidationError.BadCapacity, result.Error);
    }

    [Fact]
    public void Build_ImportModeIgnoresCapacityValue()
    {
        var input = ValidCreateInput() with
        {
            Mode = CreateDiskMode.ImportImage,
            CapacityValue = 0,
            ImportCapacityBytes = 4UL * 1024 * 1024,
            ImportVolumeLabel = "Imported",
        };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.True(result.Success);
        Assert.Equal(4UL * 1024 * 1024, result.Options!.CapacityBytes);
        Assert.Equal("Imported", result.Options.VolumeLabel);
    }

    [Fact]
    public void Build_ArchiveImportForcesReadOnlyAndSourcePath()
    {
        var input = ValidCreateInput() with
        {
            Mode = CreateDiskMode.ImportArchive,
            IsReadOnly = false,
            ImportArchivePath = @"C:\data\archive.zip",
            ImportCapacityBytes = 8UL * 1024 * 1024,
            ImportVolumeLabel = "Archive",
        };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.True(result.Success);
        Assert.True(result.Options!.ReadOnly);
        Assert.Equal(@"C:\data\archive.zip", result.Options.SourceArchivePath);
        Assert.Equal(8UL * 1024 * 1024, result.Options.CapacityBytes);
    }

    [Fact]
    public void Build_BadImagePath_ReturnsBadImagePath()
    {
        var input = ValidCreateInput() with { ImagePathText = "relative\\path.mdr" };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.Equal(CreateDiskValidationError.BadImagePath, result.Error);
    }

    [Fact]
    public void Build_ImagePathUsedByAnotherDisk_ReturnsImagePathInUse()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var imagePath = Path.Combine(dir.FullName, "disk.mdr");
            var other = MinimalOptions() with { MountPoint = "Y:", PersistImagePath = imagePath };
            var input = ValidCreateInput() with { ImagePathText = imagePath, OtherDisks = [other] };

            var result = CreateDiskOptionsBuilder.Build(input);

            Assert.Equal(CreateDiskValidationError.ImagePathInUse, result.Error);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Build_ImagePathUnderMountPoint_ReturnsImagePathOnRamDisk()
    {
        // Path is rooted with an existing parent dir, but starts with the disk's own mount point.
        var input = ValidCreateInput() with
        {
            MountPoint = Path.GetTempPath()[..2], // e.g. "C:"
            ImagePathText = Path.Combine(Path.GetTempPath(), "on-ramdisk.mdr"),
        };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.Equal(CreateDiskValidationError.ImagePathOnRamDisk, result.Error);
    }

    [Fact]
    public void Build_ReadOnlyWithoutImage_ReturnsReadOnlyRequiresImage()
    {
        var input = ValidCreateInput() with { IsReadOnly = true, ImagePathText = null };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.Equal(CreateDiskValidationError.ReadOnlyRequiresImage, result.Error);
    }

    [Fact]
    public void Build_ReadOnlyWithMissingImage_ReturnsReadOnlyImageNotFound()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var input = ValidCreateInput() with
            {
                IsReadOnly = true,
                ImagePathText = Path.Combine(dir.FullName, "missing.mdr"),
            };

            var result = CreateDiskOptionsBuilder.Build(input);

            Assert.Equal(CreateDiskValidationError.ReadOnlyImageNotFound, result.Error);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Build_AutoSaveWithImage_SetsInterval()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var input = ValidCreateInput() with
            {
                ImagePathText = Path.Combine(dir.FullName, "disk.mdr"),
                AutoSaveEnabled = true,
                IntervalValue = 15,
            };

            var result = CreateDiskOptionsBuilder.Build(input);

            Assert.True(result.Success);
            Assert.Equal(15U, result.Options!.AutoSaveIntervalMinutes);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Build_BadAutoSaveInterval_ReturnsError(int interval)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var input = ValidCreateInput() with
            {
                ImagePathText = Path.Combine(dir.FullName, "disk.mdr"),
                AutoSaveEnabled = true,
                IntervalValue = interval,
            };

            var result = CreateDiskOptionsBuilder.Build(input);

            Assert.Equal(CreateDiskValidationError.BadAutoSaveInterval, result.Error);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Build_BadHighUsagePercent_ReturnsError(int percent)
    {
        var input = ValidCreateInput() with { HighUsageWarnEnabled = true, HighUsageWarnPercentValue = percent };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.Equal(CreateDiskValidationError.BadHighUsagePercent, result.Error);
    }

    [Fact]
    public void Build_HighUsageDisabled_LeavesPercentNull()
    {
        var input = ValidCreateInput() with { HighUsageWarnEnabled = false };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.True(result.Success);
        Assert.Null(result.Options!.HighUsageWarnPercent);
    }

    [Fact]
    public void Build_EncryptWithoutPassword_ReturnsPasswordRequired()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var input = ValidCreateInput() with
            {
                ImagePathText = Path.Combine(dir.FullName, "disk.mdr"),
                EncryptChecked = true,
                Password1 = "",
                Password2 = "",
            };

            var result = CreateDiskOptionsBuilder.Build(input);

            Assert.Equal(CreateDiskValidationError.PasswordRequired, result.Error);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Build_PasswordMismatch_ReturnsError()
    {
        var input = EncryptedInput(out var dir, "password123", "password124");
        try
        {
            var result = CreateDiskOptionsBuilder.Build(input);
            Assert.Equal(CreateDiskValidationError.PasswordMismatch, result.Error);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Build_PasswordTooShort_ReturnsError()
    {
        var input = EncryptedInput(out var dir, "short", "short");
        try
        {
            var result = CreateDiskOptionsBuilder.Build(input);
            Assert.Equal(CreateDiskValidationError.PasswordTooShort, result.Error);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Build_ValidNewPassword_SetsPasswordChanged()
    {
        var input = EncryptedInput(out var dir, "password123", "password123");
        try
        {
            var result = CreateDiskOptionsBuilder.Build(input);
            Assert.True(result.Success);
            Assert.Equal("password123", result.Password);
            Assert.True(result.PasswordChanged);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Build_EditEncryptedKeepsPassword_ReportsUnchanged()
    {
        var input = EncryptedInput(out var dir, "password123", "password123") with
        {
            WasEncrypted = true,
            OriginalPassword = "password123",
        };
        try
        {
            var result = CreateDiskOptionsBuilder.Build(input);
            Assert.True(result.Success);
            Assert.False(result.PasswordChanged);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Build_EditEncryptedUnchecked_RemovesPassword()
    {
        var input = ValidCreateInput() with
        {
            EncryptChecked = false,
            WasEncrypted = true,
            OriginalPassword = "password123",
        };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.True(result.Success);
        Assert.True(result.PasswordChanged);
        Assert.Null(result.Password);
    }

    private static CreateDiskInput EncryptedInput(out DirectoryInfo dir, string p1, string p2)
    {
        dir = Directory.CreateTempSubdirectory();
        return ValidCreateInput() with
        {
            ImagePathText = Path.Combine(dir.FullName, "disk.mdr"),
            EncryptChecked = true,
            Password1 = p1,
            Password2 = p2,
        };
    }

    private static CreateDiskInput ValidCreateInput() => new()
    {
        MountPoint = "Z:",
        Mode = CreateDiskMode.Create,
        CapacityValue = 2,
        CapacityIsGb = true,
        MaxCapacityValue = 4096,
        VolumeLabel = "Data",
        ImagePathText = null,
        IsReadOnly = false,
        AutoMount = false,
        CompressionLevel = ImageCompressionLevel.Fastest,
        SaveImageOnExit = true,
        OtherDisks = [],
    };

    private static DiskOptions MinimalOptions() => new()
    {
        MountPoint = "X:",
        CapacityBytes = 1024 * 1024,
    };
}
