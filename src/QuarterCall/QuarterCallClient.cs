using System.Net;
using QuarterCall.Internal;

namespace QuarterCall;

/// <summary>
/// The entry point: a client of the QuarterCall data host. One client may serve a whole application, and its
/// members are safe to call concurrently.
/// </summary>
public sealed class QuarterCallClient : IDisposable
{
    // Within the longest timeout HttpClient accepts, int.MaxValue milliseconds or about 24.8 days.
    private const int MaxTimeoutDays = 24;

    // A client may serve a whole application. Retiring each pooled connection after this long makes it resolve
    // the host's name again, so a change of the host's address reaches a client that never restarts.
    private const int ConnectionLifetimeMinutes = 15;

    private readonly HttpClient? _ownedHttp;

    /// <summary>Creates a client of the public data host, <c>https://data.quartercall.com/</c>.</summary>
    public QuarterCallClient()
        : this(new QuarterCallOptions())
    {
    }

    /// <summary>Creates a client with its own connections, configured by <paramref name="options"/>.</summary>
    /// <param name="options">Where to read from, and how long to wait.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The data base address is not an absolute http or https address, or the cache directory is empty, blank or not
    /// a valid path.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The timeout is neither more than zero and at most 24 days, nor infinite.
    /// </exception>
    public QuarterCallClient(QuarterCallOptions options)
    {
        // Every option is checked before the connections exist, so a refused option leaks none.
        var cacheDirectory = Validate(options);
        var http = CreateHttpClient();
        try
        {
            Data = new DataClient(CreateHost(http, options), cacheDirectory);
        }
        catch
        {
            // Nothing here throws for valid options today; should a later change make it, nothing leaks.
            http.Dispose();
            throw;
        }

        _ownedHttp = http;
    }

    /// <summary>
    /// Creates a client that sends through <paramref name="httpClient"/>, which stays the caller's: it is neither
    /// changed nor disposed. Its own <see cref="HttpClient.Timeout"/> still applies, beside the client's.
    /// </summary>
    /// <param name="httpClient">The client to send through, such as one from <c>IHttpClientFactory</c>.</param>
    /// <param name="options">Where to read from, and how long to wait; the defaults when null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The data base address is not an absolute http or https address, or the cache directory is empty, blank or not
    /// a valid path.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The timeout is neither more than zero and at most 24 days, nor infinite.
    /// </exception>
    public QuarterCallClient(HttpClient httpClient, QuarterCallOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        options ??= new QuarterCallOptions();
        var cacheDirectory = Validate(options);
        Data = new DataClient(CreateHost(httpClient, options), cacheDirectory);
    }

    /// <summary>The evidence: the filings as the data host publishes them.</summary>
    public DataClient Data { get; }

    /// <summary>
    /// Releases the connections the client opened. An <see cref="HttpClient"/> supplied to it is left open.
    /// </summary>
    public void Dispose()
    {
        _ownedHttp?.Dispose();
    }

    // Checks the options, and returns the cache directory as a full path, or null for none.
    private static string? Validate(QuarterCallOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var address = options.DataBaseAddress;
        if (address is null
            || !address.IsAbsoluteUri
            || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                "The data base address must be an absolute http or https address, such as "
                + $"https://data.quartercall.com/; it is \"{address}\".",
                nameof(options));
        }

        var timeout = options.Timeout;
        var inRange = timeout > TimeSpan.Zero && timeout <= TimeSpan.FromDays(MaxTimeoutDays);
        if (timeout != Timeout.InfiniteTimeSpan && !inRange)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                timeout,
                $"The timeout must be more than zero and at most {MaxTimeoutDays} days, "
                + "or Timeout.InfiniteTimeSpan for none.");
        }

        if (options.CacheDirectory is not { } cache)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(cache))
        {
            throw new ArgumentException(
                $"The cache directory must name a directory, or be null to keep no cache; it is \"{cache}\".",
                nameof(options));
        }

        try
        {
            // A relative cache directory is taken from the working directory now, so that a later change of the
            // working directory does not move the cache.
            return Path.GetFullPath(cache);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(
                $"The cache directory \"{cache}\" is not a valid path.", nameof(options), e);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(ConnectionLifetimeMinutes),
        };
        // The client's own timeout covers each request to its last byte, so HttpClient's is switched off.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static HostClient CreateHost(HttpClient http, QuarterCallOptions options)
    {
        return new HostClient(http, options.DataBaseAddress, options.Timeout);
    }
}
