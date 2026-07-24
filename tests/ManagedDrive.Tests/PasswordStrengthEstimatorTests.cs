namespace ManagedDrive.Tests;

public class PasswordStrengthEstimatorTests
{
    [Theory]
    [InlineData("Password1")]
    [InlineData("abcd1234")]
    public void Estimate_LongEnoughWithTwoClasses_ReturnsMedium(string password)
    {
        var strength = PasswordStrengthEstimator.Estimate(password);

        Assert.Equal(PasswordStrength.Medium, strength);
    }

    [Theory]
    [InlineData("Str0ng!Password")]
    [InlineData("C0mplex#Passw0rd")]
    public void Estimate_LongWithThreeOrMoreClasses_ReturnsStrong(string password)
    {
        var strength = PasswordStrengthEstimator.Estimate(password);

        Assert.Equal(PasswordStrength.Strong, strength);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("password")]
    [InlineData("12345678")]
    public void Estimate_ShortOrSingleClassPassword_ReturnsWeak(string password)
    {
        var strength = PasswordStrengthEstimator.Estimate(password);

        Assert.Equal(PasswordStrength.Weak, strength);
    }
}