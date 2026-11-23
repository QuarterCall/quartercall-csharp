namespace QuarterCall;

/// <summary>
/// One account value of one filing: a row of a quarter's accounts file, as
/// <see cref="DataClient.ReadAccountsAsync"/> reads it.
/// </summary>
public sealed record Account
{
    /// <summary>The quarter the filing is for, <c>quarter</c>.</summary>
    public required Quarter Quarter { get; init; }

    /// <summary>The quarter's last day, the date the filing is as of, <c>cycle_date</c>.</summary>
    public required DateOnly CycleDate { get; init; }

    /// <summary>The charter number of the credit union that filed, <c>cu_number</c>.</summary>
    public required int CuNumber { get; init; }

    /// <summary>The filing the value is from, <c>submission_id</c>.</summary>
    public required long SubmissionId { get; init; }

    /// <summary>The version of the filing, <c>version</c>.</summary>
    public required short Version { get; init; }

    /// <summary>The Call Report account, <c>account_code</c>, such as <c>A010</c>, as filed.</summary>
    public required string AccountCode { get; init; }

    /// <summary>
    /// The value filed for the account, <c>account_value</c>, exact to its eight decimal places. Null where the
    /// filing gives no number, which is not zero.
    /// </summary>
    public decimal? AccountValue { get; init; }

    /// <summary>When the host took the filing in, in UTC, <c>ingested_at</c>.</summary>
    public required DateTimeOffset IngestedAt { get; init; }

    /// <summary>
    /// The institution that filed, <c>cuid</c>, which follows it across charter conversions; null where the file
    /// gives none.
    /// </summary>
    public int? Cuid { get; init; }
}
