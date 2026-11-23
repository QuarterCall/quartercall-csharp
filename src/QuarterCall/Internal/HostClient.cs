using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;

namespace QuarterCall.Internal;

/// <summary>
/// Sends the client's requests to one host: composes each address beneath the host's base address, names the
/// package in <c>User-Agent</c>, and holds each request, its body included, to the client's timeout.
/// </summary>
internal sealed class HostClient
{
    private const string ProductName = "quartercall-csharp";

    // The SDK writes the informational version as "<version>+<commit>"; the host is told the version only.
    private const char MetadataMark = '+';

    private static readonly ProductInfoHeaderValue UserAgent = new(ProductName, PackageVersion());

    private readonly HttpClient _http;
    private readonly Uri _baseAddress;
    private readonly TimeSpan _timeout;

    /// <summary>Creates a client of the host at <paramref name="baseAddress"/>.</summary>
    /// <param name="http">The client that sends; it is never changed.</param>
    /// <param name="baseAddress">The host's absolute address. A trailing slash is added when it has none.</param>
    /// <param name="timeout">How long one request may take, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    public HostClient(HttpClient http, Uri baseAddress, TimeSpan timeout)
    {
        _http = http;
        _baseAddress = AsDirectory(baseAddress);
        _timeout = timeout;
    }

    /// <summary>The address of <paramref name="relativePath"/> beneath the host's base address.</summary>
    public Uri Resolve(string relativePath)
    {
        return new Uri(_baseAddress, relativePath);
    }

    /// <summary>
    /// Sends a GET for <paramref name="uri"/>, and once its headers arrive, reads the response with
    /// <paramref name="read"/>. The timeout runs until <paramref name="read"/> completes.
    /// </summary>
    /// <param name="uri">The address to read.</param>
    /// <param name="read">Reads the successful response; its token carries the timeout.</param>
    /// <param name="ct">The caller's token.</param>
    /// <exception cref="HttpRequestException">The host answered with a status that is not success.</exception>
    /// <exception cref="TaskCanceledException">
    /// The timeout passed; a <see cref="TimeoutException"/> is inside.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<T> ReadAsync<T>(
        Uri uri, Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (_timeout != Timeout.InfiniteTimeSpan)
        {
            limit.CancelAfter(_timeout);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(UserAgent);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token)
                .ConfigureAwait(false);
            ThrowIfFailed(response, uri);
            return await read(response, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Reported as HttpClient reports its own timeout, so a caller handles both alike.
            var seconds = _timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture);
            var message = $"Reading {uri} took longer than the timeout of {seconds} seconds. "
                + "Try again, or allow more time with QuarterCallOptions.Timeout.";
            throw new TaskCanceledException(message, new TimeoutException(message, e), e.CancellationToken);
        }
    }

    private static void ThrowIfFailed(HttpResponseMessage response, Uri uri)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        throw new HttpRequestException(
            $"The host answered {status} ({response.ReasonPhrase}) to the request for {uri}. "
            + "Check the data address, or try again later.",
            inner: null,
            response.StatusCode);
    }

    // A base address names a directory: without the trailing slash, its last segment would be replaced.
    private static Uri AsDirectory(Uri baseAddress)
    {
        if (baseAddress.AbsolutePath.EndsWith('/'))
        {
            return baseAddress;
        }

        return new UriBuilder(baseAddress) { Path = baseAddress.AbsolutePath + "/" }.Uri;
    }

    private static string PackageVersion()
    {
        // The SDK always writes the attribute; "0.0.0" stands in should a build ever omit it.
        var version = typeof(HostClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var mark = version.IndexOf(MetadataMark, StringComparison.Ordinal);
        return mark < 0 ? version : version[..mark];
    }
}
