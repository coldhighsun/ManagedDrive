namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="CreateDiskOptionsBuilder.ValidatePasswordLength"/>, the length rule shared
/// by the create/edit dialogs and the CLI.
/// </summary>
public sealed class PasswordLengthValidationTests
{
    /// <summary>
    /// Lengths from the minimum to the maximum are accepted, both limits included.
    /// </summary>
    [Theory]
    [InlineData(CreateDiskOptionsBuilder.MinPasswordLength)]
    [InlineData(CreateDiskOptionsBuilder.MaxPasswordLength)]
    [InlineData(20)]
    public void ValidatePasswordLength_LengthWithinLimits_ReturnsNone(int length)
    {
        var result = CreateDiskOptionsBuilder.ValidatePasswordLength(new string('a', length));

        Assert.Equal(CreateDiskValidationError.None, result);
    }

    /// <summary>
    /// An empty password, and anything shorter than the minimum, is too short.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(CreateDiskOptionsBuilder.MinPasswordLength - 1)]
    public void ValidatePasswordLength_LengthBelowMinimum_ReturnsTooShort(int length)
    {
        var result = CreateDiskOptionsBuilder.ValidatePasswordLength(new string('a', length));

        Assert.Equal(CreateDiskValidationError.PasswordTooShort, result);
    }

    /// <summary>
    /// A password longer than the maximum is too long.
    /// </summary>
    [Fact]
    public void ValidatePasswordLength_LengthAboveMaximum_ReturnsTooLong()
    {
        var result = CreateDiskOptionsBuilder.ValidatePasswordLength(new string('a', CreateDiskOptionsBuilder.MaxPasswordLength + 1));

        Assert.Equal(CreateDiskValidationError.PasswordTooLong, result);
    }
}
