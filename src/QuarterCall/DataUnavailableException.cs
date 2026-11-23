namespace QuarterCall;

/// <summary>The quarter, table or file asked for is not published.</summary>
public sealed class DataUnavailableException : QuarterCallException
{
    /// <summary>Creates the exception with a message for the reader.</summary>
    /// <param name="message">What was found, and what to do.</param>
    public DataUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message for the reader and the exception that caused it.</summary>
    /// <param name="message">What was found, and what to do.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public DataUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
