namespace QuarterCall;

/// <summary>
/// The base of the exceptions QuarterCall throws about what a host published: data that is not there, data that
/// does not match its description, or data in a form this version of the package does not read.
/// </summary>
public class QuarterCallException : Exception
{
    /// <summary>Creates the exception with a message for the reader.</summary>
    /// <param name="message">What was found, and what to do.</param>
    public QuarterCallException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message for the reader and the exception that caused it.</summary>
    /// <param name="message">What was found, and what to do.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public QuarterCallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
