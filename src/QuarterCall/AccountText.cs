namespace QuarterCall;

/// <summary>
/// One value of an account that is not a number, as filed: a row of a quarter's accounts-text file, as
/// <see cref="DataClient.ReadAccountTextAsync"/> reads it. The matching <see cref="Account"/> has no value.
/// </summary>
public sealed record AccountText
{
    /// <summary>The quarter the filing is for, <c>quarter</c>.</summary>
    public required Quarter Quarter { get; init; }

    /// <summary>The quarter's last day, the date the filing is as of, <c>cycle_date</c>.</summary>
    public required DateOnly CycleDate { get; init; }

    /// <summary>The charter number of the credit union that filed, <c>cu_number</c>.</summary>
    public required int CuNumber { get; init; }

    /// <summary>The filing the value is from, <c>submission_id</c>.</summary>
    public required long SubmissionId { get; init; }

    /// <summary>The Call Report account, <c>account_code</c>, such as <c>A700</c>, as filed.</summary>
    public required string AccountCode { get; init; }

    /// <summary>
    /// The value exactly as filed, <c>account_text</c>, such as <c>Well Capitalized</c>: not trimmed, parsed or
    /// otherwise changed here.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>When the host took the filing in, in UTC, <c>ingested_at</c>.</summary>
    public required DateTimeOffset IngestedAt { get; init; }
}
