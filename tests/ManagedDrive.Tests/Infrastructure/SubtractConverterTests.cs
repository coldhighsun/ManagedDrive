using System.Globalization;
using System.Windows.Data;
using ManagedDrive.App.Infrastructure;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="SubtractConverter"/>: sizing a drop-down list from its combo box.
/// </summary>
public sealed class SubtractConverterTests
{
    private readonly SubtractConverter _converter = new();

    /// <summary>
    /// Verifies that the parameter is taken off the value.
    /// </summary>
    [Fact]
    public void Convert_ValueAndParameter_Subtracts()
    {
        var result = _converter.Convert(300.0, typeof(double), "10", CultureInfo.InvariantCulture);

        Assert.Equal(290.0, result);
    }

    /// <summary>
    /// Verifies that the result never goes below zero.
    /// </summary>
    [Fact]
    public void Convert_ParameterLargerThanValue_ReturnsZero()
    {
        var result = _converter.Convert(4.0, typeof(double), "10", CultureInfo.InvariantCulture);

        Assert.Equal(0.0, result);
    }

    /// <summary>
    /// Verifies that a missing or unreadable parameter subtracts nothing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("abc")]
    public void Convert_NoUsableParameter_ReturnsValueUnchanged(string? parameter)
    {
        var result = _converter.Convert(120.0, typeof(double), parameter, CultureInfo.InvariantCulture);

        Assert.Equal(120.0, result);
    }

    /// <summary>
    /// Verifies that a width that is not a number (a combo box not laid out yet) leaves the binding alone.
    /// </summary>
    [Fact]
    public void Convert_NaN_DoesNothing()
    {
        var result = _converter.Convert(double.NaN, typeof(double), "10", CultureInfo.InvariantCulture);

        Assert.Same(Binding.DoNothing, result);
    }

    /// <summary>
    /// Verifies that a decimal parameter is read with the invariant culture, whatever the UI culture.
    /// </summary>
    [Fact]
    public void Convert_DecimalParameter_UsesInvariantCulture()
    {
        var result = _converter.Convert(100.0, typeof(double), "2.5", new CultureInfo("de-DE"));

        Assert.Equal(97.5, result);
    }
}
