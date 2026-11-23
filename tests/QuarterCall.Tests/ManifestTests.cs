using System.Net;
using System.Text.Json.Nodes;
using QuarterCall.Internal;
using QuarterCall.Tests.Support;
using Xunit;

namespace QuarterCall.Tests;

public class ManifestTests
{
    [Fact]
    public async Task ListsReadableQuartersNewestFirst()
    {
        var host = new FixtureHost();
        host.AddQuarter("2019Q1");
        host.AddQuarter("2026Q3");
        host.AddQuarter("2026Q2");
        host.AddQuarter("2025Q4").Remove("accounts_path");
        using var client = host.CreateClient();

        var quarters = await client.Data.GetQuartersAsync();

        Assert.Equal(new[] { "2026Q3", "2026Q2", "2019Q1" }, quarters.Select(q => q.Quarter.ToString()));
        var newest = quarters[0];
        Assert.Equal(new DateOnly(2026, 9, 30), newest.CycleDate);
        Assert.Equal(QuarterStatus.Open, newest.Status);
        Assert.Equal("curated/accounts/quarter=2026Q3/", newest.AccountsPath);

        // The order is the client's own, not the order the host lists them in.
        var entries = host.Manifest["quarters"]!.AsArray();
        var oldestFirst = entries.Reverse().ToList();
        entries.Clear();
        foreach (var entry in oldestFirst)
        {
            entries.Add(entry);
        }

        var reread = await client.Data.GetQuartersAsync();

        Assert.Equal(new[] { "2026Q3", "2026Q2", "2019Q1" }, reread.Select(q => q.Quarter.ToString()));
    }

    [Fact]
    public async Task ReadsAnUnknownStatusAsOpen()
    {
        var host = new FixtureHost();
        host.AddQuarter("2026Q3")["status"] = "sealed";
        var dormant = host.AddQuarter("2019Q1");
        dormant["status"] = "dormant";
        dormant["dormant_since"] = "2022-09-21";
        using var client = host.CreateClient();

        var quarters = await client.Data.GetQuartersAsync();

        Assert.Equal(QuarterStatus.Open, quarters[0].Status);
        Assert.Null(quarters[0].DormantSince);
        Assert.Equal(QuarterStatus.Dormant, quarters[1].Status);
        Assert.Equal(new DateOnly(2022, 9, 21), quarters[1].DormantSince);
    }

    [Fact]
    public async Task ReadsEveryPublishedField()
    {
        var host = new FixtureHost();
        var entry = host.AddQuarter("2026Q3");
        entry["submission_count"] = 4661;
        entry["cu_count"] = 4271;
        entry["counts_at"] = "2026-11-22T21:46:36Z";
        entry["accounts_sha256"] = "8cb3";
        entry["accounts_bytes"] = 25218914L;
        entry["accounts_text_path"] = "curated/accounts_text/quarter=2026Q3/";
        entry["accounts_text_sha256"] = "ef5a";
        entry["accounts_text_bytes"] = 99371L;
        entry["profiles_path"] = "curated/profiles/quarter=2026Q3/";
        entry["profiles_sha256"] = "0b4f";
        entry["profiles_bytes"] = 1539275L;
        entry["profile_rows"] = 4271;
        entry["profiles_missing"] = 0;
        entry["updated_at"] = "2026-11-22T05:37:55+01:00";
        entry["quorum"] = new JsonObject { ["expected"] = 4300 };
        host.Manifest["index"] = new JsonObject
        {
            ["submissions_path"] = "index/submissions.parquet",
            ["submissions_sha256"] = "b757",
            ["submissions_bytes"] = 13694750L,
        };
        using var client = host.CreateClient();

        var quarter = Assert.Single(await client.Data.GetQuartersAsync());

        Assert.Equal(4661, quarter.SubmissionCount);
        Assert.Equal(4271, quarter.CuCount);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 21, 46, 36, TimeSpan.Zero), quarter.CountsAt);
        Assert.Equal("8cb3", quarter.AccountsSha256);
        Assert.Equal(25218914L, quarter.AccountsBytes);
        Assert.Equal("curated/accounts_text/quarter=2026Q3/", quarter.AccountsTextPath);
        Assert.Equal("ef5a", quarter.AccountsTextSha256);
        Assert.Equal(99371L, quarter.AccountsTextBytes);
        Assert.Equal("curated/profiles/quarter=2026Q3/", quarter.ProfilesPath);
        Assert.Equal("0b4f", quarter.ProfilesSha256);
        Assert.Equal(1539275L, quarter.ProfilesBytes);
        Assert.Equal(4271, quarter.ProfileRows);
        Assert.Equal(0, quarter.ProfilesMissing);
        // A time is given in UTC whatever offset the host wrote it with.
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 4, 37, 55, TimeSpan.Zero), quarter.UpdatedAt);
        Assert.Equal(TimeSpan.Zero, quarter.UpdatedAt!.Value.Offset);

        var snapshot = await new ManifestReader(Host(host)).ReadAsync(CancellationToken.None);

        Assert.Equal("index/submissions.parquet", snapshot.SubmissionsPath);
        Assert.Equal("b757", snapshot.SubmissionsSha256);
        Assert.Equal(13694750L, snapshot.SubmissionsBytes);
    }

    [Fact]
    public async Task KeepsAbsentApartFromNull()
    {
        var host = new FixtureHost();
        var zero = host.AddQuarter("2026Q3");
        zero["cu_count"] = 0;
        zero["accounts_bytes"] = 0L;
        host.AddQuarter("2026Q2").Remove("cycle_date");
        using var client = host.CreateClient();

        var quarters = await client.Data.GetQuartersAsync();

        Assert.Equal(0, quarters[0].CuCount);
        Assert.Equal(0L, quarters[0].AccountsBytes);
        var bare = quarters[1];
        Assert.Null(bare.CuCount);
        Assert.Null(bare.UpdatedAt);
        Assert.Null(bare.AccountsSha256);
        Assert.Null(bare.AccountsTextSha256);
        Assert.Null(bare.ProfilesSha256);
        Assert.Null(bare.AccountsBytes);
        Assert.Equal(new DateOnly(2026, 6, 30), bare.CycleDate);

        var snapshot = await new ManifestReader(Host(host)).ReadAsync(CancellationToken.None);

        Assert.Null(snapshot.SubmissionsPath);
        Assert.Null(snapshot.SubmissionsSha256);
        Assert.Null(snapshot.SubmissionsBytes);

        using var empty = new FixtureHost().CreateClient();

        Assert.Empty(await empty.Data.GetQuartersAsync());
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("")]
    [InlineData("one")]
    [InlineData(null)]
    public async Task RefusesAnotherMajor(string? version)
    {
        var host = new FixtureHost();
        host.AddQuarter("2026Q3");
        host.Manifest.Remove("schema_version");
        if (version is not null)
        {
            host.Manifest["schema_version"] = version;
        }

        using var client = host.CreateClient();

        var e = await Assert.ThrowsAsync<UnsupportedSchemaException>(() => client.Data.GetQuartersAsync());

        Assert.Contains("upgrade", e.Message);
    }

    [Fact]
    public async Task ReadsANewerMinor()
    {
        var host = new FixtureHost();
        host.Manifest["schema_version"] = "1.9";
        host.Manifest["coming_soon"] = new JsonObject { ["nested"] = new JsonArray(1, 2) };
        host.AddQuarter("2026Q3")["coming_soon"] = "x";
        using var client = host.CreateClient();

        var quarter = Assert.Single(await client.Data.GetQuartersAsync());

        Assert.Equal(Quarter.Parse("2026Q3"), quarter.Quarter);
    }

    [Fact]
    public async Task RefusesWhatIsNotAManifest()
    {
        var html = new FixtureHost
        {
            OnRequest = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") },
        };
        await AssertMalformed(html);

        var textual = new FixtureHost();
        textual.Manifest["quarters"] = "x";
        await AssertMalformed(textual);

        var unnamed = new FixtureHost();
        unnamed.AddQuarter("2026Q3").Remove("quarter");
        await AssertMalformed(unnamed);

        var misnamed = new FixtureHost();
        misnamed.AddQuarter("2026Q3")["quarter"] = "2026Q5";
        await AssertMalformed(misnamed);

        var misdated = new FixtureHost();
        misdated.AddQuarter("2026Q3")["cycle_date"] = "30/09/2026";
        await AssertMalformed(misdated);

        var listless = new FixtureHost();
        listless.Manifest.Remove("quarters");
        await AssertMalformed(listless);

        var newer = new FixtureHost();
        newer.Manifest["schema_version"] = "2.0";
        newer.Manifest["quarters"] = "x";
        using var client = newer.CreateClient();

        await Assert.ThrowsAsync<UnsupportedSchemaException>(() => client.Data.GetQuartersAsync());
    }

    private static async Task AssertMalformed(FixtureHost host)
    {
        using var client = host.CreateClient();

        var e = await Assert.ThrowsAsync<DataIntegrityException>(() => client.Data.GetQuartersAsync());

        Assert.IsType<System.Text.Json.JsonException>(e.InnerException);
    }

    private static HostClient Host(FixtureHost host)
    {
        var http = new HttpClient(host, disposeHandler: false);
        return new HostClient(http, host.BaseAddress, Timeout.InfiniteTimeSpan);
    }
}
