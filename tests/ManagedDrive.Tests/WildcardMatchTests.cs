using ManagedDrive.Core;

namespace ManagedDrive.Tests;

public sealed class WildcardMatchTests
{
    [Theory]
    [InlineData("foo.txt", "foo.txt", true)]
    [InlineData("foo.txt", "bar.txt", false)]
    [InlineData("FOO.TXT", "foo.txt", true)]
    [InlineData("foo.txt", "foo.tx", false)]
    [InlineData("foo.tx", "foo.txt", false)]
    public void ExactAndCaseInsensitiveMatch(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, WildcardMatcher.Match(pattern, name));
    }

    [Theory]
    [InlineData("f?o.txt", "foo.txt", true)]
    [InlineData("f?o.txt", "fo.txt", false)]
    [InlineData("???", "abc", true)]
    [InlineData("???", "ab", false)]
    public void QuestionMarkMatchesSingleChar(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, WildcardMatcher.Match(pattern, name));
    }

    [Theory]
    [InlineData("*.txt", "foo.txt", true)]
    [InlineData("*.txt", "foo.doc", false)]
    [InlineData("foo*", "foo.txt", true)]
    [InlineData("foo*", "bar.txt", false)]
    [InlineData("f*o.txt", "foo.txt", true)]
    [InlineData("f*o.txt", "fabco.txt", true)]
    [InlineData("*", "anything", true)]
    [InlineData("*", "", true)]
    [InlineData("**", "anything", true)]
    [InlineData("*a*b*", "xaxbx", true)]
    [InlineData("*a*b*", "xbxax", false)]
    public void StarMatchesZeroOrMoreChars(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, WildcardMatcher.Match(pattern, name));
    }

    [Theory]
    [InlineData("", "", true)]
    [InlineData("", "a", false)]
    [InlineData("a", "", false)]
    [InlineData("*", "", true)]
    public void EmptyPatternOrName(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, WildcardMatcher.Match(pattern, name));
    }

    [Theory]
    [InlineData("*a*a*a*b", "aaab", true)]
    [InlineData("*a*a*a*b", "aaaab", true)]
    [InlineData("*a*a*a*b", "aab", false)]
    [InlineData("*a*a*a*b", "aaac", false)]
    public void AdversarialBacktrackingPatterns(string pattern, string name, bool expected)
    {
        Assert.Equal(expected, WildcardMatcher.Match(pattern, name));
    }
}
