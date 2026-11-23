namespace QuarterCall;

/// <summary>A file saved from the host, after it matched the manifest's description of it.</summary>
public sealed record PublishedFile
{
    /// <summary>
    /// The address the file was read from: the data base address joined with the manifest's path for the file. That
    /// is <c>accounts_path</c>, <c>accounts_text_path</c> or <c>profiles_path</c>, each a directory, followed by
    /// <c>data.parquet</c>; or <c>quorum.detail_path</c> or <c>index.submissions_path</c>, each the file itself.
    /// </summary>
    public required Uri Uri { get; init; }

    /// <summary>
    /// The file's SHA-256 digest in lowercase hex, as the manifest names it: <c>accounts_sha256</c>,
    /// <c>accounts_text_sha256</c>, <c>profiles_sha256</c>, <c>quorum.detail_sha256</c> or
    /// <c>index.submissions_sha256</c>.
    /// </summary>
    public required string Sha256 { get; init; }

    /// <summary>
    /// The file's length in bytes, as the manifest names it: <c>accounts_bytes</c>, <c>accounts_text_bytes</c>,
    /// <c>profiles_bytes</c>, <c>quorum.detail_bytes</c> or <c>index.submissions_bytes</c>.
    /// </summary>
    public required long Bytes { get; init; }

    /// <summary>Where the file was saved, as the caller gave it.</summary>
    public required string Path { get; init; }
}
