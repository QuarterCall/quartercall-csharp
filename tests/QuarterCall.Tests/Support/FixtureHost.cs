using System.Globalization;
using System.Net;
using System.Security.Cryptography;
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

    /// <summary>Where <see cref="AddIndex"/> publishes the index of filings.</summary>
    public const string SubmissionsPath = "index/submissions.parquet";

    // The one file in the directory a table's path names.
    private const string DataFileName = "data.parquet";

    private readonly List<CountingStream> _served = [];

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

    /// <summary>
    /// For each body of <see cref="Bodies"/> served, in the order served, how many of its bytes the client has read.
    /// </summary>
    public IReadOnlyList<long> BodyBytesRead
    {
        get
        {
            lock (_served)
            {
                return [.. _served.Select(stream => stream.BytesRead)];
            }
        }
    }

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

    /// <summary>
    /// Publishes <paramref name="body"/> as the <paramref name="table"/> file of the quarter whose entry is
    /// <paramref name="quarter"/>: stores it at the table's path, and writes that path, its <c>sha256</c> and its
    /// <c>bytes</c> into the entry, or into its <c>quorum</c> object, which is created if absent. A path the entry
    /// already names is kept. Returns where the file is, relative to <see cref="BaseAddress"/>.
    /// </summary>
    public string AddFile(JsonObject quarter, EvidenceTable table, byte[] body)
    {
        var label = quarter["quarter"]!.GetValue<string>();
        return table switch
        {
            EvidenceTable.Accounts => Publish(quarter, "accounts", $"curated/accounts/quarter={label}/", body),
            EvidenceTable.AccountText =>
                Publish(quarter, "accounts_text", $"curated/accounts_text/quarter={label}/", body),
            EvidenceTable.Profiles => Publish(quarter, "profiles", $"curated/profiles/quarter={label}/", body),
            EvidenceTable.Quorum => PublishFile(
                Member(quarter, "quorum"), "detail", $"index/quorum/quarter={label}/{DataFileName}", body),
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, null),
        };
    }

    /// <summary>
    /// Publishes <paramref name="body"/> as the index of filings: stores it at <see cref="SubmissionsPath"/>, unless
    /// the manifest already names another, and writes <c>index.submissions_path</c>, <c>submissions_sha256</c> and
    /// <c>submissions_bytes</c>. Returns where the file is, relative to <see cref="BaseAddress"/>.
    /// </summary>
    public string AddIndex(byte[] body)
    {
        return PublishFile(Member(Manifest, "index"), "submissions", SubmissionsPath, body);
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

    // The object held under name, created if absent.
    private static JsonObject Member(JsonObject owner, string name)
    {
        if (owner[name] is JsonObject member)
        {
            return member;
        }

        member = [];
        owner[name] = member;
        return member;
    }

    // A table whose path names a directory: its file is the directory's data.parquet, with or without a final slash.
    private string Publish(JsonObject owner, string prefix, string defaultPath, byte[] body)
    {
        var directory = Describe(owner, prefix, defaultPath, body);
        var file = directory.EndsWith('/') ? directory + DataFileName : $"{directory}/{DataFileName}";
        Bodies[file] = body;
        return file;
    }

    // A file whose path names the file itself.
    private string PublishFile(JsonObject owner, string prefix, string defaultPath, byte[] body)
    {
        var file = Describe(owner, prefix, defaultPath, body);
        Bodies[file] = body;
        return file;
    }

    // Writes <prefix>_path, unless already there, <prefix>_sha256 and <prefix>_bytes; returns the path.
    private static string Describe(JsonObject owner, string prefix, string defaultPath, byte[] body)
    {
        var path = owner[prefix + "_path"]?.GetValue<string>() ?? defaultPath;
        owner[prefix + "_path"] = path;
        owner[prefix + "_sha256"] = Convert.ToHexStringLower(SHA256.HashData(body));
        owner[prefix + "_bytes"] = body.LongLength;
        return path;
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
            // Two requests at once may serialise the manifest together.
            string text;
            lock (Manifest)
            {
                text = Manifest.ToJsonString();
            }

            var json = new StringContent(text, Encoding.UTF8, "application/json");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = json };
        }

        if (Bodies.TryGetValue(path, out var body))
        {
            var served = new CountingStream(body);
            lock (_served)
            {
                _served.Add(served);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(served) };
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    // A body served as a stream that counts the bytes read from it. It cannot seek, so nothing reads it ahead.
    private sealed class CountingStream(byte[] body) : Stream
    {
        private readonly MemoryStream _body = new(body, writable: false);
        private long _read;

        public long BytesRead => Interlocked.Read(ref _read);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Count(_body.Read(buffer, offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            return Count(_body.Read(buffer));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Count(_body.Read(buffer.Span)));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            Interlocked.Add(ref _read, read);
            return read;
        }
    }
}
