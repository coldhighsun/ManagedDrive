namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Qualitative strength rating produced by <see cref="PasswordStrengthEstimator.Estimate"/>.
/// </summary>
public enum PasswordStrength
{
    /// <summary>
    /// The password is easy to guess.
    /// </summary>
    Weak,
    /// <summary>
    /// The password offers moderate resistance to guessing.
    /// </summary>
    Medium,
    /// <summary>
    /// The password is hard to guess.
    /// </summary>
    Strong,
}

/// <summary>
/// Estimates password strength for the <c>CreateDiskDialog</c> encryption password fields, purely
/// as a UI hint — it does not affect <c>CreateDiskOptionsBuilder</c>'s length-based validation.
/// </summary>
public static class PasswordStrengthEstimator
{
    /// <summary>
    /// Estimates the strength of <paramref name="password"/> from its length and the variety of
    /// character classes it uses (lowercase, uppercase, digit, other/symbol).
    /// </summary>
    public static PasswordStrength Estimate(string password)
    {
        var hasLower = false;
        var hasUpper = false;
        var hasDigit = false;
        var hasOther = false;

        foreach (var c in password)
        {
            if (char.IsLower(c))
            {
                hasLower = true;
            }
            else if (char.IsUpper(c))
            {
                hasUpper = true;
            }
            else if (char.IsDigit(c))
            {
                hasDigit = true;
            }
            else
            {
                hasOther = true;
            }
        }

        var classCount = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasOther ? 1 : 0);

        if (password.Length >= 12 && classCount >= 3)
        {
            return PasswordStrength.Strong;
        }

        if (password.Length >= 8 && classCount >= 2)
        {
            return PasswordStrength.Medium;
        }

        return PasswordStrength.Weak;
    }
}
