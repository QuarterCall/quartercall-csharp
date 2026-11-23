namespace QuarterCall;

/// <summary>
/// What the host says of a filing-quorum policy for a quarter, a verdict's <c>status</c>. The package only
/// reports it; it never works a status out from the counts.
/// </summary>
public enum QuorumStatus
{
    /// <summary>A status this version does not recognise. It is never read as reached.</summary>
    Unknown,

    /// <summary><c>reached</c>: the policy's terms are met.</summary>
    Reached,

    /// <summary><c>below</c>: the policy's terms are not met.</summary>
    Below,

    /// <summary><c>indeterminate</c>: the host could not say whether the terms are met.</summary>
    Indeterminate,
}
