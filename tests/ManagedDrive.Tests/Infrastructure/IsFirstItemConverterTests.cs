using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ManagedDrive.App.Infrastructure;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="IsFirstItemConverter"/>, which tells the minimal style's disk list which row is first. WPF list controls need an
/// STA thread, hence <see cref="StaThread"/>.
/// </summary>
public sealed class IsFirstItemConverterTests
{
    private static bool Convert(params object[] values) =>
        (bool)new IsFirstItemConverter().Convert(values, typeof(bool), null, CultureInfo.InvariantCulture);

    private static ListBox ListOf(params string[] items)
    {
        var list = new ListBox();
        foreach (var item in items)
        {
            list.Items.Add(item);
        }

        return list;
    }

    /// <summary>
    /// Verifies that the first item of the list is first.
    /// </summary>
    [Fact]
    public void Convert_FirstItem_ReturnsTrue()
    {
        StaThread.Run(() =>
        {
            var list = ListOf("a", "b");

            Assert.True(Convert("a", list, list.Items.Count));
        });
    }

    /// <summary>
    /// Verifies that a later item is not first.
    /// </summary>
    [Fact]
    public void Convert_LaterItem_ReturnsFalse()
    {
        StaThread.Run(() =>
        {
            var list = ListOf("a", "b");

            Assert.False(Convert("b", list, list.Items.Count));
        });
    }

    /// <summary>
    /// Verifies that the answer follows the list when the first item is removed.
    /// </summary>
    [Fact]
    public void Convert_AfterFirstItemRemoved_NextItemIsFirst()
    {
        StaThread.Run(() =>
        {
            var list = ListOf("a", "b");
            list.Items.RemoveAt(0);

            Assert.True(Convert("b", list, list.Items.Count));
            Assert.False(Convert("a", list, list.Items.Count));
        });
    }

    /// <summary>
    /// Verifies that an empty list, an unset item and an owner that is not an items control give false and do not throw.
    /// </summary>
    [Fact]
    public void Convert_EmptyListUnsetItemOrOtherOwner_ReturnsFalse()
    {
        StaThread.Run(() =>
        {
            Assert.False(Convert("a", new ListBox(), 0));
            Assert.False(Convert(DependencyProperty.UnsetValue, ListOf("a"), 1));
            Assert.False(Convert("a", new object(), 1));
        });
    }
}
