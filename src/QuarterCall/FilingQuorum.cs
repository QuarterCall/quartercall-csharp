namespace QuarterCall;

/// <summary>
/// How much of the system has filed for a quarter, the <c>quorum</c> object of the quarter's manifest entry. The
/// verdicts are the host's: the package computes none. A member the host leaves out is null: an absent key
/// means none, never zero.
/// </summary>
/// <remarks>
/// The equality a record gives compares <see cref="Verdicts"/> and <see cref="OutstandingMaterial"/> by reference,
/// and each read of the manifest makes new ones. Two quorums read apart are never equal, even with the same values:
/// a quorum is equal only to itself, or to a copy of it made with <c>with</c> that changes nothing else. Compare
/// their members instead.
/// </remarks>
public sealed record FilingQuorum
{
    /// <summary>The name and version of the measurement, <c>measurement</c>.</summary>
    public string? Measurement { get; init; }

    /// <summary>When the quorum was measured, in UTC, <c>measured_at</c>.</summary>
    public DateTimeOffset? MeasuredAt { get; init; }

    /// <summary>The date filings for the quarter are due, <c>due_date</c>.</summary>
    public DateOnly? DueDate { get; init; }

    /// <summary>
    /// How the expected filers were chosen, <c>expected_basis</c>: <c>prior_cycle</c> or <c>partial</c>.
    /// </summary>
    public string? ExpectedBasis { get; init; }

    /// <summary>The quarter whose asset sizes weight the measurement, <c>weights_from</c>.</summary>
    public Quarter? WeightsFrom { get; init; }

    /// <summary>The number of credit unions expected to file, <c>expected</c>.</summary>
    public int? Expected { get; init; }

    /// <summary>The number of expected credit unions that have filed, <c>filed</c>.</summary>
    public int? Filed { get; init; }

    /// <summary>The number of filings that carry no weight, <c>filed_unweighted</c>.</summary>
    public int? FiledUnweighted { get; init; }

    /// <summary>The number of filings matched to an expected filer by charter, <c>filed_by_charter</c>.</summary>
    public int? FiledByCharter { get; init; }

    /// <summary>The number of expected credit unions that have not filed, <c>outstanding_count</c>.</summary>
    public int? OutstandingCount { get; init; }

    /// <summary>The number of credit unions excused from filing, <c>excused</c>.</summary>
    public int? Excused { get; init; }

    /// <summary>The assets, in whole dollars, of the expected credit unions, <c>assets_expected</c>.</summary>
    public long? AssetsExpected { get; init; }

    /// <summary>The assets, in whole dollars, of the credit unions that have filed, <c>assets_filed</c>.</summary>
    public long? AssetsFiled { get; init; }

    /// <summary>
    /// The assets, in whole dollars, of the largest credit union yet to file, <c>largest_outstanding_assets</c>.
    /// </summary>
    public long? LargestOutstandingAssets { get; init; }

    /// <summary>The share of expected credit unions that have filed, <c>coverage.by_count</c>.</summary>
    public decimal? CoverageByCount { get; init; }

    /// <summary>The share of expected assets that have filed, <c>coverage.by_assets</c>.</summary>
    public decimal? CoverageByAssets { get; init; }

    /// <summary>The highest the share of assets can reach, <c>coverage.by_assets_ceiling</c>.</summary>
    public decimal? CoverageByAssetsCeiling { get; init; }

    /// <summary>
    /// The largest outstanding filer's share of expected assets, <c>coverage.largest_outstanding_share</c>.
    /// </summary>
    public decimal? CoverageLargestOutstandingShare { get; init; }

    /// <summary>
    /// The share of assets at or above which an outstanding filer is material, <c>materiality</c>.
    /// </summary>
    public decimal? Materiality { get; init; }

    /// <summary>What the ceiling of the asset coverage rests on, <c>coverage.ceiling_basis</c>.</summary>
    public string? CoverageCeilingBasis { get; init; }

    /// <summary>The path of the quorum's detail file, <c>detail_path</c>, relative to the host.</summary>
    public string? DetailPath { get; init; }

    /// <summary>The SHA-256 digest of the detail file, in lowercase hex, <c>detail_sha256</c>.</summary>
    public string? DetailSha256 { get; init; }

    /// <summary>The length of the detail file in bytes, <c>detail_bytes</c>.</summary>
    public long? DetailBytes { get; init; }

    /// <summary>
    /// The host's verdict for each policy it names, keyed by policy name such as <c>floor/1</c>, <c>verdicts</c>.
    /// Empty when the key is absent.
    /// </summary>
    public required IReadOnlyDictionary<string, QuorumVerdict> Verdicts { get; init; }

    /// <summary>
    /// The outstanding filers large enough to matter, <c>outstanding_material</c>. Empty when the key is absent.
    /// </summary>
    public required IReadOnlyList<OutstandingFiler> OutstandingMaterial { get; init; }

    /// <summary>
    /// Whether the host's verdict for <paramref name="policy"/> is <see cref="QuorumStatus.Reached"/>. False for
    /// any other status, and for a policy the quorum does not name.
    /// </summary>
    /// <param name="policy">A policy name, such as <c>floor/1</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    public bool HasReached(string policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return Verdicts.TryGetValue(policy, out var verdict) && verdict.Status == QuorumStatus.Reached;
    }
}

/// <summary>The host's verdict on one filing-quorum policy, an entry of <c>verdicts</c>.</summary>
public sealed record QuorumVerdict
{
    /// <summary>The verdict, <c>status</c>; <see cref="QuorumStatus.Unknown"/> for one not recognised.</summary>
    public required QuorumStatus Status { get; init; }

    /// <summary>When the policy was first observed reached, in UTC, <c>reached_at</c>.</summary>
    public DateTimeOffset? ReachedAt { get; init; }

    /// <summary>How <see cref="ReachedAt"/> was established, <c>reached_at_basis</c>.</summary>
    public string? ReachedAtBasis { get; init; }

    /// <summary>When the policy was first observed below its terms, in UTC, <c>below_since</c>.</summary>
    public DateTimeOffset? BelowSince { get; init; }
}

/// <summary>A credit union yet to file that is large enough to matter, an entry of <c>outstanding_material</c>.</summary>
public sealed record OutstandingFiler
{
    /// <summary>The credit union's key, <c>cuid</c>.</summary>
    public int? Cuid { get; init; }

    /// <summary>The credit union's charter number, <c>cu_number</c>.</summary>
    public int? CuNumber { get; init; }

    /// <summary>The credit union's name, <c>name</c>.</summary>
    public string? Name { get; init; }

    /// <summary>The assets, in whole dollars, last reported by the credit union, <c>last_known_assets</c>.</summary>
    public long? LastKnownAssets { get; init; }

    /// <summary>The credit union's share of expected assets, <c>share</c>.</summary>
    public decimal? Share { get; init; }
}
