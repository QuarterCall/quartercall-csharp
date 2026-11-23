namespace QuarterCall;

/// <summary>Where a <see cref="QuarterCallClient"/> reads from, and how long it waits.</summary>
public sealed class QuarterCallOptions
{
    private const string PublicDataAddress = "https://data.quartercall.com/";

    // HttpClient's own default, so a client behaves as a plain HttpClient would.
    private const int DefaultTimeoutSeconds = 100;

    /// <summary>
    /// The address of the data host, <c>https://data.quartercall.com/</c> unless set. An absolute <c>http</c> or
    /// <c>https</c> address; a mirror beneath a path, such as <c>https://mirror.example/qc</c>, keeps its path.
    /// </summary>
    public Uri DataBaseAddress { get; init; } = new(PublicDataAddress);

    /// <summary>
    /// A directory in which to keep downloaded files between runs, or null to keep none.
    /// </summary>
    public string? CacheDirectory { get; init; }

    /// <summary>
    /// How long one request may take, to its last byte; 100 seconds unless set.
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> sets no limit. A request that takes longer throws
    /// <see cref="TaskCanceledException"/> holding a <see cref="TimeoutException"/>.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
}
