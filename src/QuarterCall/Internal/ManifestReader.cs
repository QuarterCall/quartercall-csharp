using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace QuarterCall.Internal;

/// <summary>What one reading of the manifest says: its readable quarters, newest first, and its index file.</summary>
/// <param name="Quarters">The entries of <c>quarters[]</c> that have an <c>accounts_path</c>, newest first.</param>
/// <param name="SubmissionsPath"><c>index.submissions_path</c>.</param>
/// <param name="SubmissionsSha256"><c>index.submissions_sha256</c>.</param>
/// <param name="SubmissionsBytes"><c>index.submissions_bytes</c>.</param>
internal sealed record ManifestSnapshot(
    IReadOnlyList<PublishedQuarter> Quarters,
    string? SubmissionsPath,
    string? SubmissionsSha256,
    long? SubmissionsBytes);

/// <summary>Reads the host's manifest, <c>index/manifest.json</c>, afresh at each call.</summary>
internal sealed class ManifestReader
{
    /// <summary>Where the manifest is, relative to the host's base address.</summary>
    public const string ManifestPath = "index/manifest.json";

    // The manifest major this package reads; any minor of it is read on.
    private const int SupportedMajor = 1;

    private const string DormantStatus = "dormant";

    private readonly HostClient _host;

    /// <summary>Creates a reader of the manifest of <paramref name="host"/>.</summary>
    public ManifestReader(HostClient host)
    {
        _host = host;
    }

    /// <summary>Reads the manifest.</summary>
    /// <exception cref="UnsupportedSchemaException">
    /// The manifest is of a major version this package does not read.
    /// </exception>
    /// <exception cref="DataIntegrityException">
    /// The manifest is not JSON, a required member is absent, or a member is not of its published type.
    /// </exception>
    public async Task<ManifestSnapshot> ReadAsync(CancellationToken ct)
    {
        var uri = _host.Resolve(ManifestPath);
        var body = await _host
            .ReadAsync(uri, static (response, token) => response.Content.ReadAsByteArrayAsync(token), ct)
            .ConfigureAwait(false);

        // The version is read first, so a manifest of another major is refused as that, whatever else it holds.
        var header = Deserialize(body, ManifestJsonContext.Default.ManifestHeader, uri);
        SchemaVersion.Require(header.SchemaVersion, SupportedMajor, $"the manifest at {uri}");
        var document = Deserialize(body, ManifestJsonContext.Default.ManifestDocument, uri);

        var quarters = new List<PublishedQuarter>(document.Quarters.Count);
        foreach (var entry in document.Quarters)
        {
            if (entry is null)
            {
                throw Malformed(uri, inner: null);
            }

            if (entry.AccountsPath is not null)
            {
                quarters.Add(Publish(entry, entry.AccountsPath, uri));
            }
        }

        quarters.Sort(static (a, b) => b.Quarter.CompareTo(a.Quarter));
        var index = document.Index;
        return new ManifestSnapshot(
            quarters.AsReadOnly(), index?.SubmissionsPath, index?.SubmissionsSha256, index?.SubmissionsBytes);
    }

    private static T Deserialize<T>(byte[] body, JsonTypeInfo<T> type, Uri uri)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, type) ?? throw Malformed(uri, inner: null);
        }
        catch (JsonException e)
        {
            throw Malformed(uri, e);
        }
    }

    private static DataIntegrityException Malformed(Uri uri, JsonException? inner)
    {
        var message = $"The manifest at {uri} cannot be read: it is not valid JSON, or one of its members is "
            + "missing or of the wrong type. It may be in the middle of being republished; try again in a few "
            + "minutes. If it stays this way, check that the data address points to a QuarterCall data host.";
        return inner is null ? new DataIntegrityException(message) : new DataIntegrityException(message, inner);
    }

    private static PublishedQuarter Publish(ManifestQuarter entry, string accountsPath, Uri uri)
    {
        return new PublishedQuarter
        {
            Quarter = entry.Quarter,
            CycleDate = entry.CycleDate ?? entry.Quarter.CycleDate,
            // Only "dormant" is dormant: a status this version does not know is open.
            Status = entry.Status == DormantStatus ? QuarterStatus.Dormant : QuarterStatus.Open,
            AccountsPath = accountsPath,
            DormantSince = entry.DormantSince,
            SubmissionCount = entry.SubmissionCount,
            CuCount = entry.CuCount,
            CountsAt = entry.CountsAt,
            AccountsSha256 = entry.AccountsSha256,
            AccountsBytes = entry.AccountsBytes,
            AccountsTextPath = entry.AccountsTextPath,
            AccountsTextSha256 = entry.AccountsTextSha256,
            AccountsTextBytes = entry.AccountsTextBytes,
            ProfilesPath = entry.ProfilesPath,
            ProfilesSha256 = entry.ProfilesSha256,
            ProfilesBytes = entry.ProfilesBytes,
            ProfileRows = entry.ProfileRows,
            ProfilesMissing = entry.ProfilesMissing,
            UpdatedAt = entry.UpdatedAt,
            Quorum = entry.Quorum is null ? null : PublishQuorum(entry.Quorum, uri),
        };
    }

    private static FilingQuorum PublishQuorum(ManifestQuorum quorum, Uri uri)
    {
        var verdicts = new Dictionary<string, QuorumVerdict>(quorum.Verdicts.Count);
        foreach (var (policy, verdict) in quorum.Verdicts)
        {
            verdicts[policy] = PublishVerdict(verdict ?? throw Malformed(uri, inner: null));
        }

        var outstanding = new List<OutstandingFiler>(quorum.OutstandingMaterial.Count);
        foreach (var filer in quorum.OutstandingMaterial)
        {
            outstanding.Add(PublishFiler(filer ?? throw Malformed(uri, inner: null)));
        }

        var coverage = quorum.Coverage;
        return new FilingQuorum
        {
            Measurement = quorum.Measurement,
            MeasuredAt = quorum.MeasuredAt,
            DueDate = quorum.DueDate,
            ExpectedBasis = quorum.ExpectedBasis,
            WeightsFrom = quorum.WeightsFrom,
            Expected = quorum.Expected,
            Filed = quorum.Filed,
            FiledUnweighted = quorum.FiledUnweighted,
            FiledByCharter = quorum.FiledByCharter,
            OutstandingCount = quorum.OutstandingCount,
            Excused = quorum.Excused,
            AssetsExpected = quorum.AssetsExpected,
            AssetsFiled = quorum.AssetsFiled,
            LargestOutstandingAssets = quorum.LargestOutstandingAssets,
            CoverageByCount = coverage?.ByCount,
            CoverageByAssets = coverage?.ByAssets,
            CoverageByAssetsCeiling = coverage?.ByAssetsCeiling,
            CoverageLargestOutstandingShare = coverage?.LargestOutstandingShare,
            CoverageCeilingBasis = coverage?.CeilingBasis,
            Materiality = quorum.Materiality,
            DetailPath = quorum.DetailPath,
            DetailSha256 = quorum.DetailSha256,
            DetailBytes = quorum.DetailBytes,
            Verdicts = verdicts.AsReadOnly(),
            OutstandingMaterial = outstanding.AsReadOnly(),
        };
    }

    // The host's word is passed on: the status is matched to a name, never worked out from the counts.
    private static QuorumVerdict PublishVerdict(ManifestVerdict verdict)
    {
        return new QuorumVerdict
        {
            Status = verdict.Status switch
            {
                "reached" => QuorumStatus.Reached,
                "below" => QuorumStatus.Below,
                "indeterminate" => QuorumStatus.Indeterminate,
                _ => QuorumStatus.Unknown,
            },
            ReachedAt = verdict.ReachedAt,
            ReachedAtBasis = verdict.ReachedAtBasis,
            BelowSince = verdict.BelowSince,
        };
    }

    private static OutstandingFiler PublishFiler(ManifestOutstandingFiler filer)
    {
        return new OutstandingFiler
        {
            Cuid = filer.Cuid,
            CuNumber = filer.CuNumber,
            Name = filer.Name,
            LastKnownAssets = filer.LastKnownAssets,
            Share = filer.Share,
        };
    }
}
