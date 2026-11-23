namespace QuarterCall;

/// <summary>
/// The host publishes a document in a schema version this version of QuarterCall does not read. Upgrading the
/// package is the remedy.
/// </summary>
public sealed class UnsupportedSchemaException : QuarterCallException
{
    /// <summary>Creates the exception with a message for the reader.</summary>
    /// <param name="message">What was found, and what to do.</param>
    public UnsupportedSchemaException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message for the reader and the exception that caused it.</summary>
    /// <param name="message">What was found, and what to do.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public UnsupportedSchemaException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
