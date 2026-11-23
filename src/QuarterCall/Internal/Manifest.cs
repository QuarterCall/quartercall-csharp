using System.Text.Json.Serialization;

namespace QuarterCall.Internal;

/// <summary>The one member of the manifest read before the rest: its <c>schema_version</c>.</summary>
internal sealed record ManifestHeader
{
    [JsonPropertyName("schema_version")]
    public string? SchemaVersion { get; init; }
}

/// <summary>The members of the manifest the client reads. Every other member is skipped.</summary>
internal sealed record ManifestDocument
{
    // An element of the array may still be null: element nullability is not checked by the serializer.
    [JsonPropertyName("quarters")]
    public required IReadOnlyList<ManifestQuarter?> Quarters { get; init; }

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
/// Reads the manifest's documents. A member given null where the type does not allow it is an error, as is a
/// required member that is absent; a member the type does not name is skipped.
/// </summary>
[JsonSourceGenerationOptions(RespectNullableAnnotations = true)]
[JsonSerializable(typeof(ManifestHeader))]
[JsonSerializable(typeof(ManifestDocument))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;
