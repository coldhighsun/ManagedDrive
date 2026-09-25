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
    public void ValidateImagePath_UsablePath_ReturnsNone()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), "save-as.mdr");

        var error = CreateDiskOptionsBuilder.ValidateImagePath(imagePath, "Z:", []);

        Assert.Equal(CreateDiskValidationError.None, error);
    }

    [Fact]
    public void ValidateImagePath_RelativePath_ReturnsBadImagePath()
    {
        var error = CreateDiskOptionsBuilder.ValidateImagePath("disk.mdr", "Z:", []);

        Assert.Equal(CreateDiskValidationError.BadImagePath, error);
    }

    [Fact]
    public void ValidateImagePath_OnDisksOwnMountPoint_ReturnsImagePathOnRamDisk()
    {
        var mountPoint = Path.GetTempPath()[..2]; // e.g. "C:"
        var imagePath = Path.Combine(Path.GetTempPath(), "on-ramdisk.mdr");

        var error = CreateDiskOptionsBuilder.ValidateImagePath(imagePath, mountPoint, []);

        Assert.Equal(CreateDiskValidationError.ImagePathOnRamDisk, error);
    }

    [Fact]
    public void ValidateImagePath_OnAnotherRamDisk_ReturnsImagePathOnRamDisk()
    {
        var other = MinimalOptions() with { MountPoint = Path.GetTempPath()[..2] };
        var imagePath = Path.Combine(Path.GetTempPath(), "on-other-ramdisk.mdr");

        var error = CreateDiskOptionsBuilder.ValidateImagePath(imagePath, "Z:", [other]);

        Assert.Equal(CreateDiskValidationError.ImagePathOnRamDisk, error);
    }

    [Fact]
    public void ValidateImagePath_SiblingOfDirectoryMountPoint_ReturnsNone()
    {
        var mountPoint = Path.Combine(Path.GetTempPath(), "ram");
        var imagePath = Path.Combine(Path.GetTempPath(), "ram2.mdr");

        var error = CreateDiskOptionsBuilder.ValidateImagePath(imagePath, mountPoint, []);

        Assert.Equal(CreateDiskValidationError.None, error);
    }

    [Fact]
    public void ValidateImagePath_AnotherDisksImage_ReturnsImagePathInUse()
    {
        var imagePath = Path.Combine(Path.GetTempPath(), "shared.mdr");
        var other = MinimalOptions() with { PersistImagePath = imagePath };

        var error = CreateDiskOptionsBuilder.ValidateImagePath(imagePath, "Z:", [other]);

        Assert.Equal(CreateDiskValidationError.ImagePathInUse, error);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_EditEncryptedReadOnly_KeepsPasswordRegardlessOfCheckbox(bool encryptChecked)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var imagePath = Path.Combine(dir.FullName, "disk.mdr");
            File.WriteAllBytes(imagePath, []);
            var input = ValidCreateInput() with
            {
                ImagePathText = imagePath,
                IsReadOnly = true,
                EncryptChecked = encryptChecked,
                Password1 = "stale-one",
                Password2 = "stale-two",
                WasEncrypted = true,
                OriginalPassword = "password123",
            };

            var result = CreateDiskOptionsBuilder.Build(input);

            Assert.True(result.Success);
            Assert.False(result.PasswordChanged);
            Assert.Null(result.Password);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Build_EditCapacityUntouched_KeepsExactNonWholeMbCapacity()
    {
        const ulong original = (100UL * 1024 * 1024) + 4096;
        var input = ValidCreateInput() with
        {
            Mode = CreateDiskMode.Edit,
            OriginalCapacityBytes = original,
            CapacityValue = 100,
            CapacityIsGb = false,
        };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.True(result.Success);
        Assert.Equal(original, result.Options!.CapacityBytes);
    }

    [Fact]
    public void Build_EditCapacityChanged_UsesDisplayedValue()
    {
        var input = ValidCreateInput() with
        {
            Mode = CreateDiskMode.Edit,
            OriginalCapacityBytes = (100UL * 1024 * 1024) + 4096,
            CapacityValue = 200,
            CapacityIsGb = false,
        };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.True(result.Success);
        Assert.Equal(200UL * 1024 * 1024, result.Options!.CapacityBytes);
    }

    /// <summary>
    /// Editing a disk larger than this machine's current limit keeps its capacity when the
    /// capacity field is left alone.
    /// </summary>
    [Fact]
    public void Build_EditCapacityAboveMaximumUnchanged_KeepsOriginalCapacity()
    {
        const ulong original = 8UL * 1024 * 1024 * 1024;
        var input = ValidCreateInput() with
        {
            Mode = CreateDiskMode.Edit,
            OriginalCapacityBytes = original,
            CapacityValue = 8,
            CapacityIsGb = true,
            MaxCapacityValue = 4,
        };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.True(result.Success);
        Assert.Equal(original, result.Options!.CapacityBytes);
    }

    /// <summary>
    /// A new capacity above the limit is still rejected, even while editing a disk that was
    /// already above it.
    /// </summary>
    [Fact]
    public void Build_EditCapacityChangedAboveMaximum_ReturnsBadCapacity()
    {
        var input = ValidCreateInput() with
        {
            Mode = CreateDiskMode.Edit,
            OriginalCapacityBytes = 8UL * 1024 * 1024 * 1024,
            CapacityValue = 6,
            CapacityIsGb = true,
            MaxCapacityValue = 4,
        };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.Equal(CreateDiskValidationError.BadCapacity, result.Error);
    }

    /// <summary>
    /// An auto-save interval above 60 minutes set through the CLI survives an edit that leaves
    /// it unchanged; a different out-of-range value is still rejected.
    /// </summary>
    /// <param name="interval">The interval the dialog submits.</param>
    /// <param name="expectedError">The expected validation error.</param>
    [Theory]
    [InlineData(120, CreateDiskValidationError.None)]
    [InlineData(90, CreateDiskValidationError.BadAutoSaveInterval)]
    public void Build_EditIntervalAboveRange_AcceptsOnlyTheOriginalInterval(
        int interval, CreateDiskValidationError expectedError)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var input = ValidCreateInput() with
            {
                Mode = CreateDiskMode.Edit,
                ImagePathText = Path.Combine(dir.FullName, "disk.mdr"),
                AutoSaveEnabled = true,
                IntervalValue = interval,
                OriginalAutoSaveIntervalMinutes = 120,
            };

            var result = CreateDiskOptionsBuilder.Build(input);

            Assert.Equal(expectedError, result.Error);
            if (expectedError == CreateDiskValidationError.None)
            {
                Assert.Equal(120u, result.Options!.AutoSaveIntervalMinutes);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A snapshot count limit above 20 set through the CLI survives an edit that leaves it
    /// unchanged; a different out-of-range value is still rejected.
    /// </summary>
    /// <param name="count">The snapshot count the dialog submits.</param>
    /// <param name="expectedError">The expected validation error.</param>
    [Theory]
    [InlineData(50, CreateDiskValidationError.None)]
    [InlineData(30, CreateDiskValidationError.BadSnapshotCount)]
    public void Build_EditSnapshotCountAboveRange_AcceptsOnlyTheOriginalCount(
        int count, CreateDiskValidationError expectedError)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var input = ValidCreateInput() with
            {
                Mode = CreateDiskMode.Edit,
                ImagePathText = Path.Combine(dir.FullName, "disk.mdr"),
                AutoSaveEnabled = true,
                IntervalValue = 10,
                SnapshotCountEnabled = true,
                SnapshotCountValue = count,
                OriginalMaxSnapshotCount = 50,
            };

            var result = CreateDiskOptionsBuilder.Build(input);

            Assert.Equal(expectedError, result.Error);
            if (expectedError == CreateDiskValidationError.None)
            {
                Assert.Equal(50u, result.Options!.MaxSnapshotCount);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A high-usage percentage the slider can't represent exactly (fractional, or outside 1-99
    /// from the CLI) is kept while the slider stays on the position it was shown at.
    /// </summary>
    /// <param name="original">The edited disk's stored percentage.</param>
    [Theory]
    [InlineData(85.5)]
    [InlineData(100.0)]
    public void Build_EditHighUsagePercentUnchanged_KeepsOriginalPercent(double original)
    {
        var input = ValidCreateInput() with
        {
            Mode = CreateDiskMode.Edit,
            HighUsageWarnEnabled = true,
            HighUsageWarnPercentValue = CreateDiskOptionsBuilder.ToHighUsageWarnPercentValue(original),
            OriginalHighUsageWarnPercent = original,
        };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.True(result.Success);
        Assert.Equal(original, result.Options!.HighUsageWarnPercent);
    }

    /// <summary>
    /// Moving the slider away from the original percentage's position saves the new value.
    /// </summary>
    [Fact]
    public void Build_EditHighUsagePercentChanged_UsesNewPercent()
    {
        var input = ValidCreateInput() with
        {
            Mode = CreateDiskMode.Edit,
            HighUsageWarnEnabled = true,
            HighUsageWarnPercentValue = 80,
            OriginalHighUsageWarnPercent = 85.5,
        };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.True(result.Success);
        Assert.Equal(80.0, result.Options!.HighUsageWarnPercent);
    }

    /// <summary>
    /// An invalid stored percentage (0, above 100, or <c>NaN</c>) is not written back; the slider
    /// position it was shown at is saved instead.
    /// </summary>
    /// <param name="original">The edited disk's stored percentage.</param>
    /// <param name="expected">The expected saved percentage.</param>
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(150.0, 99.0)]
    [InlineData(double.NaN, 99.0)]
    public void Build_EditHighUsagePercentInvalidOriginal_SavesSliderPosition(double original, double expected)
    {
        var input = ValidCreateInput() with
        {
            Mode = CreateDiskMode.Edit,
            HighUsageWarnEnabled = true,
            HighUsageWarnPercentValue = CreateDiskOptionsBuilder.ToHighUsageWarnPercentValue(original),
            OriginalHighUsageWarnPercent = original,
        };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.True(result.Success);
        Assert.Equal(expected, result.Options!.HighUsageWarnPercent);
    }

    /// <summary>
    /// Only a percentage above 0 and at most 100 is kept as-is on edit.
    /// </summary>
    /// <param name="percent">The stored percentage.</param>
    /// <param name="expected">Whether it may be kept.</param>
    [Theory]
    [InlineData(85.5, true)]
    [InlineData(100.0, true)]
    [InlineData(0.0, false)]
    [InlineData(100.5, false)]
    [InlineData(double.NaN, false)]
    public void CanKeepHighUsageWarnPercent_Percent_ReturnsWhetherValid(double percent, bool expected)
    {
        var canKeep = CreateDiskOptionsBuilder.CanKeepHighUsageWarnPercent(percent);

        Assert.Equal(expected, canKeep);
    }

    /// <summary>
    /// The slider position for a stored percentage truncates and clamps it to 1-99, and maps
    /// <c>NaN</c> to 99.
    /// </summary>
    /// <param name="percent">The stored percentage.</param>
    /// <param name="expected">The expected slider position.</param>
    [Theory]
    [InlineData(85.5, 85)]
    [InlineData(90.0, 90)]
    [InlineData(100.0, 99)]
    [InlineData(0.0, 1)]
    [InlineData(double.NaN, 99)]
    public void ToHighUsageWarnPercentValue_Percent_ReturnsSliderPosition(double percent, int expected)
    {
        var value = CreateDiskOptionsBuilder.ToHighUsageWarnPercentValue(percent);

        Assert.Equal(expected, value);
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

    [Fact]
    public void Build_CustomZstdLevelDisabled_LeavesCustomZstdLevelNull()
    {
        var result = CreateDiskOptionsBuilder.Build(ValidCreateInput());

        Assert.True(result.Success);
        Assert.Null(result.Options!.CustomZstdLevel);
    }

    [Fact]
    public void Build_CustomZstdLevelEnabled_SetsCustomZstdLevel()
    {
        var input = ValidCreateInput() with { CustomZstdLevelEnabled = true, CustomZstdLevelValue = 12 };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.True(result.Success);
        Assert.Equal(12, result.Options!.CustomZstdLevel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(23)]
    public void Build_CustomZstdLevelOutOfRange_ReturnsBadCustomZstdLevel(int value)
    {
        var input = ValidCreateInput() with { CustomZstdLevelEnabled = true, CustomZstdLevelValue = value };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.Equal(CreateDiskValidationError.BadCustomZstdLevel, result.Error);
    }

    [Fact]
    public void Build_CustomZstdLevelEnabledButCompressionNone_IgnoresCustomZstdLevel()
    {
        var input = ValidCreateInput() with
        {
            CompressionLevel = ImageCompressionLevel.None,
            CustomZstdLevelEnabled = true,
            CustomZstdLevelValue = 12,
        };

        var result = CreateDiskOptionsBuilder.Build(input);

        Assert.True(result.Success);
        Assert.Null(result.Options!.CustomZstdLevel);
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
