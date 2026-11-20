namespace ManagedDrive.App.Services;

/// <summary>
/// The one place that knows where ManagedDrive lives on the web: the GitHub repository and the pages the app
/// links to (About dialog, update fallback). Nothing else should build these addresses by hand.
/// </summary>
internal static class ProjectLinks
{
    /// <summary>
    /// GitHub owner of the repository.
    /// </summary>
    internal const string RepoOwner = "coldhighsun";

    /// <summary>
    /// Name of the GitHub repository.
    /// </summary>
    internal const string RepoName = "ManagedDrive";

    /// <summary>
    /// Web address of the repository.
    /// </summary>
    internal const string RepositoryUrl = $"https://github.com/{RepoOwner}/{RepoName}";

    /// <summary>
    /// Issue tracker of the repository, where users report problems and give feedback.
    /// </summary>
    internal const string IssuesUrl = RepositoryUrl + "/issues";

    /// <summary>
    /// Releases page, for when a release carries no address of its own.
    /// </summary>
    internal const string ReleasesUrl = RepositoryUrl + "/releases/latest";

    /// <summary>
    /// Third-party notices document in the repository.
    /// </summary>
    internal const string ThirdPartyNoticesUrl = RepositoryUrl + "/blob/main/docs/THIRD-PARTY-NOTICES.md";

    /// <summary>
    /// Web address of the owner's GitHub Sponsors page.
    /// </summary>
    internal const string SponsorsUrl = $"https://github.com/sponsors/{RepoOwner}";
}
