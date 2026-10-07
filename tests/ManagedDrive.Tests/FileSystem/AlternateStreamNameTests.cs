namespace ManagedDrive.Tests;

public sealed class AlternateStreamNameTests
{
    [Theory]
    [InlineData("\\a.txt", "\\a.txt")]
    [InlineData("\\dir\\a.txt", "\\dir\\a.txt")]
    [InlineData("\\a.txt::$DATA", "\\a.txt")]
    [InlineData("\\a.txt::$data", "\\a.txt")]
    [InlineData("\\a.txt:s", "\\a.txt:s")]
    [InlineData("\\a.txt:s:$DATA", "\\a.txt:s")]
    [InlineData("\\dir\\a.txt:Zone.Identifier", "\\dir\\a.txt:Zone.Identifier")]
    [InlineData("\\dir:s", "\\dir:s")]
    public void TryNormalize_ValidName_ReturnsTheMapKey(string input, string expected)
    {
        var valid = AlternateStreamName.TryNormalize(input, out var key);

        Assert.True(valid);
        Assert.Equal(expected, key);
    }

    [Theory]
    [InlineData("\\a.txt:")]
    [InlineData("\\a.txt:s:$INDEX_ALLOCATION")]
    [InlineData("\\a.txt:s:other")]
    [InlineData("\\a.txt:s:$DATA:x")]
    [InlineData("\\a.txt:s*")]
    [InlineData("\\a.txt:s?")]
    [InlineData("\\a.txt:s<")]
    [InlineData("\\a.txt:s|")]
    [InlineData("\\a.txt:s\u0001")]
    [InlineData("\\a:b\\c.txt")]
    [InlineData("\\a:b\\c.txt:s")]
    [InlineData("\\:s")]
    [InlineData("\\dir\\:s")]
    [InlineData("\\a.txt:s\\x")]
    public void TryNormalize_MalformedName_ReturnsFalse(string input)
    {
        var valid = AlternateStreamName.TryNormalize(input, out _);

        Assert.False(valid);
    }

    [Fact]
    public void KeyOrSelf_MalformedName_ReturnsTheNameUnchanged()
    {
        Assert.Equal("\\a.txt:", AlternateStreamName.KeyOrSelf("\\a.txt:"));
        Assert.Equal("\\a.txt", AlternateStreamName.KeyOrSelf("\\a.txt::$DATA"));
    }

    [Fact]
    public void IsStreamKey_DistinguishesStreamsFromFiles()
    {
        Assert.True(AlternateStreamName.IsStreamKey("\\a.txt:s"));
        Assert.False(AlternateStreamName.IsStreamKey("\\a.txt"));
        Assert.False(AlternateStreamName.IsStreamKey("\\"));
    }

    [Fact]
    public void OwnerOfAndNameOf_StreamKey_SplitAtTheSeparator()
    {
        Assert.Equal("\\dir\\a.txt", AlternateStreamName.OwnerOf("\\dir\\a.txt:Zone.Identifier"));
        Assert.Equal("Zone.Identifier", AlternateStreamName.NameOf("\\dir\\a.txt:Zone.Identifier"));
    }
}
