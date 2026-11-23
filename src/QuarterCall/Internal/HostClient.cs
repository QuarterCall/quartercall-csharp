using System.Buffers;
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

    private const char Separator = '/';

    // A colon in a path's first segment makes the path a scheme's, such as "https:" or "C:".
    private const char SchemeMark = ':';

    private const string ParentSegment = "..";

    private static readonly ProductInfoHeaderValue UserAgent = new(ProductName, PackageVersion());

    // What a relative path beneath the base address never holds: a query, a fragment, a backslash.
    private static readonly SearchValues<char> RefusedMarks = SearchValues.Create("?#\\");

    private readonly HttpClient _http;
    private readonly Uri _baseAddress;
    private readonly TimeSpan _timeout;

    // The base address without its query or fragment: every address beneath it starts with this.
    private readonly string _root;

    /// <summary>Creates a client of the host at <paramref name="baseAddress"/>.</summary>
    /// <param name="http">The client that sends; it is never changed.</param>
    /// <param name="baseAddress">The host's absolute address. A trailing slash is added when it has none.</param>
    /// <param name="timeout">How long one request may take, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    public HostClient(HttpClient http, Uri baseAddress, TimeSpan timeout)
    {
        _http = http;
        _baseAddress = AsDirectory(baseAddress);
        _timeout = timeout;
        _root = _baseAddress.GetLeftPart(UriPartial.Path);
    }

    /// <summary>
    /// The address of <paramref name="relativePath"/> beneath the host's base address. Every address the client
    /// requests is made here, so no path the manifest names can lead elsewhere.
    /// </summary>
    /// <param name="relativePath">A path relative to the base address, as the manifest writes it.</param>
    /// <exception cref="DataIntegrityException">
    /// The path is not relative, or leads out from beneath the base address: it is empty, or has a scheme, a host, a
    /// query, a fragment, a leading <c>/</c>, a backslash, white space, a control character or a <c>..</c> segment,
    /// or the address composed from it is not beneath the base address.
    /// </exception>
    public Uri Resolve(string relativePath)
    {
        if (!HasRelativeForm(relativePath))
        {
            throw NotRelative(relativePath, inner: null);
        }

        Uri uri;
        try
        {
            uri = new Uri(_baseAddress, relativePath);
        }
        catch (UriFormatException e)
        {
            throw NotRelative(relativePath, e);
        }

        // A second guard, should the form rule ever miss a way out: the composed address must start with the base's.
        if (!uri.AbsoluteUri.StartsWith(_root, StringComparison.Ordinal))
        {
            throw NotRelative(relativePath, inner: null);
        }

        return uri;
    }

    /// <summary>
    /// Sends a GET for <paramref name="uri"/>, and once its headers arrive, reads the response with
    /// <paramref name="read"/>. The timeout runs until <paramref name="read"/> completes.
    /// </summary>
    /// <remarks>
    /// The response, its body included, is disposed as soon as <paramref name="read"/> completes. So
    /// <paramref name="read"/> must finish with the body itself, and must not return a stream, or anything else,
    /// that reads the body later.
    /// </remarks>
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

    // Whether a path has the form of one relative to the base address and beneath it. The path is judged unescaped,
    // so an escaped dot, slash or mark counts as what it stands for.
    private static bool HasRelativeForm(string path)
    {
        var plain = Uri.UnescapeDataString(path);
        if (plain.Length == 0
            || plain[0] == Separator
            || plain.AsSpan().ContainsAny(RefusedMarks)
            || HasSpaceOrControl(plain))
        {
            return false;
        }

        var slash = plain.IndexOf(Separator);
        var first = slash < 0 ? plain : plain[..slash];
        if (first.Contains(SchemeMark))
        {
            return false;
        }

        return !plain.Split(Separator).Contains(ParentSegment);
    }

    // A published path holds no white space or control character. Uri trims some of them, and so would compose the
    // address of a path other than the one judged: " ../x" is read as "../x".
    private static bool HasSpaceOrControl(string path)
    {
        foreach (var c in path)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }

    private DataIntegrityException NotRelative(string path, UriFormatException? inner)
    {
        var message = $"The manifest names \"{path}\" as the path of a file, but a path must be relative to the "
            + $"data address, {_root}, and stay beneath it: no scheme, host, query, fragment, leading slash, "
            + "backslash, white space, control character or \"..\" segment. Nothing was requested from it. Check "
            + "that the data address points to a QuarterCall data host.";
        return inner is null ? new DataIntegrityException(message) : new DataIntegrityException(message, inner);
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
