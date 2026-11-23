using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuarterCall.Internal;

/// <summary>The one member of the manifest read before the rest: its <c>schema_version</c>.</summary>
internal sealed record ManifestHeader
{
    // Any JSON value, so that a version which is not a string is refused as a version, not as malformed JSON.
    // Undefined when the key is absent.
    [JsonPropertyName("schema_version")]
    public JsonElement SchemaVersion { get; init; }
}

/// <summary>The members of the manifest the client reads. Every other member is skipped.</summary>
internal sealed record ManifestDocument
{
    // Absent means none, so the initialiser stands; a null or a value that is not an array is malformed. A set
    // accessor: through an init accessor, the generated reader replaced the initialiser with null for an absent
    // key. An element may still be null: the serializer does not check the nullability of an element.
    [JsonPropertyName("quarters")]
    public IReadOnlyList<ManifestQuarter?> Quarters { get; set; } = [];

    [JsonPropertyName("index")]
    public ManifestIndex? Index { get; init; }
}

/// <summary>An entry of the manifest's <c>quarters[]</c>, its members as published.</summary>
internal sealed record ManifestQuarter
{
    [JsonPropertyName("quarter")]
    public required Quarter Quarter { get; init; }

    [JsonPropertyName("cycle_date")]
    public DateOnly? CycleDate { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("dormant_since")]
    public DateOnly? DormantSince { get; init; }

    [JsonPropertyName("submission_count")]
    public int? SubmissionCount { get; init; }

    [JsonPropertyName("cu_count")]
    public int? CuCount { get; init; }

    [JsonPropertyName("counts_at")]
    public DateTimeOffset? CountsAt { get; init; }

    [JsonPropertyName("accounts_path")]
    public string? AccountsPath { get; init; }

    [JsonPropertyName("accounts_sha256")]
    public string? AccountsSha256 { get; init; }

    [JsonPropertyName("accounts_bytes")]
    public long? AccountsBytes { get; init; }

    [JsonPropertyName("accounts_text_path")]
    public string? AccountsTextPath { get; init; }

    [JsonPropertyName("accounts_text_sha256")]
    public string? AccountsTextSha256 { get; init; }

    [JsonPropertyName("accounts_text_bytes")]
    public long? AccountsTextBytes { get; init; }

    [JsonPropertyName("profiles_path")]
    public string? ProfilesPath { get; init; }

    [JsonPropertyName("profiles_sha256")]
    public string? ProfilesSha256 { get; init; }

    [JsonPropertyName("profiles_bytes")]
    public long? ProfilesBytes { get; init; }

    [JsonPropertyName("profile_rows")]
    public int? ProfileRows { get; init; }

    [JsonPropertyName("profiles_missing")]
    public int? ProfilesMissing { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("quorum")]
    public ManifestQuorum? Quorum { get; init; }
}

/// <summary>A quarter's <c>quorum</c> object, its members as published.</summary>
internal sealed record ManifestQuorum
{
    [JsonPropertyName("measurement")]
    public string? Measurement { get; init; }

    [JsonPropertyName("measured_at")]
    public DateTimeOffset? MeasuredAt { get; init; }

    [JsonPropertyName("due_date")]
    public DateOnly? DueDate { get; init; }

    [JsonPropertyName("expected_basis")]
    public string? ExpectedBasis { get; init; }

    [JsonPropertyName("weights_from")]
    public Quarter? WeightsFrom { get; init; }

    [JsonPropertyName("expected")]
    public int? Expected { get; init; }

    [JsonPropertyName("filed")]
    public int? Filed { get; init; }

    [JsonPropertyName("filed_unweighted")]
    public int? FiledUnweighted { get; init; }

    [JsonPropertyName("filed_by_charter")]
    public int? FiledByCharter { get; init; }

    [JsonPropertyName("outstanding_count")]
    public int? OutstandingCount { get; init; }

    [JsonPropertyName("excused")]
    public int? Excused { get; init; }

    [JsonPropertyName("assets_expected")]
    public long? AssetsExpected { get; init; }

    [JsonPropertyName("assets_filed")]
    public long? AssetsFiled { get; init; }

    [JsonPropertyName("largest_outstanding_assets")]
    public long? LargestOutstandingAssets { get; init; }

    [JsonPropertyName("coverage")]
    public ManifestCoverage? Coverage { get; init; }

    // Set accessors on the two collections: with init, the generated reader replaced the initialiser with null
    // for an absent key.
    [JsonPropertyName("verdicts")]
    public IReadOnlyDictionary<string, ManifestVerdict?> Verdicts { get; set; } =
        new Dictionary<string, ManifestVerdict?>();

    [JsonPropertyName("materiality")]
    public decimal? Materiality { get; init; }

    [JsonPropertyName("outstanding_material")]
    public IReadOnlyList<ManifestOutstandingFiler?> OutstandingMaterial { get; set; } = [];

    [JsonPropertyName("detail_path")]
    public string? DetailPath { get; init; }

    [JsonPropertyName("detail_sha256")]
    public string? DetailSha256 { get; init; }

    [JsonPropertyName("detail_bytes")]
    public long? DetailBytes { get; init; }
}

/// <summary>A quorum's <c>coverage</c> object.</summary>
internal sealed record ManifestCoverage
{
    [JsonPropertyName("by_count")]
    public decimal? ByCount { get; init; }

    [JsonPropertyName("by_assets")]
    public decimal? ByAssets { get; init; }

    [JsonPropertyName("by_assets_ceiling")]
    public decimal? ByAssetsCeiling { get; init; }

    [JsonPropertyName("largest_outstanding_share")]
    public decimal? LargestOutstandingShare { get; init; }

    [JsonPropertyName("ceiling_basis")]
    public string? CeilingBasis { get; init; }
}

/// <summary>An entry of a quorum's <c>verdicts</c>. Its status is text, so one not known is read, not refused.</summary>
internal sealed record ManifestVerdict
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("reached_at")]
    public DateTimeOffset? ReachedAt { get; init; }

    [JsonPropertyName("reached_at_basis")]
    public string? ReachedAtBasis { get; init; }

    [JsonPropertyName("below_since")]
    public DateTimeOffset? BelowSince { get; init; }
}

/// <summary>An entry of a quorum's <c>outstanding_material</c>.</summary>
internal sealed record ManifestOutstandingFiler
{
    [JsonPropertyName("cuid")]
    public int? Cuid { get; init; }

    [JsonPropertyName("cu_number")]
    public int? CuNumber { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("last_known_assets")]
    public long? LastKnownAssets { get; init; }

    [JsonPropertyName("share")]
    public decimal? Share { get; init; }
}

/// <summary>The manifest's <c>index</c> object: the file that indexes every filing.</summary>
internal sealed record ManifestIndex
{
    [JsonPropertyName("submissions_path")]
    public string? SubmissionsPath { get; init; }

    [JsonPropertyName("submissions_sha256")]
    public string? SubmissionsSha256 { get; init; }

    [JsonPropertyName("submissions_bytes")]
    public long? SubmissionsBytes { get; init; }
}

/// <summary>
/// Reads a time as UTC: one written with an offset is converted, and one written without is taken to be UTC
/// already, so the result never depends on the machine's time zone.
/// </summary>
internal sealed class UtcTimeConverter : JsonConverter<DateTimeOffset>
{
    // ISO 8601: a fraction of a second is optional, and K takes "Z", an offset, or nothing.
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK";

    private const DateTimeStyles Styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (DateTimeOffset.TryParseExact(text, Format, CultureInfo.InvariantCulture, Styles, out var time))
        {
            return time;
        }

        throw new JsonException("A time is an ISO 8601 string such as \"2026-11-22T21:46:36Z\".");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToUniversalTime());
    }
}

/// <summary>
/// Reads the manifest's documents. A member given null where the type does not allow it is an error, as is a
/// required member that is absent; a member the type does not name is skipped. Every time is read as UTC.
/// </summary>
[JsonSourceGenerationOptions(RespectNullableAnnotations = true, Converters = [typeof(UtcTimeConverter)])]
[JsonSerializable(typeof(ManifestHeader))]
[JsonSerializable(typeof(ManifestDocument))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;
