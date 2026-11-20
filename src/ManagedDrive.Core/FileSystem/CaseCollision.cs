namespace ManagedDrive.Core.FileSystem;

/// <summary>
/// Creates and recognizes the <see cref="InvalidDataException"/> raised when contents loaded into a
/// case-insensitive disk hold two names that differ only by case. The exception type stays
/// <see cref="InvalidDataException"/> (it is sealed, and existing handlers catch it); a marker in
/// <see cref="Exception.Data"/> lets callers tell a name collision from a corrupt file without
/// matching on the message text.
/// </summary>
public static class CaseCollision
{
    /// <summary>
    /// The <see cref="Exception.Data"/> key that marks a case-collision exception.
    /// </summary>
    private const string DataKey = "ManagedDrive.CaseCollision";

    /// <summary>
    /// Creates a case-collision exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    /// <returns>The exception, marked so that <see cref="IsCaseCollision"/> recognizes it.</returns>
    public static InvalidDataException CreateException(string message, Exception? innerException = null)
    {
        var exception = new InvalidDataException(message, innerException);
        exception.Data[DataKey] = true;
        return exception;
    }

    /// <summary>
    /// Tells whether an exception was created by <see cref="CreateException"/>.
    /// </summary>
    /// <param name="exception">The exception to test.</param>
    /// <returns><c>true</c> for a case-collision exception.</returns>
    public static bool IsCaseCollision(Exception exception) =>
        exception is InvalidDataException && exception.Data.Contains(DataKey);
}
