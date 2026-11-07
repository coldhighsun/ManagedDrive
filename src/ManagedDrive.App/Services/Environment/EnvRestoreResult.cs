namespace ManagedDrive.App.Services;

/// <summary>
/// What a request to restore redirected environment variables did.
/// </summary>
public enum EnvRestoreResult
{
    /// <summary>None of the requested variables point into a RAM disk and nothing was recorded, so nothing was changed.</summary>
    NothingToRestore,

    /// <summary>At least one variable was put back.</summary>
    Restored,

    /// <summary>A variable could not be written.</summary>
    Failed,
}

/// <summary>
/// The outcome of a restore as the user is told about it.
/// </summary>
/// <param name="Result">What the restore did.</param>
/// <param name="ReleasedFrom">
/// One entry per mounted disk that lost presets or redirections because their variables were
/// restored, such as <c>R: (Node.js caches)</c>; empty when no disk changed.
/// </param>
public sealed record EnvRestoreReport(EnvRestoreResult Result, IReadOnlyList<string> ReleasedFrom);
