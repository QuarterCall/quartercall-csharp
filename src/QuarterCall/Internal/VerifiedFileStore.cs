using System.Buffers;
using System.Security.Cryptography;
using static System.FormattableString;

namespace QuarterCall.Internal;

/// <summary>
/// Downloads files the manifest names and verifies each against its length and SHA-256 digest before anyone reads
/// it. With a cache directory, a verified file is kept there by its digest and read from there again.
/// </summary>
/// <remarks>
/// A file is written whole to a temporary file of its own, hashed as each chunk is written, and only once it matches
/// is it renamed to where it belongs. So a reader of a file this store has placed sees an earlier file or the new
/// one, whole, and never part of one. Two calls at once for the same file may both download it; the second rename
/// replaces the first with the same bytes.
/// </remarks>
internal sealed class VerifiedFileStore
{
    /// <summary>
    /// How much of a body each read takes, and so the most a download reads past a length it already exceeds.
    /// <see cref="Stream.CopyToAsync(Stream)"/>'s own buffer size, beneath the large object heap's threshold.
    /// </summary>
    internal const int BufferSize = 81920;

    private const string TemporaryExtension = ".tmp";

    // Names the package's files among others in the system's temporary directory.
    private const string ScratchPrefix = "quartercall-";

    // An extension of letters and digits only is carried into a cached file's name.
    private static readonly SearchValues<char> PlainExtension =
        SearchValues.Create("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    private readonly HostClient _host;
    private readonly string? _cacheDirectory;
    private readonly string? _temporaryDirectory;

    // What of a download differs from the manifest.
    private enum Measure
    {
        Length,
        Digest,
    }

    /// <summary>Creates a store that downloads from <paramref name="host"/>.</summary>
    /// <param name="host">The host the files are on.</param>
    /// <param name="cacheDirectory">Where to keep verified files between reads, or null to keep none.</param>
    /// <param name="temporaryDirectory">
    /// Where a file fetched without a cache is kept while it is read, or null for the system's temporary directory.
    /// </param>
    public VerifiedFileStore(HostClient host, string? cacheDirectory, string? temporaryDirectory = null)
    {
        _host = host;
        _cacheDirectory = cacheDirectory;
        _temporaryDirectory = temporaryDirectory;
    }

    /// <summary>
    /// A verified copy of <paramref name="file"/> to read: the cached one when it can be read and still matches, or
    /// else one just downloaded, into the cache when there is one and into the temporary directory when not.
    /// </summary>
    /// <exception cref="DataIntegrityException">
    /// The download does not match <paramref name="file"/>: its length or its digest differs, or it broke off. The
    /// store throws this exception for nothing else, so a caller may retry on it.
    /// </exception>
    /// <exception cref="HttpRequestException">The host answered with a status that is not success.</exception>
    /// <exception cref="TaskCanceledException">
    /// The timeout passed; a <see cref="TimeoutException"/> is inside.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="IOException">A file could not be written.</exception>
    public async Task<VerifiedFile> FetchAsync(FileRef file, CancellationToken ct)
    {
        if (_cacheDirectory is null)
        {
            var name = $"{ScratchPrefix}{Guid.NewGuid():N}{Extension(file.Uri)}";
            var scratch = Path.Combine(_temporaryDirectory ?? Path.GetTempPath(), name);
            await DownloadAsync(file, scratch, ct).ConfigureAwait(false);
            return new VerifiedFile(scratch, FileKeeper.None);
        }

        var cached = CachePath(_cacheDirectory, file);
        if (!await IsWholeAsync(cached, file, ct).ConfigureAwait(false))
        {
            await DownloadOverAsync(file, cached, ct).ConfigureAwait(false);
        }

        return new VerifiedFile(cached, FileKeeper.Cache);
    }

    /// <summary>
    /// Saves a verified copy of <paramref name="file"/> at <paramref name="path"/>, creating its directory if
    /// missing. A file already there is replaced only by one that matches. A copy from the cache is hashed as it is
    /// copied, so the bytes placed at <paramref name="path"/> are always the bytes verified, and the cached file is
    /// read once. A cached file that cannot be opened or read, or does not match, is fetched again and replaced, as
    /// for <see cref="FetchAsync"/>.
    /// </summary>
    /// <exception cref="DataIntegrityException">
    /// The download does not match <paramref name="file"/>, as for <see cref="FetchAsync"/>; or the copy just placed
    /// in the cache could not be read back whole. Both may be retried.
    /// </exception>
    /// <exception cref="HttpRequestException">The host answered with a status that is not success.</exception>
    /// <exception cref="TaskCanceledException">
    /// The timeout passed; a <see cref="TimeoutException"/> is inside.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    /// <exception cref="IOException">A file could not be written.</exception>
    public async Task SaveAsync(FileRef file, string path, CancellationToken ct)
    {
        var target = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (_cacheDirectory is null)
        {
            await DownloadOverAsync(file, target, ct).ConfigureAwait(false);
            return;
        }

        var cached = CachePath(_cacheDirectory, file);
        if (await CopyCachedAsync(cached, target, file, ct).ConfigureAwait(false))
        {
            return;
        }

        // No usable copy in the cache: it is fetched again and replaced, then copied as before.
        await DownloadOverAsync(file, cached, ct).ConfigureAwait(false);
        if (!await CopyCachedAsync(cached, target, file, ct).ConfigureAwait(false))
        {
            throw new DataIntegrityException(
                $"The copy of {file.Uri} just placed in the cache, at {cached}, could not be read back whole. "
                + "Another process may be changing the cache; try again shortly.");
        }
    }

    // The cache's file for file, named by its digest. The cache directory is created if missing.
    private static string CachePath(string cacheDirectory, FileRef file)
    {
        Directory.CreateDirectory(cacheDirectory);
        return Path.Combine(cacheDirectory, file.Sha256 + Extension(file.Uri));
    }

    // A cached file's name carries the extension of the URL's file name, ".parquet" for every file today.
    private static string Extension(Uri uri)
    {
        var extension = Path.GetExtension(uri.AbsolutePath);
        // Anything but letters and digits could mean something else to a file system, such as a stream on NTFS.
        var plain = extension.Length > 1 && !extension.AsSpan(1).ContainsAnyExcept(PlainExtension);
        return plain ? extension : string.Empty;
    }

    // A temporary file beside target, named so that no other call's can be the same.
    private static string Beside(string target)
    {
        return $"{target}.{Guid.NewGuid():N}{TemporaryExtension}";
    }

    // Whether the file at path is there, can be read, and matches file.
    private static Task<bool> IsWholeAsync(string path, FileRef file, CancellationToken ct)
    {
        return CopyIfWholeAsync(path, Stream.Null, file, ct);
    }

    // Reads the file at path once, into output, hashing it as it goes. False when it is not there, cannot be opened
    // or read, or does not match file: in every such case the caller fetches the file again. A cancellation is
    // thrown, never taken for a file that is not whole; so is a failure to write output.
    private static async Task<bool> CopyIfWholeAsync(string path, Stream output, FileRef file, CancellationToken ct)
    {
        FileStream input;
        try
        {
            input = VerifiedFile.OpenShared(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        await using (input.ConfigureAwait(false))
        {
            // A length that differs needs no reading.
            if (input.Length != file.Bytes)
            {
                return false;
            }

            try
            {
                await CopyVerifiedAsync(input, output, file, ct).ConfigureAwait(false);
                return true;
            }
            catch (DataIntegrityException)
            {
                // Its digest differs, or reading it failed part way.
                return false;
            }
        }
    }

    // Moves the verified temporary file over target.
    private static async Task PlaceAsync(string temporary, string target, FileRef file, CancellationToken ct)
    {
        try
        {
            File.Move(temporary, target, overwrite: true);
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Windows refuses to replace a file another reader holds open. If that file already matches, it serves.
            bool whole;
            try
            {
                whole = await IsWholeAsync(target, file, ct).ConfigureAwait(false);
            }
            catch
            {
                DeleteQuietly(temporary);
                throw;
            }

            if (!whole)
            {
                DeleteQuietly(temporary);
                throw;
            }

            // The target matches and serves; the copy is no longer needed.
            DeleteQuietly(temporary);
        }
    }

    // Copies the cached file to a temporary file beside target, hashing it as it goes, and moves the copy over
    // target once it matches. False, with target untouched, when the cached file is not whole.
    private static async Task<bool> CopyCachedAsync(string cached, string target, FileRef file, CancellationToken ct)
    {
        var temporary = Beside(target);
        bool whole;
        try
        {
            var output = CreateNew(temporary);
            await using (output.ConfigureAwait(false))
            {
                whole = await CopyIfWholeAsync(cached, output, file, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            DeleteQuietly(temporary);
            throw;
        }

        if (!whole)
        {
            DeleteQuietly(temporary);
            return false;
        }

        await PlaceAsync(temporary, target, file, ct).ConfigureAwait(false);
        return true;
    }

    // Deletes a temporary file the work no longer needs: one a failure leaves while its exception is on the way out,
    // a copy from the cache that did not match, or a copy whose target already matches. Should the delete fail, the
    // file is left behind, and that is tolerated: throwing here would replace the exception in flight, hiding why the
    // work failed, would turn a mismatch, which is retried, into an IOException, which is not, and would fail a file
    // that serves.
    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind, as above.
        }
    }

    private static FileStream CreateNew(string path)
    {
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
        });
    }

    private static async Task<long> WriteVerifiedAsync(
        HttpResponseMessage response, FileRef file, string destination, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            var output = CreateNew(destination);
            await using (output.ConfigureAwait(false))
            {
                return await CopyVerifiedAsync(body, output, file, ct).ConfigureAwait(false);
            }
        }
    }

    // Reads body, a download or a cached file, in one pass: each chunk goes to output and to the hash. Reading stops
    // at the first byte past the declared length, so a body that is too long costs at most one byte more than the
    // file.
    private static async Task<long> CopyVerifiedAsync(Stream body, Stream output, FileRef file, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long total = 0;
            while (true)
            {
                var remaining = file.Bytes - total;
                var wanted = remaining < BufferSize ? (int)remaining + 1 : BufferSize;
                var read = await ReadBodyAsync(body, buffer.AsMemory(0, wanted), file, total, ct)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > file.Bytes)
                {
                    var detail = Invariant($"it holds more than the {file.Bytes} bytes the manifest names");
                    throw Mismatch(file, Measure.Length, detail);
                }

                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(0, read));
            }

            if (total < file.Bytes)
            {
                var detail = Invariant($"it ended after {total} of the {file.Bytes} bytes the manifest names");
                throw Mismatch(file, Measure.Length, detail);
            }

            if (!HasDigest(hash, file))
            {
                throw Mismatch(file, Measure.Digest, $"the manifest names {file.Sha256}");
            }

            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async ValueTask<int> ReadBodyAsync(
        Stream body, Memory<byte> buffer, FileRef file, long total, CancellationToken ct)
    {
        try
        {
            return await body.ReadAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            // A read cut short by a cancellation, the caller's or the timeout's, is that cancellation.
            ct.ThrowIfCancellationRequested();
            // A dropped connection, or a cached file that cannot be read, is a short file.
            var detail = Invariant($"reading it broke off after {total} of the {file.Bytes} bytes the manifest names");
            throw Mismatch(file, Measure.Length, detail, e);
        }
    }

    private static bool HasDigest(IncrementalHash hash, FileRef file)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        hash.GetHashAndReset(digest);
        return file.HasDigest(digest);
    }

    private static DataIntegrityException Mismatch(
        FileRef file, Measure differs, string detail, IOException? inner = null)
    {
        var measure = differs == Measure.Length ? "length" : "SHA-256 digest";
        var message = $"The file at {file.Uri} does not match the manifest: its {measure} differs ({detail}). "
            + "The file may be being republished; try again shortly.";
        return inner is null ? new DataIntegrityException(message) : new DataIntegrityException(message, inner);
    }

    // Downloads file into a temporary file beside target, then moves it over target once it matches.
    private async Task DownloadOverAsync(FileRef file, string target, CancellationToken ct)
    {
        var temporary = Beside(target);
        await DownloadAsync(file, temporary, ct).ConfigureAwait(false);
        await PlaceAsync(temporary, target, file, ct).ConfigureAwait(false);
    }

    // Downloads file to destination, verified. A failed or cancelled download leaves nothing at destination.
    private async Task DownloadAsync(FileRef file, string destination, CancellationToken ct)
    {
        try
        {
            await _host
                .ReadAsync(file.Uri, (response, token) => WriteVerifiedAsync(response, file, destination, token), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            DeleteQuietly(destination);
            throw;
        }
    }
}
