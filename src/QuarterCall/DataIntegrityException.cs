namespace QuarterCall;

/// <summary>
/// What the host published does not match its own description: a document that cannot be read, or a file whose
/// digest or length is not the one published. Most often the data is being republished.
/// </summary>
public sealed class DataIntegrityException : QuarterCallException
{
    /// <summary>Creates the exception with a message for the reader.</summary>
    /// <param name="message">What was found, and what to do.</param>
    public DataIntegrityException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message for the reader and the exception that caused it.</summary>
    /// <param name="message">What was found, and what to do.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public DataIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
