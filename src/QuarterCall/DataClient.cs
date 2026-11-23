using System.Runtime.CompilerServices;
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

    internal DataClient(HostClient host, string? cacheDirectory, string? temporaryDirectory)
    {
        _manifest = new ManifestReader(host);
        var files = new VerifiedFileStore(host, cacheDirectory, temporaryDirectory);
        _evidence = new EvidenceResolver(_manifest, files, host);
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
    /// Reads a quarter's account values, one <see cref="Account"/> for each row of its accounts file, in the file's
    /// order. The file is downloaded whole and verified against the manifest, as <see cref="DownloadAsync"/> verifies
    /// it, and a cache is used in the same way when <see cref="QuarterCallOptions.CacheDirectory"/> is set. It is then
    /// read one row group at a time, so a whole quarter streams in memory that does not grow with it. A filter
    /// reduces the rows returned, not the download.
    /// </summary>
    /// <remarks>
    /// Nothing is requested until the enumeration begins, and each enumeration reads the manifest and the file
    /// afresh. When it ends, whether at its last row, at a <c>break</c>, at a cancellation or at an error, the file is
    /// closed, and a file downloaded without a cache is deleted. A null in a column the record requires, which only a
    /// column the file declares optional can hold, is refused when its row is built, so a row the filter does not
    /// select is not checked for one.
    /// </remarks>
    /// <param name="quarter">The quarter.</param>
    /// <param name="filter">
    /// Which rows to return; every row when null. An empty set returns none, but the quarter must still be published.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the read, as a token given to <c>WithCancellation</c> also does.
    /// </param>
    /// <returns>The quarter's rows, as the file holds them.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown by this call, before any request: <paramref name="quarter"/> is the default quarter, or a code in the
    /// filter is null or empty, or a charter number or cuid in it is less than 1.
    /// </exception>
    /// <exception cref="DataUnavailableException">
    /// While enumerating: the manifest does not list the quarter as readable.
    /// </exception>
    /// <exception cref="DataIntegrityException">
    /// While enumerating: the file did not match the manifest in three attempts, most often because the quarter is
    /// being republished; or the manifest names no usable digest or length for the file, or a path that is not
    /// beneath the data address; or the manifest is not in its published form.
    /// </exception>
    /// <exception cref="UnsupportedSchemaException">
    /// While enumerating: the manifest is of a schema version this version of the package does not read, or the file
    /// is in a form it does not read: it lacks a column, holds one as another type or holds a null where a value is
    /// required, or is not a Parquet file it can read.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// While enumerating: a request failed, or the host answered with an error.
    /// </exception>
    /// <exception cref="TaskCanceledException">
    /// While enumerating: a request took longer than <see cref="QuarterCallOptions.Timeout"/>; a
    /// <see cref="TimeoutException"/> is inside.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// While enumerating: <paramref name="cancellationToken"/>, or the token given to <c>WithCancellation</c>, was
    /// cancelled.
    /// </exception>
    /// <exception cref="IOException">While enumerating: the file could not be written or read on disk.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// While enumerating: the process may not write the file in the cache or the temporary directory.
    /// </exception>
    public IAsyncEnumerable<Account> ReadAccountsAsync(
        Quarter quarter, AccountFilter? filter = null, CancellationToken cancellationToken = default)
    {
        quarter.ThrowIfDefault(nameof(quarter));
        var rows = AccountRows.Select(filter, nameof(filter));
        return StreamAccountsAsync(quarter, rows, cancellationToken);
    }

    /// <summary>
    /// Reads a quarter's account values that are not numbers, one <see cref="AccountText"/> for each row of its
    /// accounts-text file, in the file's order. A quarter without such values has no file, and yields no row.
    /// </summary>
    /// <remarks>
    /// Nothing is requested until the enumeration begins, and each enumeration reads the manifest and the file
    /// afresh. The file is downloaded whole and verified against the manifest, as <see cref="DownloadAsync"/>
    /// verifies it, and a cache is used in the same way when <see cref="QuarterCallOptions.CacheDirectory"/> is set.
    /// It is read one row group at a time. When the enumeration ends, whether at its last row, at a <c>break</c>, at
    /// a cancellation or at an error, the file is closed, and a file downloaded without a cache is deleted. A null in
    /// a column the record requires, which only a column the file declares optional can hold, is refused when its row
    /// is built, so a row the filter does not select is not checked for one.
    /// </remarks>
    /// <param name="quarter">The quarter.</param>
    /// <param name="filter">
    /// Which rows to return; every row when null. An empty set returns none, but the quarter must still be published.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the read, as a token given to <c>WithCancellation</c> also does.
    /// </param>
    /// <returns>The quarter's text values, as the file holds them; empty for a quarter without any.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown by this call, before any request: <paramref name="quarter"/> is the default quarter, or a code in the
    /// filter is null or empty, or a charter number in it is less than 1.
    /// </exception>
    /// <exception cref="DataUnavailableException">
    /// While enumerating: the manifest does not list the quarter as readable.
    /// </exception>
    /// <exception cref="DataIntegrityException">
    /// While enumerating: the file did not match the manifest in three attempts, most often because the quarter is
    /// being republished; or the manifest names no usable digest or length for the file, or a path that is not beneath
    /// the data address; or the manifest is not in its published form.
    /// </exception>
    /// <exception cref="UnsupportedSchemaException">
    /// While enumerating: the manifest is of a schema version this version of the package does not read, or the file
    /// is in a form it does not read: it lacks a column, holds one as another type or holds a null where a value
    /// is required, or is not a Parquet file it can read.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// While enumerating: a request failed, or the host answered with an error.
    /// </exception>
    /// <exception cref="TaskCanceledException">
    /// While enumerating: a request took longer than <see cref="QuarterCallOptions.Timeout"/>; a
    /// <see cref="TimeoutException"/> is inside.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// While enumerating: <paramref name="cancellationToken"/>, or the token given to <c>WithCancellation</c>, was
    /// cancelled.
    /// </exception>
    /// <exception cref="IOException">While enumerating: the file could not be written or read on disk.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// While enumerating: the process may not write the file in the cache or the temporary directory.
    /// </exception>
    public IAsyncEnumerable<AccountText> ReadAccountTextAsync(
        Quarter quarter, AccountTextFilter? filter = null, CancellationToken cancellationToken = default)
    {
        quarter.ThrowIfDefault(nameof(quarter));
        var rows = AccountTextRows.Select(filter, nameof(filter));
        return StreamTextAsync(quarter, rows, cancellationToken);
    }

    /// <summary>
    /// Reads a quarter's filing profiles, one <see cref="FilingProfile"/> for each row of its profiles file, in the
    /// file's order: one row for each credit union's winning filing. The regulator's own columns are read by name
    /// from each profile.
    /// </summary>
    /// <remarks>
    /// Nothing is requested until the enumeration begins, and each enumeration reads the manifest and the file
    /// afresh. The file is downloaded whole and verified against the manifest, as <see cref="DownloadAsync"/>
    /// verifies it, and a cache is used in the same way when <see cref="QuarterCallOptions.CacheDirectory"/> is set.
    /// It is read one row group at a time. When the enumeration ends, whether at its last row, at a <c>break</c>, at
    /// a cancellation or at an error, the file is closed, and a file downloaded without a cache is deleted. A null in
    /// a column the record requires, which only a column the file declares optional can hold, is refused when its row
    /// is built, so a row the filter does not select is not checked for one.
    /// </remarks>
    /// <param name="quarter">The quarter.</param>
    /// <param name="filter">
    /// Which rows to return; every row when null. An empty set returns none, but the quarter must still be published.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the read, as a token given to <c>WithCancellation</c> also does.
    /// </param>
    /// <returns>The quarter's profiles, as the file holds them.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown by this call, before any request: <paramref name="quarter"/> is the default quarter, or a charter
    /// number or cuid in the filter is less than 1.
    /// </exception>
    /// <exception cref="DataUnavailableException">
    /// While enumerating: the manifest does not list the quarter as readable, or names no <c>profiles_path</c>.
    /// </exception>
    /// <exception cref="DataIntegrityException">
    /// While enumerating: the file did not match the manifest in three attempts, most often because the quarter is
    /// being republished; or the manifest names no usable digest or length for the file, or a path that is not beneath
    /// the data address; or the manifest is not in its published form.
    /// </exception>
    /// <exception cref="UnsupportedSchemaException">
    /// While enumerating: the manifest is of a schema version this version of the package does not read, or the file
    /// is in a form it does not read: it lacks one of the eight leading columns, holds one as another type or holds a
    /// null where a value is required, or is not a Parquet file it can read. A regulator's column of a type the
    /// package does not read is left out of <see cref="FilingProfile.Columns"/>, and does not refuse the file.
    /// </exception>
    /// <exception cref="HttpRequestException">
    /// While enumerating: a request failed, or the host answered with an error.
    /// </exception>
    /// <exception cref="TaskCanceledException">
    /// While enumerating: a request took longer than <see cref="QuarterCallOptions.Timeout"/>; a
    /// <see cref="TimeoutException"/> is inside.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// While enumerating: <paramref name="cancellationToken"/>, or the token given to <c>WithCancellation</c>, was
    /// cancelled.
    /// </exception>
    /// <exception cref="IOException">While enumerating: the file could not be written or read on disk.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// While enumerating: the process may not write the file in the cache or the temporary directory.
    /// </exception>
    public IAsyncEnumerable<FilingProfile> ReadProfilesAsync(
        Quarter quarter, ProfileFilter? filter = null, CancellationToken cancellationToken = default)
    {
        quarter.ThrowIfDefault(nameof(quarter));
        var rows = ProfileRows.Select(filter, nameof(filter));
        return StreamProfilesAsync(quarter, rows, cancellationToken);
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

    // One enumeration's read of the quarter's accounts file. The file, its reader and its buffers are released when
    // the enumeration ends, however it ends. ct is the call's token joined to WithCancellation's.
    private async IAsyncEnumerable<Account> StreamAccountsAsync(
        Quarter quarter, AccountRows rows, [EnumeratorCancellation] CancellationToken ct)
    {
        if (rows.SelectsNone)
        {
            // No row is selected, but a quarter that is not published is still refused: the manifest is read, and
            // no file.
            _ = await _evidence.IsPublishedAsync(quarter, EvidenceTable.Accounts, ct).ConfigureAwait(false);
            yield break;
        }

        var file = await _evidence.OpenAsync(quarter, EvidenceTable.Accounts, ct).ConfigureAwait(false);
        if (file is null)
        {
            // Only a quarter's text may be absent without an error; the manifest always names its accounts.
            yield break;
        }

        await using (file.ConfigureAwait(false))
        {
            var table = await ParquetTable.OpenAsync(file, ct).ConfigureAwait(false);
            await using (table.ConfigureAwait(false))
            {
                await foreach (var account in rows.ReadAsync(table, ct).ConfigureAwait(false))
                {
                    yield return account;
                }
            }
        }
    }

    // One enumeration's read of the quarter's accounts-text file, as StreamAccountsAsync reads accounts. A quarter
    // without text yields nothing, and makes no request for a file.
    private async IAsyncEnumerable<AccountText> StreamTextAsync(
        Quarter quarter, AccountTextRows rows, [EnumeratorCancellation] CancellationToken ct)
    {
        if (rows.SelectsNone)
        {
            _ = await _evidence.IsPublishedAsync(quarter, EvidenceTable.AccountText, ct).ConfigureAwait(false);
            yield break;
        }

        var file = await _evidence.OpenAsync(quarter, EvidenceTable.AccountText, ct).ConfigureAwait(false);
        if (file is null)
        {
            yield break;
        }

        await using (file.ConfigureAwait(false))
        {
            var table = await ParquetTable.OpenAsync(file, ct).ConfigureAwait(false);
            await using (table.ConfigureAwait(false))
            {
                await foreach (var text in rows.ReadAsync(table, ct).ConfigureAwait(false))
                {
                    yield return text;
                }
            }
        }
    }

    // One enumeration's read of the quarter's profiles file, as StreamAccountsAsync reads accounts.
    private async IAsyncEnumerable<FilingProfile> StreamProfilesAsync(
        Quarter quarter, ProfileRows rows, [EnumeratorCancellation] CancellationToken ct)
    {
        if (rows.SelectsNone)
        {
            _ = await _evidence.IsPublishedAsync(quarter, EvidenceTable.Profiles, ct).ConfigureAwait(false);
            yield break;
        }

        var file = await _evidence.OpenAsync(quarter, EvidenceTable.Profiles, ct).ConfigureAwait(false);
        if (file is null)
        {
            // Only a quarter's text may be absent without an error; a quarter without profiles is refused earlier.
            yield break;
        }

        await using (file.ConfigureAwait(false))
        {
            var table = await ParquetTable.OpenAsync(file, ct).ConfigureAwait(false);
            await using (table.ConfigureAwait(false))
            {
                await foreach (var profile in rows.ReadAsync(table, ct).ConfigureAwait(false))
                {
                    yield return profile;
                }
            }
        }
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
