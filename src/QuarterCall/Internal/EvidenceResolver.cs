namespace QuarterCall.Internal;

/// <summary>
/// Finds a published file through the manifest and hands it on verified. A file that does not match the manifest
/// is tried again from a fresh reading of the manifest, since the host may be republishing it.
/// </summary>
internal sealed class EvidenceResolver
{
    // Attempts in all, each from a fresh manifest, before a file that does not match is reported.
    private const int MaxAttempts = 3;

    // The wait before the second attempt; it doubles before each one after.
    private const int FirstWaitMilliseconds = 250;

    // The one file in the directory a table's path names.
    private const string DataFileName = "data.parquet";

    // The manifest members whose absence means a file is not published.
    private const string AccountsTextPathMember = "accounts_text_path";
    private const string ProfilesPathMember = "profiles_path";
    private const string QuorumDetailPathMember = "quorum.detail_path";
    private const string SubmissionsPathMember = "index.submissions_path";

    private readonly ManifestReader _manifest;
    private readonly VerifiedFileStore _files;
    private readonly HostClient _host;

    /// <summary>
    /// Creates a resolver that reads <paramref name="manifest"/> and fetches through <paramref name="files"/>.
    /// </summary>
    /// <param name="manifest">The host's manifest.</param>
    /// <param name="files">Where verified files come from.</param>
    /// <param name="host">The host, which resolves each path the manifest names.</param>
    public EvidenceResolver(ManifestReader manifest, VerifiedFileStore files, HostClient host)
    {
        _manifest = manifest;
        _files = files;
        _host = host;
    }

    /// <summary>
    /// A verified copy of the <paramref name="table"/> file of <paramref name="quarter"/>, to read and then dispose
    /// of. Null only for <see cref="EvidenceTable.AccountText"/> in a quarter whose entry has no
    /// <c>accounts_text_path</c>: a quarter without text values.
    /// </summary>
    /// <exception cref="DataUnavailableException">
    /// The manifest does not list the quarter as readable, or names no file for the table in it.
    /// </exception>
    /// <exception cref="DataIntegrityException">
    /// The manifest names no usable digest or length for the file, or a path that is not beneath the base address;
    /// or the file did not match the manifest in three attempts; or the manifest is malformed.
    /// </exception>
    public Task<VerifiedFile?> OpenAsync(Quarter quarter, EvidenceTable table, CancellationToken ct)
    {
        return AttemptAsync(snapshot => Find(snapshot, quarter, table), _files.FetchAsync, ct);
    }

    /// <summary>
    /// Saves a verified copy of the <paramref name="table"/> file of <paramref name="quarter"/> at
    /// <paramref name="path"/>.
    /// </summary>
    /// <exception cref="DataUnavailableException">
    /// As for <see cref="OpenAsync"/>, and for a quarter without text values, whose text file is not published.
    /// </exception>
    /// <exception cref="DataIntegrityException">As for <see cref="OpenAsync"/>.</exception>
    public async Task<PublishedFile> SaveAsync(Quarter quarter, EvidenceTable table, string path, CancellationToken ct)
    {
        var saved = await AttemptAsync(
                snapshot => Find(snapshot, quarter, table),
                (file, token) => SaveFileAsync(file, path, token),
                ct)
            .ConfigureAwait(false);
        // Find gives no file only for the text of a quarter that has none.
        return saved ?? throw NotListed(quarter, table, AccountsTextPathMember);
    }

    /// <summary>
    /// Saves a verified copy of the index of filings, <c>index.submissions_path</c>, at <paramref name="path"/>.
    /// </summary>
    /// <exception cref="DataUnavailableException">The manifest names no <c>index.submissions_path</c>.</exception>
    /// <exception cref="DataIntegrityException">As for <see cref="OpenAsync"/>.</exception>
    public async Task<PublishedFile> SaveIndexAsync(string path, CancellationToken ct)
    {
        var saved = await AttemptAsync(FindIndex, (file, token) => SaveFileAsync(file, path, token), ct)
            .ConfigureAwait(false);
        return saved ?? throw new DataUnavailableException(
            $"The index of filings is not published: the manifest gives no {SubmissionsPathMember}.");
    }

    private static PublishedQuarter? FindQuarter(ManifestSnapshot snapshot, Quarter quarter)
    {
        foreach (var entry in snapshot.Quarters)
        {
            if (entry.Quarter == quarter)
            {
                return entry;
            }
        }

        return null;
    }

    private static DataUnavailableException NotListed(Quarter quarter, EvidenceTable table, string member)
    {
        return new DataUnavailableException(
            $"The {table} file of {quarter} is not published: the manifest gives no {member} for {quarter}.");
    }

    // Reads the manifest, finds the file in it, and acts on the file. A file that does not match the manifest starts
    // the attempt again from a fresh manifest, after a wait; what the manifest itself says is final. Null when the
    // manifest names no file and that is not an error.
    private async Task<T?> AttemptAsync<T>(
        Func<ManifestSnapshot, FileRef?> find, Func<FileRef, CancellationToken, Task<T>> act, CancellationToken ct)
        where T : class
    {
        for (var attempt = 1; ; attempt++)
        {
            var snapshot = await _manifest.ReadAsync(ct).ConfigureAwait(false);
            // Refused here, before any file is requested, and never retried: a file not published, and a digest or
            // a path the manifest names that cannot be used.
            if (find(snapshot) is not { } file)
            {
                return null;
            }

            try
            {
                return await act(file, ct).ConfigureAwait(false);
            }
            catch (DataIntegrityException) when (attempt < MaxAttempts)
            {
                // The file does not match the manifest read a moment ago: the host may be republishing it.
            }

            await Task.Delay(FirstWaitMilliseconds << (attempt - 1), ct).ConfigureAwait(false);
        }
    }

    private async Task<PublishedFile> SaveFileAsync(FileRef file, string path, CancellationToken ct)
    {
        await _files.SaveAsync(file, path, ct).ConfigureAwait(false);
        return new PublishedFile(file.Uri, file.Sha256, file.Bytes, path);
    }

    // The table's file in the quarter's entry; null only for the text of a quarter that has none.
    private FileRef? Find(ManifestSnapshot snapshot, Quarter quarter, EvidenceTable table)
    {
        var entry = FindQuarter(snapshot, quarter) ?? throw new DataUnavailableException(
            $"The {table} file of {quarter} is not published: the manifest does not list {quarter} as a quarter "
            + "that can be read. GetQuartersAsync lists the quarters that can.");
        switch (table)
        {
            case EvidenceTable.Accounts:
                return InDirectory(entry.AccountsPath, entry.AccountsSha256, entry.AccountsBytes);
            case EvidenceTable.AccountText:
                return entry.AccountsTextPath is { } text
                    ? InDirectory(text, entry.AccountsTextSha256, entry.AccountsTextBytes)
                    : null;
            case EvidenceTable.Profiles:
                var profiles = entry.ProfilesPath ?? throw NotListed(quarter, table, ProfilesPathMember);
                return InDirectory(profiles, entry.ProfilesSha256, entry.ProfilesBytes);
            case EvidenceTable.Quorum:
                var quorum = entry.Quorum;
                var detail = quorum?.DetailPath ?? throw NotListed(quarter, table, QuorumDetailPathMember);
                return FileRef.Create(_host.Resolve(detail), quorum.DetailSha256, quorum.DetailBytes);
            default:
                throw new ArgumentOutOfRangeException(nameof(table), table, "The table is not an EvidenceTable.");
        }
    }

    // The index of filings; null when the manifest names none.
    private FileRef? FindIndex(ManifestSnapshot snapshot)
    {
        if (snapshot.SubmissionsPath is not { } path)
        {
            return null;
        }

        return FileRef.Create(_host.Resolve(path), snapshot.SubmissionsSha256, snapshot.SubmissionsBytes);
    }

    // A table whose path names a directory is read from the directory's one file, with or without a final slash.
    private FileRef InDirectory(string directory, string? sha256, long? bytes)
    {
        // The directory is resolved alone first, so a path the rule refuses is reported as the manifest wrote it.
        _ = _host.Resolve(directory);
        var file = directory.EndsWith('/') ? directory + DataFileName : $"{directory}/{DataFileName}";
        return FileRef.Create(_host.Resolve(file), sha256, bytes);
    }
}
