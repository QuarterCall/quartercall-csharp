namespace QuarterCall;

/// <summary>
/// How likely a quarter's files are to change, the manifest's <c>status</c>. Neither value is a promise: an
/// amendment can arrive for a quarter of any age.
/// </summary>
public enum QuarterStatus
{
    /// <summary>
    /// <c>open</c>: filings for the quarter are still arriving, and its files may change between runs. A status
    /// this version does not know reads as open.
    /// </summary>
    Open,

    /// <summary>
    /// <c>dormant</c>: no filing for the quarter has been validated for more than a year; its files change only if
    /// a late amendment arrives, which makes the quarter open again.
    /// </summary>
    Dormant,
}
