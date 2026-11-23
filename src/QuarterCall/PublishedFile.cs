namespace QuarterCall;

/// <summary>A file saved from the host, after it matched the manifest's description of it.</summary>
/// <param name="Uri">The address the file was read from.</param>
/// <param name="Sha256">
/// The file's SHA-256 digest in lowercase hex, as the manifest names it: <c>accounts_sha256</c>,
/// <c>accounts_text_sha256</c>, <c>profiles_sha256</c>, <c>quorum.detail_sha256</c> or
/// <c>index.submissions_sha256</c>.
/// </param>
/// <param name="Bytes">
/// The file's length in bytes, as the manifest names it: <c>accounts_bytes</c>, <c>accounts_text_bytes</c>,
/// <c>profiles_bytes</c>, <c>quorum.detail_bytes</c> or <c>index.submissions_bytes</c>.
/// </param>
/// <param name="Path">Where the file was saved, as the caller gave it.</param>
public sealed record PublishedFile(Uri Uri, string Sha256, long Bytes, string Path);
