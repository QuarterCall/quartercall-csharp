using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace QuarterCall.Tests.Support;

/// <summary>
/// A data host held in memory: it serves <see cref="Manifest"/> and <see cref="Bodies"/> to a client built over it,
/// and records every request.
/// </summary>
internal sealed class FixtureHost : HttpMessageHandler
{
    /// <summary>Where the host serves its manifest, relative to <see cref="BaseAddress"/>.</summary>
    public const string ManifestPath = "index/manifest.json";

    /// <summary>The manifest, serialised afresh at each request so a test may edit it between requests.</summary>
    public JsonObject Manifest { get; } = new()
    {
        ["schema_version"] = "1.2",
        ["quarters"] = new JsonArray(),
    };

    /// <summary>The bodies of the other files, by path relative to <see cref="BaseAddress"/>.</summary>
    public Dictionary<string, byte[]> Bodies { get; } = [];

    /// <summary>Answers a request before the host does; returning null leaves it to the host.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? OnRequest { get; set; }

    /// <summary>How long each request waits before it is answered, on the request's own cancellation token.</summary>
    public TimeSpan? Delay { get; set; }

    /// <summary>Every request received, in order, with its <c>User-Agent</c>, or null when it had none.</summary>
    public List<(Uri Uri, string? UserAgent)> Requests { get; } = [];

    /// <summary>The address the host answers beneath.</summary>
    public Uri BaseAddress { get; init; } = new("https://fixture.test/");

    /// <summary>A client of this host, its cache in <paramref name="cacheDirectory"/>.</summary>
    public QuarterCallClient CreateClient(string? cacheDirectory = null)
    {
        // The client's own timeout is the one under test, so the HttpClient sets none.
        var http = new HttpClient(this, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new QuarterCallOptions { DataBaseAddress = BaseAddress, CacheDirectory = cacheDirectory };
        return new QuarterCallClient(http, options);
    }

    /// <summary>
    /// Adds a readable quarter to the manifest, newest first, and returns its entry for a test to edit.
    /// </summary>
    public JsonObject AddQuarter(string label)
    {
        var quarter = Quarter.Parse(label);
        var entry = new JsonObject
        {
            ["quarter"] = label,
            ["cycle_date"] = quarter.CycleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["status"] = "open",
            ["accounts_path"] = $"curated/accounts/quarter={label}/",
        };

        var quarters = Manifest["quarters"]!.AsArray();
        var index = 0;
        while (index < quarters.Count && !IsOlder(quarters[index], quarter))
        {
            index++;
        }

        quarters.Insert(index, entry);
        return entry;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var userAgent = request.Headers.TryGetValues("User-Agent", out var values) ? string.Join(" ", values) : null;
        lock (Requests)
        {
            Requests.Add((uri, userAgent));
        }

        if (Delay is { } delay)
        {
            await Task.Delay(delay, cancellationToken);
        }

        return OnRequest?.Invoke(request) ?? Answer(uri);
    }

    // Whether a manifest entry names a quarter older than the one being inserted.
    private static bool IsOlder(JsonNode? entry, Quarter quarter)
    {
        var label = entry?["quarter"]?.GetValue<string>();
        return Quarter.TryParse(label, out var existing) && existing < quarter;
    }

    private HttpResponseMessage Answer(Uri uri)
    {
        // A base address without a trailing slash still has its last segment as a directory.
        var root = BaseAddress.AbsoluteUri.TrimEnd('/') + "/";
        var address = uri.AbsoluteUri;
        if (!address.StartsWith(root, StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var path = address[root.Length..];
        if (path == ManifestPath)
        {
            var json = new StringContent(Manifest.ToJsonString(), Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = json };
        }

        if (Bodies.TryGetValue(path, out var body))
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
