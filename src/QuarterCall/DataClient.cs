using QuarterCall.Internal;

namespace QuarterCall;

/// <summary>
/// Reads the evidence: the credit unions' Call Report filings as the data host publishes them. Reach it through
/// <see cref="QuarterCallClient.Data"/>. Its methods are safe to call concurrently.
/// </summary>
public sealed class DataClient
{
    private readonly ManifestReader _manifest;
    private readonly EvidenceResolver _evidence;

    internal DataClient(HostClient host, string? cacheDirectory)
    {
        _manifest = new ManifestReader(host);
        _evidence = new EvidenceResolver(_manifest, new VerifiedFileStore(host, cacheDirectory), host);
    }

    /// <summary>
    /// Lists the quarters that can be read, newest first, from the host's manifest. A quarter the host holds but
    /// has not yet published, one without an <c>accounts_path</c>, is not listed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The readable quarters, newest first; empty when the host has published none.</returns>
    /// <exception cref="UnsupportedSchemaException">
    /// The manifest is of a schema version this version of the package does not read.
    /// </exception>
    /// <exception cref="DataIntegrityException">The manifest is not in its published form.</exception>
    /// <exception cref="HttpRequestException">The request failed, or the host answered with an error.</exception>
    /// <exception cref="TaskCanceledException">
    /// The request took longer than <see cref="QuarterCallOptions.Timeout"/>; a <see cref="TimeoutException"/> is
    /// inside.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<IReadOnlyList<PublishedQuarter>> GetQuartersAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _manifest.ReadAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Quarters;
    }

    /// <summary>
    /// Saves one of a quarter's published files at <paramref name="path"/>. The file is verified against the
    /// manifest before it is written there: its length and its SHA-256 digest must be the ones the manifest names. A
    /// file that does not match is read again from a fresh manifest, up to three attempts in all, in case the host
    /// is republishing it. A file already at <paramref name="path"/> is replaced only once the new one is verified.
    /// When <see cref="QuarterCallOptions.CacheDirectory"/> is set, the verified file is kept there, named by its
    /// digest, and a cached file that still matches is copied from there rather than downloaded again.
    /// </summary>
    /// <param name="quarter">The quarter.</param>
    /// <param name="table">Which of the quarter's files to save.</param>
    /// <param name="path">The file to write. Its directory is created if missing.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The file's address, digest and length, as the manifest names them, and where it was saved.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="quarter"/> is the default quarter, or <paramref name="path"/> is null, empty, blank or ends
    /// in a directory separator.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="table"/> is not one of its named values.
    /// </exception>
    /// <exception cref="DataUnavailableException">
    /// The manifest does not list the quarter as readable, or names no file for the table in it: a quarter without
    /// <c>profiles_path</c>, without <c>quorum.detail_path</c>, or without text values.
    /// </exception>
    /// <exception cref="DataIntegrityException">
    /// The file did not match the manifest in three attempts, most often because the quarter is being republished;
    /// or the manifest names no usable digest or length for the file, or a path that is not beneath the data
    /// address; or the manifest is not in its published form.
    /// </exception>
    /// <exception cref="UnsupportedSchemaException">
    /// The manifest is of a schema version this version of the package does not read.
    /// </exception>
    /// <exception cref="HttpRequestException">The request failed, or the host answered with an error.</exception>
    /// <exception cref="TaskCanceledException">
    /// A request took longer than <see cref="QuarterCallOptions.Timeout"/>; a <see cref="TimeoutException"/> is
    /// inside.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="IOException">
    /// The file could not be written at <paramref name="path"/> or in the cache.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The process may not write at <paramref name="path"/> or in the cache.
    /// </exception>
    public Task<PublishedFile> DownloadAsync(
        Quarter quarter, EvidenceTable table, string path, CancellationToken cancellationToken = default)
    {
        quarter.ThrowIfDefault(nameof(quarter));
        if (!Enum.IsDefined(table))
        {
            throw new ArgumentOutOfRangeException(nameof(table), table, "The table is not one of EvidenceTable's.");
        }

        ThrowIfNotAFile(path);
        return _evidence.SaveAsync(quarter, table, path, cancellationToken);
    }

    /// <summary>
    /// Saves the index of every filing the host holds, the manifest's <c>index.submissions_path</c>, at
    /// <paramref name="path"/>. The file is verified against the manifest before it is written there, as
    /// <see cref="DownloadAsync"/> verifies a quarter's file, and a cache is used in the same way when
    /// <see cref="QuarterCallOptions.CacheDirectory"/> is set.
    /// </summary>
    /// <param name="path">The file to write. Its directory is created if missing.</param>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>The file's address, digest and length, as the manifest names them, and where it was saved.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is null, empty, blank or ends in a directory separator.
    /// </exception>
    /// <exception cref="DataUnavailableException">The manifest names no <c>index.submissions_path</c>.</exception>
    /// <exception cref="DataIntegrityException">
    /// The file did not match the manifest in three attempts, most often because the index is being republished;
    /// or the manifest names no usable digest or length for the file, or a path that is not beneath the data
    /// address; or the manifest is not in its published form.
    /// </exception>
    /// <exception cref="UnsupportedSchemaException">
    /// The manifest is of a schema version this version of the package does not read.
    /// </exception>
    /// <exception cref="HttpRequestException">The request failed, or the host answered with an error.</exception>
    /// <exception cref="TaskCanceledException">
    /// A request took longer than <see cref="QuarterCallOptions.Timeout"/>; a <see cref="TimeoutException"/> is
    /// inside.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="IOException">
    /// The file could not be written at <paramref name="path"/> or in the cache.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The process may not write at <paramref name="path"/> or in the cache.
    /// </exception>
    public Task<PublishedFile> DownloadIndexAsync(string path, CancellationToken cancellationToken = default)
    {
        ThrowIfNotAFile(path);
        return _evidence.SaveIndexAsync(path, cancellationToken);
    }

    // A path to write must name a file. Checked at the call, before any request.
    private static void ThrowIfNotAFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.EndsInDirectorySeparator(path))
        {
            throw new ArgumentException(
                $"The path to write must name a file; \"{path}\" names a directory.", nameof(path));
        }
    }
}
