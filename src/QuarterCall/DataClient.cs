using QuarterCall.Internal;

namespace QuarterCall;

/// <summary>
/// Reads the evidence: the credit unions' Call Report filings as the data host publishes them. Reach it through
/// <see cref="QuarterCallClient.Data"/>. Its methods are safe to call concurrently.
/// </summary>
public sealed class DataClient
{
    private readonly ManifestReader _manifest;

    internal DataClient(HostClient host)
    {
        _manifest = new ManifestReader(host);
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
}
