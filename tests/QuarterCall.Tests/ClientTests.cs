using System.Net;
using QuarterCall.Internal;
using QuarterCall.Tests.Support;
using Xunit;

namespace QuarterCall.Tests;

// One test here changes the process's working directory, so the class runs alone, after every other.
[Collection(WorkingDirectoryCollection.Name)]
public class ClientTests
{
    // Long enough that a test which passes never waits for it, short enough that one which fails ends.
    private static readonly TimeSpan LongDelay = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task NamesItselfToTheHost()
    {
        var host = new FixtureHost();
        using var client = host.CreateClient();

        await client.Data.GetQuartersAsync();

        var request = Assert.Single(host.Requests);
        Assert.Matches(@"^quartercall-csharp/\d+\.\d+\.\d+$", request.UserAgent);
    }

    [Theory]
    [InlineData("https://fixture.test", "https://fixture.test/index/manifest.json")]
    [InlineData("https://fixture.test/", "https://fixture.test/index/manifest.json")]
    [InlineData("https://mirror.test/qc", "https://mirror.test/qc/index/manifest.json")]
    public async Task StaysBeneathItsBaseAddress(string baseAddress, string expected)
    {
        var host = new FixtureHost { BaseAddress = new Uri(baseAddress) };
        using var client = host.CreateClient();

        await client.Data.GetQuartersAsync();

        Assert.Equal(new Uri(expected), Assert.Single(host.Requests).Uri);
    }

    [Fact]
    public async Task LeavesASuppliedHttpClientAlone()
    {
        var host = new FixtureHost();
        using var http = new HttpClient(host, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(42) };
        var options = new QuarterCallOptions { DataBaseAddress = host.BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var client = new QuarterCallClient(http, options);
        await client.Data.GetQuartersAsync();

        client.Dispose();

        Assert.Equal(TimeSpan.FromSeconds(42), http.Timeout);
        Assert.Empty(http.DefaultRequestHeaders);
        using var response = await http.GetAsync(new Uri(host.BaseAddress, FixtureHost.ManifestPath));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PassesAFailedRequestThrough()
    {
        var host = new FixtureHost { OnRequest = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };
        using var client = host.CreateClient();

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.Data.GetQuartersAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, e.StatusCode);
    }

    [Fact]
    public async Task TimesOut()
    {
        var host = new FixtureHost { Delay = LongDelay };
        using var http = new HttpClient(host, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var options = new QuarterCallOptions
        {
            DataBaseAddress = host.BaseAddress,
            Timeout = TimeSpan.FromMilliseconds(50),
        };
        using var client = new QuarterCallClient(http, options);

        var late = await Assert.ThrowsAsync<TaskCanceledException>(() => client.Data.GetQuartersAsync());

        Assert.IsType<TimeoutException>(late.InnerException);

        using var patient = host.CreateClient();
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => patient.Data.GetQuartersAsync(caller.Token));

        Assert.False(cancelled.InnerException is TimeoutException);
    }

    [Fact]
    public async Task TimesOutWhileReadingTheBody()
    {
        var host = new FixtureHost();
        using var http = new HttpClient(host, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var hostClient = new HostClient(http, host.BaseAddress, TimeSpan.FromMilliseconds(50));
        var uri = hostClient.Resolve(FixtureHost.ManifestPath);

        var late = await Assert.ThrowsAsync<TaskCanceledException>(() => hostClient.ReadAsync(
            uri, async (_, token) => { await Task.Delay(LongDelay, token); return 0; }, CancellationToken.None));

        Assert.IsType<TimeoutException>(late.InnerException);
    }

    [Fact]
    public void RefusesARelativeBaseAddress()
    {
        var options = new QuarterCallOptions { DataBaseAddress = new Uri("qc/", UriKind.Relative) };

        Assert.Throws<ArgumentException>(() => new QuarterCallClient(options));
    }

    [Theory]
    [InlineData("ftp://mirror.test/qc/")]
    [InlineData("file:///srv/qc/")]
    public void RefusesABaseAddressOffTheWeb(string address)
    {
        var options = new QuarterCallOptions { DataBaseAddress = new Uri(address) };

        Assert.Throws<ArgumentException>(() => new QuarterCallClient(options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void RefusesATimeoutThatIsNotPositive(int seconds)
    {
        var options = new QuarterCallOptions { Timeout = TimeSpan.FromSeconds(seconds) };

        Assert.Throws<ArgumentOutOfRangeException>(() => new QuarterCallClient(options));
    }

    [Fact]
    public void RefusesATimeoutOver24Days()
    {
        var longest = new QuarterCallOptions { Timeout = TimeSpan.FromDays(24) };
        var longer = new QuarterCallOptions { Timeout = TimeSpan.FromDays(24) + TimeSpan.FromMilliseconds(1) };

        using var client = new QuarterCallClient(longest);
        var e = Assert.Throws<ArgumentOutOfRangeException>(() => new QuarterCallClient(longer));

        Assert.Contains("at most 24 days", e.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // A path no file system accepts.
    [InlineData("a\0b")]
    public void RefusesAnUnusableCacheDirectory(string cacheDirectory)
    {
        var options = new QuarterCallOptions { CacheDirectory = cacheDirectory };
        using var http = new HttpClient();

        var owned = Assert.Throws<ArgumentException>(() => new QuarterCallClient(options));
        var supplied = Assert.Throws<ArgumentException>(() => new QuarterCallClient(http, options));

        Assert.Equal("options", owned.ParamName);
        Assert.Equal("options", supplied.ParamName);
        // The runtime's own refusal is kept inside.
        Assert.Equal(cacheDirectory.Contains('\0'), owned.InnerException is ArgumentException);
        // Null still means no cache.
        using var uncached = new QuarterCallClient(new QuarterCallOptions { CacheDirectory = null });
    }

    [Fact]
    public async Task KeepsARelativeCacheWhereItWasNamed()
    {
        var host = new FixtureHost();
        var body = new byte[] { 1, 2, 3, 4 };
        host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, body);
        var named = Directory.CreateTempSubdirectory("client-tests-").FullName;
        var later = Directory.CreateTempSubdirectory("client-tests-").FullName;
        var original = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(named);
            using var client = host.CreateClient("cache");
            Directory.SetCurrentDirectory(later);

            await client.Data.DownloadAsync(
                Quarter.Parse("2026Q3"), EvidenceTable.Accounts, Path.Combine(later, "accounts.parquet"));

            // The cache is beneath the directory that was current when the client was built.
            Assert.Single(Directory.GetFiles(Path.Combine(named, "cache")));
            Assert.False(Directory.Exists(Path.Combine(later, "cache")));
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            Directory.Delete(named, recursive: true);
            Directory.Delete(later, recursive: true);
        }
    }

    [Fact]
    public void DefaultsToThePublicHost()
    {
        var options = new QuarterCallOptions();

        Assert.Equal(new Uri("https://data.quartercall.com/"), options.DataBaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(100), options.Timeout);
        Assert.Null(options.CacheDirectory);
    }
}

/// <summary>Tests that must not run beside any other, such as one that changes the working directory.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkingDirectoryCollection
{
    /// <summary>The collection's name.</summary>
    public const string Name = "Working directory";
}
