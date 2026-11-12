using System.Diagnostics.CodeAnalysis;

namespace ManagedDrive.Core.Mounting;

/// <summary>
/// The outcome of applying options to a mounted disk asynchronously: either what the apply did or
/// why it was refused.
/// </summary>
/// <param name="Change">What the apply did, or <c>null</c> when it was refused.</param>
/// <param name="Error">Why the apply was refused, or <c>null</c> when it was applied.</param>
public sealed record OptionsApplyResult(OptionsChange? Change, string? Error)
{
    /// <summary>
    /// Gets a value indicating whether the options were applied.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Change))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool Applied => Change is not null;

    /// <summary>
    /// Creates the result of an apply that went through.
    /// </summary>
    /// <param name="change">What the apply did.</param>
    /// <returns>The result.</returns>
    public static OptionsApplyResult Succeeded(OptionsChange change) => new(change, null);

    /// <summary>
    /// Creates the result of an apply that was refused.
    /// </summary>
    /// <param name="error">Why it was refused.</param>
    /// <returns>The result.</returns>
    public static OptionsApplyResult Refused(string error) => new(null, error);
}
