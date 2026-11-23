namespace QuarterCall;

/// <summary>
/// Which rows <see cref="DataClient.ReadAccountTextAsync"/> returns: those whose values are in every set given. A
/// null set does not filter; an empty set selects no row. The sets are copied when the read is called, so a change
/// to a collection afterwards does not change that read.
/// </summary>
public sealed record AccountTextFilter
{
    /// <summary>
    /// The account codes to return, matched to <c>account_code</c> exactly, case included; each neither null nor
    /// empty.
    /// </summary>
    public IReadOnlyCollection<string>? Codes { get; init; }

    /// <summary>The charter numbers to return, matched to <c>cu_number</c>; each 1 or more.</summary>
    public IReadOnlyCollection<int>? CuNumbers { get; init; }
}
