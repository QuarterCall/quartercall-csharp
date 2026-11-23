namespace QuarterCall.Internal;

/// <summary>Who keeps a verified file once its reader is done with it.</summary>
internal enum FileKeeper
{
    /// <summary>No one: the file is temporary, and deleted when its reader disposes of it.</summary>
    None,

    /// <summary>The cache: the file stays for the next read.</summary>
    Cache,
}

/// <summary>
/// A file on disk whose length and SHA-256 digest matched the manifest. Disposing of it deletes it, unless it is in
/// the cache.
/// </summary>
internal sealed class VerifiedFile : IAsyncDisposable
{
    private readonly FileKeeper _keeper;

    /// <summary>Wraps the verified file at <paramref name="path"/>.</summary>
    /// <param name="path">Where the file is.</param>
    /// <param name="keeper">Whether the file is temporary, or the cache's.</param>
    public VerifiedFile(string path, FileKeeper keeper)
    {
        Path = path;
        _keeper = keeper;
    }

    /// <summary>Where the file is.</summary>
    public string Path { get; }

    /// <summary>
    /// Opens the file to read. Others may read it at the same time, and it may be replaced or deleted while open.
    /// </summary>
    public FileStream OpenRead()
    {
        return OpenShared(Path);
    }

    /// <summary>Deletes the file, unless it is in the cache. A delete that fails leaves the file where it is.</summary>
    public ValueTask DisposeAsync()
    {
        if (_keeper == FileKeeper.None)
        {
            try
            {
                File.Delete(Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The file has been served. One left in the temporary directory is tolerable; failing the read for it
                // is not.
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Opens a file to read while letting others read it, and replace or delete it: a cached file stays usable by
    /// one reader while another call renames a newer copy over it.
    /// </summary>
    internal static FileStream OpenShared(string path)
    {
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous,
        });
    }
}
