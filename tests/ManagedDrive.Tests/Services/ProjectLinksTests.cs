using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="ProjectLinks"/>: every address must be a well-formed HTTPS GitHub URL of this project.
/// </summary>
public sealed class ProjectLinksTests
{
    /// <summary>
    /// Verifies the repository address is built from the owner and repository name.
    /// </summary>
    [Fact]
    public void RepositoryUrl_OwnerAndName_BuildsGitHubAddress()
    {
        // Arrange / Act
        var url = ProjectLinks.RepositoryUrl;

        // Assert
        Assert.Equal($"https://github.com/{ProjectLinks.RepoOwner}/{ProjectLinks.RepoName}", url);
    }

    /// <summary>
    /// Verifies the feedback link points at the repository's issue tracker.
    /// </summary>
    [Fact]
    public void IssuesUrl_Repository_PointsToIssueTracker()
    {
        // Arrange / Act
        var url = ProjectLinks.IssuesUrl;

        // Assert
        Assert.Equal(ProjectLinks.RepositoryUrl + "/issues", url);
    }

    /// <summary>
    /// Verifies every link is an absolute HTTPS URL, so the About dialog's navigation can never throw.
    /// </summary>
    [Theory]
    [InlineData(ProjectLinks.RepositoryUrl)]
    [InlineData(ProjectLinks.IssuesUrl)]
    [InlineData(ProjectLinks.ReleasesUrl)]
    [InlineData(ProjectLinks.ThirdPartyNoticesUrl)]
    [InlineData(ProjectLinks.SponsorsUrl)]
    public void Links_AnyLink_IsAbsoluteHttpsUri(string link)
    {
        // Arrange / Act
        var parsed = Uri.TryCreate(link, UriKind.Absolute, out var uri);

        // Assert
        Assert.True(parsed);
        Assert.Equal(Uri.UriSchemeHttps, uri!.Scheme);
        Assert.Equal("github.com", uri.Host);
    }
}
