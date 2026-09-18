using GitHubReleaseUpdater.LastCheck;
using GitHubReleaseUpdater.Versioning;

namespace ManagedDrive.App.Services;

/// <summary>
/// Adapts <see cref="AppConfiguration.LastUpdateCheckUtc"/> and
/// <see cref="AppConfiguration.SkippedVersion"/> (persisted via <see cref="SettingsStore"/>) to
/// GitHubReleaseUpdater's <see cref="ILastCheckStore"/>, so the package's own daily-throttle and
/// skipped-version logic reads from and writes to the app's settings file instead of keeping
/// separate, hand-rolled state.
/// </summary>
internal sealed class SettingsLastCheckStore(SettingsStore settings) : ILastCheckStore
{
    public Task<DateTimeOffset?> GetLastCheckedAtAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(settings.Load().LastUpdateCheckUtc);

    public Task SetLastCheckedAtAsync(DateTimeOffset checkedAt, CancellationToken cancellationToken = default)
    {
        settings.Save(settings.Load() with { LastUpdateCheckUtc = checkedAt });
        return Task.CompletedTask;
    }

    public Task<SemanticVersion?> GetSkippedVersionAsync(CancellationToken cancellationToken = default)
    {
        var skipped = settings.Load().SkippedVersion;
        return Task.FromResult(skipped != null && SemanticVersion.TryParse(skipped, out var version) ? version : null);
    }

    public Task SetSkippedVersionAsync(SemanticVersion? version, CancellationToken cancellationToken = default)
    {
        settings.Save(settings.Load() with { SkippedVersion = version?.ToString() });
        return Task.CompletedTask;
    }

    public Task ClearSkippedVersionAsync(CancellationToken cancellationToken = default) =>
        SetSkippedVersionAsync(null, cancellationToken);
}
