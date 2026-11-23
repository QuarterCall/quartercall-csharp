namespace QuarterCall;

/// <summary>
/// A readable quarter, as the manifest (<c>index/manifest.json</c>) lists it in <c>quarters[]</c>. A member the
/// manifest leaves out is null: an absent key means none, never zero.
/// </summary>
public sealed record PublishedQuarter
{
    /// <summary>The quarter, <c>quarter</c>.</summary>
    public required Quarter Quarter { get; init; }

    /// <summary>The quarter's last day, <c>cycle_date</c>; the quarter's own last day when the key is absent.</summary>
    public DateOnly CycleDate { get; init; }

    /// <summary>How likely the quarter's files are to change, <c>status</c>.</summary>
    public QuarterStatus Status { get; init; }

    /// <summary>The directory of the quarter's accounts file, <c>accounts_path</c>, relative to the host.</summary>
    public required string AccountsPath { get; init; }

    /// <summary>
    /// For a dormant quarter, the date of the newest filing validated for it, <c>dormant_since</c>: when activity
    /// stopped.
    /// </summary>
    public DateOnly? DormantSince { get; init; }

    /// <summary>The number of filings held for the quarter, each amendment counted, <c>submission_count</c>.</summary>
    public int? SubmissionCount { get; init; }

    /// <summary>The number of distinct credit unions holding a filing for the quarter, <c>cu_count</c>.</summary>
    public int? CuCount { get; init; }

    /// <summary>
    /// When <see cref="SubmissionCount"/> and <see cref="CuCount"/> were counted, in UTC, <c>counts_at</c>.
    /// </summary>
    public DateTimeOffset? CountsAt { get; init; }

    /// <summary>The SHA-256 digest of the accounts file, in lowercase hex, <c>accounts_sha256</c>.</summary>
    public string? AccountsSha256 { get; init; }

    /// <summary>The length of the accounts file in bytes, <c>accounts_bytes</c>.</summary>
    public long? AccountsBytes { get; init; }

    /// <summary>
    /// The directory of the quarter's account text file, <c>accounts_text_path</c>; absent when the quarter has no
    /// text values.
    /// </summary>
    public string? AccountsTextPath { get; init; }

    /// <summary>The SHA-256 digest of the account text file, in lowercase hex, <c>accounts_text_sha256</c>.</summary>
    public string? AccountsTextSha256 { get; init; }

    /// <summary>The length of the account text file in bytes, <c>accounts_text_bytes</c>.</summary>
    public long? AccountsTextBytes { get; init; }

    /// <summary>The directory of the quarter's profiles file, <c>profiles_path</c>.</summary>
    public string? ProfilesPath { get; init; }

    /// <summary>The SHA-256 digest of the profiles file, in lowercase hex, <c>profiles_sha256</c>.</summary>
    public string? ProfilesSha256 { get; init; }

    /// <summary>The length of the profiles file in bytes, <c>profiles_bytes</c>.</summary>
    public long? ProfilesBytes { get; init; }

    /// <summary>The number of rows in the profiles file, <c>profile_rows</c>.</summary>
    public int? ProfileRows { get; init; }

    /// <summary>
    /// The number of filings in the accounts file with no row in the profiles file, <c>profiles_missing</c>.
    /// </summary>
    public int? ProfilesMissing { get; init; }

    /// <summary>When the quarter's files were last built, in UTC, <c>updated_at</c>.</summary>
    public DateTimeOffset? UpdatedAt { get; init; }
}
