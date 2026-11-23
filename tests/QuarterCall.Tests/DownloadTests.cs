using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using QuarterCall.Internal;
using QuarterCall.Tests.Support;
using Xunit;

namespace QuarterCall.Tests;

public sealed class DownloadTests : IDisposable
{
    private static readonly Quarter Q2026Q3 = Quarter.Parse("2026Q3");

    // Long enough that a test which passes never waits for it, short enough that one which fails ends.
    private static readonly TimeSpan LongWait = TimeSpan.FromSeconds(10);

    // Each test writes beneath a directory of its own, removed afterwards.
    private readonly string _root = Directory.CreateTempSubdirectory("download-tests-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(EvidenceTable.Accounts, "curated/accounts/quarter=2026Q3/data.parquet")]
    [InlineData(EvidenceTable.AccountText, "curated/accounts_text/quarter=2026Q3/data.parquet")]
    [InlineData(EvidenceTable.Profiles, "curated/profiles/quarter=2026Q3/data.parquet")]
    [InlineData(EvidenceTable.Quorum, "index/quorum/quarter=2026Q3/data.parquet")]
    public async Task SavesAVerifiedFile(EvidenceTable table, string file)
    {
        var host = new FixtureHost { BaseAddress = new Uri("https://mirror.test/qc") };
        var entry = host.AddQuarter("2026Q3");
        var body = Body(5000);
        host.AddFile(entry, table, body);
        var index = Body(3000, seed: 2);
        host.AddIndex(index);
        using var client = host.CreateClient();
        // Its directory does not exist yet.
        var path = Destination();

        var saved = await client.Data.DownloadAsync(Q2026Q3, table, path);

        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        Assert.Equal(new Uri("https://mirror.test/qc/" + file), saved.Uri);
        Assert.Equal(Digest(body), saved.Sha256);
        Assert.Equal(body.LongLength, saved.Bytes);
        Assert.Equal(path, saved.Path);
        Assert.Equal(new[] { ManifestUri(host), saved.Uri }, host.Requests.Select(r => r.Uri));
        Assert.StartsWith("https://mirror.test/qc/", saved.Uri.AbsoluteUri);

        var indexPath = Destination("submissions.parquet");
        var savedIndex = await client.Data.DownloadIndexAsync(indexPath);

        Assert.Equal(index, await File.ReadAllBytesAsync(indexPath));
        Assert.Equal(new Uri("https://mirror.test/qc/index/submissions.parquet"), savedIndex.Uri);
        Assert.Equal(Digest(index), savedIndex.Sha256);
        Assert.Equal(index.LongLength, savedIndex.Bytes);
        Assert.Equal(indexPath, savedIndex.Path);
        Assert.Equal(savedIndex.Uri, host.Requests[^1].Uri);
        Assert.Equal(new[] { "accounts.parquet", "submissions.parquet" }, FileNames(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task ReadsADirectoryWithoutItsTrailingSlash()
    {
        var host = new FixtureHost();
        var entry = host.AddQuarter("2026Q3");
        entry["profiles_path"] = "curated/profiles/quarter=2026Q3";
        var body = Body(100);
        host.AddFile(entry, EvidenceTable.Profiles, body);
        using var client = host.CreateClient();

        var saved = await client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Profiles, Destination());

        Assert.Equal(new Uri("https://fixture.test/curated/profiles/quarter=2026Q3/data.parquet"), saved.Uri);
        Assert.Equal(body, await File.ReadAllBytesAsync(saved.Path));
    }

    [Fact]
    public async Task ReplacesTheFileAtPath()
    {
        var host = new FixtureHost();
        var body = Body(5000);
        host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, body);
        using var client = host.CreateClient();
        var path = Destination();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Body(10, seed: 3));

        await client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, path);

        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Theory]
    [InlineData("a changed byte")]
    [InlineData("a truncated body")]
    [InlineData("a body one byte long")]
    public async Task RefusesAFileThatDoesNotMatch(string served)
    {
        var host = new FixtureHost();
        var body = Body(5000);
        var file = host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, body);
        host.Bodies[file] = served switch
        {
            "a changed byte" => Changed(body, at: 1234),
            "a truncated body" => body[..2500],
            _ => body[..1],
        };
        using var client = host.CreateClient();
        var path = Destination();

        var e = await Assert.ThrowsAsync<DataIntegrityException>(
            () => client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, path));

        Assert.Contains("republished", e.Message);
        Assert.Contains("try again", e.Message);
        Assert.Contains(new Uri(host.BaseAddress, file).AbsoluteUri, e.Message);
        Assert.Contains(served == "a changed byte" ? "digest" : "length", e.Message);
        Assert.Equal(3, ManifestRequests(host));
        Assert.False(File.Exists(path));
        // Nor is any temporary file left beside it.
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task StopsAtTheDeclaredLength()
    {
        var host = new FixtureHost();
        var entry = host.AddQuarter("2026Q3");
        var body = Body(10 * 1024 * 1024);
        host.AddFile(entry, EvidenceTable.Accounts, body);
        entry["accounts_bytes"] = 10L;
        entry["accounts_sha256"] = Digest(body[..10]);
        using var client = host.CreateClient();
        var path = Destination();

        var e = await Assert.ThrowsAsync<DataIntegrityException>(
            () => client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, path));

        Assert.Contains("length", e.Message);
        Assert.Equal(3, host.BodyBytesRead.Count);
        Assert.All(host.BodyBytesRead, read => Assert.InRange(read, 0, 10 + VerifiedFileStore.BufferSize));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task ReadsAgainFromANewerManifest()
    {
        var host = new FixtureHost();
        var entry = host.AddQuarter("2026Q3");
        var file = host.AddFile(entry, EvidenceTable.Accounts, Body(5000));
        var republished = Body(6000, seed: 2);
        host.Bodies[file] = republished;
        var fileUri = new Uri(host.BaseAddress, file);
        // The host republishes while the first copy is read: its manifest then names the new file.
        host.OnRequest = request =>
        {
            if (request.RequestUri == fileUri)
            {
                entry["accounts_sha256"] = Digest(republished);
                entry["accounts_bytes"] = republished.LongLength;
            }

            return null;
        };
        var cache = Path.Combine(_root, "cache");
        using var client = host.CreateClient(cache);
        var path = Destination();

        var saved = await client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, path);

        Assert.Equal(republished, await File.ReadAllBytesAsync(path));
        Assert.Equal(Digest(republished), saved.Sha256);
        Assert.Equal(2, ManifestRequests(host));
        // The copy that did not match left nothing in the cache.
        Assert.Equal(new[] { Digest(republished) + ".parquet" }, FileNames(cache));
    }

    [Fact]
    public async Task ReadsAgainAfterABrokenConnection()
    {
        var host = new FixtureHost();
        var body = Body(5000);
        var file = host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, body);
        var fileUri = new Uri(host.BaseAddress, file);
        var requests = 0;
        host.OnRequest = request =>
            request.RequestUri == fileUri && Interlocked.Increment(ref requests) == 1
                ? Serve(new CutShortStream(body[..1000], Cut.Break))
                : null;
        using var client = host.CreateClient();
        var path = Destination();

        await client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, path);

        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, ManifestRequests(host));
        Assert.Equal(new[] { path }, Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task KeepsTheBrokenConnectionInside()
    {
        var host = new FixtureHost();
        var body = Body(5000);
        host.OnRequest = _ => Serve(new CutShortStream(body[..1000], Cut.Break));
        var store = new VerifiedFileStore(Host(host), cacheDirectory: null);
        var file = FileRef.Create(new Uri(host.BaseAddress, "x/data.parquet"), Digest(body), body.LongLength);
        var path = Destination();

        var e = await Assert.ThrowsAsync<DataIntegrityException>(
            () => store.SaveAsync(file, path, CancellationToken.None));

        Assert.IsAssignableFrom<IOException>(e.InnerException);
        // The same message serves the index, which is no quarter's.
        Assert.Contains("The file may be being republished", e.Message);
        Assert.Contains("length", e.Message);
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task StopsWaitingToRetryWhenCancelled()
    {
        var host = new FixtureHost();
        var body = Body(5000);
        var file = host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, body);
        var fileUri = new Uri(host.BaseAddress, file);
        using var cancel = new CancellationTokenSource();
        var served = 0;
        // Every body ends short. The first attempt fails plainly. As the second's body ends, the caller cancels
        // 100 ms later: inside the 500 ms wait before a third attempt.
        host.OnRequest = request =>
        {
            if (request.RequestUri != fileUri)
            {
                return null;
            }

            var second = Interlocked.Increment(ref served) == 2;
            Action? atCut = second ? () => cancel.CancelAfter(TimeSpan.FromMilliseconds(100)) : null;
            return Serve(new CutShortStream(body[..1000], Cut.End, atCut));
        };
        using var client = host.CreateClient();
        var path = Destination();

        var download = client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, path, cancel.Token);

        // A wait that never ended would fail here, after LongWait, rather than hang.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(LongWait));
        // No third attempt began: a wait that ran out in spite of the cancellation would read the manifest again.
        var manifest = ManifestUri(host);
        Assert.Equal(new[] { manifest, fileUri, manifest, fileUri }, host.Requests.Select(r => r.Uri));
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task LeavesADirectoryAtPathAlone()
    {
        var host = new FixtureHost();
        host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, Body(5000));
        using var client = host.CreateClient();
        var path = Destination();
        Directory.CreateDirectory(path);
        await File.WriteAllBytesAsync(Path.Combine(path, "kept"), [1]);

        var e = await Record.ExceptionAsync(() => client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, path));

        // The verified file cannot be moved over a directory, and a directory is not that file: the move's failure
        // is passed on, once. Linux reports it as IOException; Windows may report it as UnauthorizedAccessException.
        Assert.True(
            e is IOException || (OperatingSystem.IsWindows() && e is UnauthorizedAccessException),
            $"Expected the failed move, got {e}");
        Assert.Equal(1, ManifestRequests(host));
        Assert.Equal(new[] { "accounts.parquet" }, FileNames(Path.GetDirectoryName(path)!));
        Assert.Equal(new[] { "kept" }, FileNames(path));
    }

    [UnixPermissionsFact]
    [UnsupportedOSPlatform("windows")]
    public async Task KeepsTheCancellationWhenCleanupFails()
    {
        var host = new FixtureHost();
        var body = Body(200_000);
        var file = host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, body);
        var fileUri = new Uri(host.BaseAddress, file);
        var stalling = new CutShortStream(body[..1000], Cut.Stall);
        host.OnRequest = request => request.RequestUri == fileUri ? Serve(stalling) : null;
        using var client = host.CreateClient();
        var directory = Path.GetDirectoryName(Destination())!;
        using var cancel = new CancellationTokenSource();

        var download = client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, Destination(), cancel.Token);
        await stalling.HeadRead.Task.WaitAsync(LongWait);
        var mode = File.GetUnixFileMode(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await cancel.CancelAsync();

            // The temporary file cannot be deleted now; the caller still learns of the cancellation.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            // And the delete did fail: the temporary file is still there.
            var left = Assert.Single(Directory.GetFiles(directory));
            Assert.EndsWith(".tmp", left);
        }
        finally
        {
            File.SetUnixFileMode(directory, mode);
        }
    }

    [Theory]
    [InlineData("no accounts_sha256")]
    [InlineData("no accounts_bytes")]
    [InlineData("an uppercase digest")]
    [InlineData("a digest of 63 characters")]
    [InlineData("-1 bytes")]
    public async Task RefusesAMissingOrMalformedDigest(string fault)
    {
        var host = new FixtureHost();
        var entry = host.AddQuarter("2026Q3");
        var body = Body(5000);
        host.AddFile(entry, EvidenceTable.Accounts, body);
        switch (fault)
        {
            case "no accounts_sha256":
                entry.Remove("accounts_sha256");
                break;
            case "no accounts_bytes":
                entry.Remove("accounts_bytes");
                break;
            case "an uppercase digest":
                entry["accounts_sha256"] = Digest(body).ToUpperInvariant();
                break;
            case "a digest of 63 characters":
                entry["accounts_sha256"] = Digest(body)[..63];
                break;
            default:
                entry["accounts_bytes"] = -1L;
                break;
        }

        using var client = host.CreateClient();

        var e = await Assert.ThrowsAsync<DataIntegrityException>(
            () => client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, Destination()));

        Assert.Contains("no usable digest", e.Message);
        Assert.Equal(new[] { ManifestUri(host) }, host.Requests.Select(r => r.Uri));
    }

    [Theory]
    [InlineData("https://other.example/x")]
    [InlineData("../x")]
    [InlineData("/x")]
    [InlineData("a\\b")]
    [InlineData("x?y=1")]
    [InlineData("//other.example/x")]
    [InlineData("x#y")]
    [InlineData("a/../../x")]
    [InlineData("%2e%2e/x")]
    [InlineData("")]
    // Uri trims leading white space, so each of these would compose another address than the one judged.
    [InlineData(" //other.example/x")]
    [InlineData(" ../x")]
    [InlineData("\t/x")]
    [InlineData("a/b\n")]
    [InlineData("a b")]
    public async Task RefusesAPathThatIsNotRelative(string path)
    {
        // Beneath a host's root, "../x" or "x?y=1" composes an address still beneath the base: only the path's form
        // refuses it there. Beneath a mirror's path, the composed address is checked as well.
        foreach (var baseAddress in new[] { "https://fixture.test/", "https://mirror.test/qc" })
        {
            var host = new FixtureHost { BaseAddress = new Uri(baseAddress) };
            var entry = host.AddQuarter("2026Q3");
            host.AddFile(entry, EvidenceTable.Accounts, Body(100));
            entry["accounts_path"] = path;
            host.AddIndex(Body(100));
            host.Manifest["index"]!["submissions_path"] = path;
            using var client = host.CreateClient();

            var e = await Assert.ThrowsAsync<DataIntegrityException>(
                () => client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, Destination()));
            await Assert.ThrowsAsync<DataIntegrityException>(
                () => client.Data.DownloadIndexAsync(Destination("submissions.parquet")));

            Assert.Contains($"\"{path}\"", e.Message);
            Assert.Equal(2, host.Requests.Count);
            Assert.All(host.Requests, request => Assert.Equal(ManifestUri(host), request.Uri));
        }
    }

    [Fact]
    public async Task SaysWhatIsNotPublished()
    {
        var host = new FixtureHost();
        var listed = host.AddQuarter("2026Q3");
        host.AddFile(listed, EvidenceTable.Accounts, Body(100));
        var unbuilt = host.AddQuarter("2026Q2");
        unbuilt.Remove("accounts_path");
        unbuilt["profiles_path"] = "curated/profiles/quarter=2026Q2/";
        host.AddQuarter("2026Q1")["quorum"] = new JsonObject { ["expected"] = 4300 };
        using var client = host.CreateClient();

        Assert.Contains("2031Q1", await Unavailable(client, "2031Q1", EvidenceTable.Accounts));
        Assert.Contains("2026Q2", await Unavailable(client, "2026Q2", EvidenceTable.Profiles));
        var profiles = await Unavailable(client, "2026Q3", EvidenceTable.Profiles);
        Assert.Contains("profiles_path", profiles);
        Assert.Contains("Profiles", profiles);
        Assert.Contains("2026Q3", profiles);
        Assert.Contains("quorum.detail_path", await Unavailable(client, "2026Q3", EvidenceTable.Quorum));
        Assert.Contains("quorum.detail_path", await Unavailable(client, "2026Q1", EvidenceTable.Quorum));
        Assert.Contains("accounts_text_path", await Unavailable(client, "2026Q3", EvidenceTable.AccountText));
        var index = await Assert.ThrowsAsync<DataUnavailableException>(
            () => client.Data.DownloadIndexAsync(Destination()));
        Assert.Contains("index.submissions_path", index.Message);

        Assert.Equal(7, host.Requests.Count);
        Assert.All(host.Requests, request => Assert.Equal(ManifestUri(host), request.Uri));

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.Data.DownloadAsync(default, EvidenceTable.Accounts, Destination()));

        Assert.Equal(7, host.Requests.Count);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Destination())));
    }

    [Fact]
    public async Task RefusesBadArgumentsBeforeAnyRequest()
    {
        var host = new FixtureHost();
        host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, Body(100));
        host.AddIndex(Body(100));
        using var client = host.CreateClient();

        foreach (var path in new[] { null, "", "   ", "out/" })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, path!));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => client.Data.DownloadIndexAsync(path!));
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.Data.DownloadAsync(Q2026Q3, (EvidenceTable)99, Destination()));

        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task PassesAMissingFileThrough()
    {
        var host = new FixtureHost();
        var file = host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, Body(100));
        host.Bodies.Remove(file);
        using var client = host.CreateClient();
        var path = Destination();

        var e = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, path));

        Assert.Equal(HttpStatusCode.NotFound, e.StatusCode);
        Assert.Equal(1, ManifestRequests(host));
        Assert.Equal(2, host.Requests.Count);
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cache")]
    public async Task LeavesNoTemporaryFileWhenCancelled(string? cacheName)
    {
        var host = new FixtureHost();
        var body = Body(200_000);
        var file = host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, body);
        var fileUri = new Uri(host.BaseAddress, file);
        var stalling = new CutShortStream(body[..1000], Cut.Stall);
        host.OnRequest = request => request.RequestUri == fileUri ? Serve(stalling) : null;
        var cache = cacheName is null ? null : Path.Combine(_root, cacheName);
        using var client = host.CreateClient(cache);
        var path = Destination();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var earlier = Body(10, seed: 3);
        await File.WriteAllBytesAsync(path, earlier);
        using var cancel = new CancellationTokenSource();

        var download = client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, path, cancel.Token);
        await stalling.HeadRead.Task.WaitAsync(LongWait);

        // The download is under way, in a temporary file of its own: in the cache, or beside the destination.
        Assert.Single(Directory.GetFiles(cache ?? directory), name => name != path);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        Assert.Equal(earlier, await File.ReadAllBytesAsync(path));
        if (cache is not null)
        {
            Assert.Empty(Directory.GetFileSystemEntries(cache));
        }
    }

    [Fact]
    public async Task UsesTheCache()
    {
        var host = new FixtureHost();
        var body = Body(5000);
        host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, body);
        var cache = Path.Combine(_root, "cache");
        using (var first = host.CreateClient(cache))
        {
            await first.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, Destination("first.parquet"));
        }

        var earlier = host.Requests.Count;
        using var second = host.CreateClient(cache);
        var path = Destination("second.parquet");

        var saved = await second.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, path);

        Assert.Equal(new[] { ManifestUri(host) }, host.Requests.Skip(earlier).Select(r => r.Uri));
        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        Assert.Equal(Digest(body), saved.Sha256);
        Assert.Equal(new[] { Digest(body) + ".parquet" }, FileNames(cache));
    }

    [Theory]
    [InlineData("other bytes of the same length")]
    [InlineData("fewer bytes")]
    [InlineData("more bytes")]
    public async Task ReplacesACorruptCachedFile(string corruption)
    {
        var host = new FixtureHost();
        var body = Body(5000);
        var file = host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, body);
        var cache = Path.Combine(_root, "cache");
        using var client = host.CreateClient(cache);
        await client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, Destination("first.parquet"));
        var cached = Path.Combine(cache, Digest(body) + ".parquet");
        var corrupt = corruption switch
        {
            "fewer bytes" => body[..4000],
            "more bytes" => [.. body, .. Body(10, seed: 3)],
            _ => Changed(body, at: 0),
        };
        await File.WriteAllBytesAsync(cached, corrupt);
        var path = Destination("second.parquet");

        await client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, path);

        Assert.Equal(2, host.Requests.Count(r => r.Uri == new Uri(host.BaseAddress, file)));
        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        Assert.Equal(body, await File.ReadAllBytesAsync(cached));
        Assert.Equal(new[] { Digest(body) + ".parquet" }, FileNames(cache));
    }

    [UnixPermissionsFact]
    [UnsupportedOSPlatform("windows")]
    public async Task ReplacesACachedFileThatCannotBeOpened()
    {
        var host = new FixtureHost();
        var body = Body(5000);
        var file = host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, body);
        var fileUri = new Uri(host.BaseAddress, file);
        var cache = Path.Combine(_root, "cache");
        using var client = host.CreateClient(cache);
        await client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, Destination("first.parquet"));
        var cached = Path.Combine(cache, Digest(body) + ".parquet");
        File.SetUnixFileMode(cached, UnixFileMode.None);
        var path = Destination("second.parquet");

        await client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, path);

        Assert.Equal(2, host.Requests.Count(r => r.Uri == fileUri));
        Assert.Equal(body, await File.ReadAllBytesAsync(path));
        Assert.Equal(body, await File.ReadAllBytesAsync(cached));

        // A read through the cache agrees.
        File.SetUnixFileMode(cached, UnixFileMode.None);
        var hostClient = Host(host);
        var evidence = new EvidenceResolver(
            new ManifestReader(hostClient), new VerifiedFileStore(hostClient, cache), hostClient);

        var opened = await evidence.OpenAsync(Q2026Q3, EvidenceTable.Accounts, CancellationToken.None);

        Assert.NotNull(opened);
        await opened.DisposeAsync();
        Assert.Equal(3, host.Requests.Count(r => r.Uri == fileUri));
        Assert.Equal(body, await File.ReadAllBytesAsync(cached));
        Assert.Equal(new[] { Digest(body) + ".parquet" }, FileNames(cache));
    }

    [Fact]
    public async Task SharesACacheBetweenTwoReads()
    {
        var host = new FixtureHost();
        var body = Body(200_000);
        var file = host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, body);
        var fileUri = new Uri(host.BaseAddress, file);
        // Neither file request is answered until both have arrived, so the two reads overlap.
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Hold = async (request, token) =>
        {
            if (request.RequestUri != fileUri)
            {
                return;
            }

            if (Interlocked.Increment(ref arrived) == 2)
            {
                both.TrySetResult();
            }

            await both.Task.WaitAsync(LongWait, token);
        };
        var cache = Path.Combine(_root, "cache");
        using var client = host.CreateClient(cache);
        var one = Destination("one.parquet");
        var two = Destination("two.parquet");

        await Task.WhenAll(
            client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, one),
            client.Data.DownloadAsync(Q2026Q3, EvidenceTable.Accounts, two));

        Assert.Equal(2, host.Requests.Count(r => r.Uri == fileUri));
        Assert.Equal(body, await File.ReadAllBytesAsync(one));
        Assert.Equal(body, await File.ReadAllBytesAsync(two));
        Assert.Equal(new[] { Digest(body) + ".parquet" }, FileNames(cache));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cache")]
    public async Task OpensAVerifiedFile(string? cacheName)
    {
        var host = new FixtureHost();
        var body = Body(5000);
        host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, body);
        var cache = cacheName is null ? null : Path.Combine(_root, cacheName);
        var hostClient = Host(host);
        var evidence = new EvidenceResolver(
            new ManifestReader(hostClient), new VerifiedFileStore(hostClient, cache), hostClient);

        var file = await evidence.OpenAsync(Q2026Q3, EvidenceTable.Accounts, CancellationToken.None);

        Assert.NotNull(file);
        await using (var stream = file.OpenRead())
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(body, copy.ToArray());
        }

        var expected = cache ?? Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        Assert.Equal(expected, Path.GetDirectoryName(file.Path));
        await file.DisposeAsync();
        // A temporary file is deleted; a cached one stays for the next read.
        Assert.Equal(cache is not null, File.Exists(file.Path));

        var before = host.Requests.Count;

        Assert.Null(await evidence.OpenAsync(Q2026Q3, EvidenceTable.AccountText, CancellationToken.None));
        Assert.Equal(new[] { ManifestUri(host) }, host.Requests.Skip(before).Select(r => r.Uri));
    }

    // Bytes that stand for a published file: the same for the same length and seed.
    private static byte[] Body(int length, int seed = 1)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Changed(byte[] body, int at)
    {
        var changed = body.ToArray();
        changed[at] ^= 0xFF;
        return changed;
    }

    private static string Digest(byte[] body)
    {
        return Convert.ToHexStringLower(SHA256.HashData(body));
    }

    private static Uri ManifestUri(FixtureHost host)
    {
        return new Uri(new Uri(host.BaseAddress.AbsoluteUri.TrimEnd('/') + "/"), FixtureHost.ManifestPath);
    }

    private static int ManifestRequests(FixtureHost host)
    {
        return host.Requests.Count(request => request.Uri == ManifestUri(host));
    }

    private static string[] FileNames(string directory)
    {
        return [.. Directory.GetFileSystemEntries(directory).Select(entry => Path.GetFileName(entry)).Order()];
    }

    private static HttpResponseMessage Serve(Stream body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
    }

    private static HostClient Host(FixtureHost host)
    {
        var http = new HttpClient(host, disposeHandler: false);
        return new HostClient(http, host.BaseAddress, Timeout.InfiniteTimeSpan);
    }

    // Where a test saves a file: in a directory that does not exist until the download makes it.
    private string Destination(string name = "accounts.parquet")
    {
        return Path.Combine(_root, "out", name);
    }

    private async Task<string> Unavailable(QuarterCallClient client, string quarter, EvidenceTable table)
    {
        var e = await Assert.ThrowsAsync<DataUnavailableException>(
            () => client.Data.DownloadAsync(Quarter.Parse(quarter), table, Destination()));
        return e.Message;
    }

    // What a cut-short body does once its head is read.
    private enum Cut
    {
        // Waits after the head until the read is cancelled.
        Stall,

        // Throws after the head, as a dropped connection does.
        Break,

        // Ends after the head.
        End,
    }

    // A body that serves its head, then stalls, breaks or ends, after running atCut.
    private sealed class CutShortStream(byte[] head, Cut cut, Action? atCut = null) : Stream
    {
        private int _position;

        // Set once the head has been read.
        public TaskCompletionSource HeadRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < head.Length)
            {
                var count = Math.Min(buffer.Length, head.Length - _position);
                head.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            HeadRead.TrySetResult();
            atCut?.Invoke();
            if (cut == Cut.Break)
            {
                throw new IOException("The connection was reset.");
            }

            if (cut == Cut.Stall)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
