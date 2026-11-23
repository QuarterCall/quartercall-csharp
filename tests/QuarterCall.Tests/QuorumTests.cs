using System.Text.Json.Nodes;
using QuarterCall.Tests.Support;
using Xunit;

namespace QuarterCall.Tests;

public class QuorumTests
{
    private const string Quorum2026Q3 = """
{
  "measurement": "filing-quorum/2",
  "measured_at": "2026-11-21T05:11:05Z",
  "due_date": "2026-10-30",
  "expected_basis": "prior_cycle",
  "weights_from": "2026Q2",
  "expected": 4300,
  "filed": 4271,
  "filed_unweighted": 0,
  "filed_by_charter": 0,
  "outstanding_count": 29,
  "excused": 0,
  "assets_expected": 2522717623724,
  "assets_filed": 2519499535513,
  "largest_outstanding_assets": 1440630816,
  "coverage": {
    "by_count": 0.9932558139534884,
    "by_assets": 0.9987243565507544,
    "by_assets_ceiling": 1,
    "ceiling_basis": "excuses",
    "largest_outstanding_share": 0.0005710630482191507
  },
  "verdicts": {
    "floor/1": {
      "status": "reached",
      "reached_at": "2026-10-31T05:14:53Z",
      "reached_at_basis": "observed"
    },
    "default/1": {
      "status": "reached",
      "reached_at": "2026-10-31T05:14:53Z",
      "reached_at_basis": "observed"
    }
  },
  "materiality": 0.0025,
  "outstanding_material": [],
  "detail_path": "index/quorum/quarter=2026Q3/data.parquet",
  "detail_sha256": "60194807b6534a558c5829b2e0e8b9f0545be972d00172c3b96e19b0ba16fe9f",
  "detail_bytes": 131121
}
""";

    private static async Task<PublishedQuarter> ReadAsync(JsonNode? quorum)
    {
        var host = new FixtureHost();
        var entry = host.AddQuarter("2026Q3");
        if (quorum is not null)
        {
            entry["quorum"] = quorum;
        }

        using var client = host.CreateClient();
        return Assert.Single(await client.Data.GetQuartersAsync());
    }

    private static async Task<FilingQuorum> ReadQuorumAsync(JsonObject quorum)
    {
        var quarter = await ReadAsync(quorum);
        return Assert.IsType<FilingQuorum>(quarter.Quorum);
    }

    private static JsonObject Published() => JsonNode.Parse(Quorum2026Q3)!.AsObject();

    private static JsonObject WithVerdict(string status)
    {
        var quorum = Published();
        quorum["verdicts"] = new JsonObject { ["floor/1"] = new JsonObject { ["status"] = status } };
        return quorum;
    }

    [Fact]
    public async Task ReadsTheTerms()
    {
        var quorum = await ReadQuorumAsync(Published());

        Assert.Equal(4300, quorum.Expected);
        Assert.Equal(4271, quorum.Filed);
        Assert.Equal(2522717623724L, quorum.AssetsExpected);
        Assert.Equal(new DateOnly(2026, 10, 30), quorum.DueDate);
        Assert.Equal(Quarter.Parse("2026Q2"), quorum.WeightsFrom);
        Assert.Equal(131121L, quorum.DetailBytes);
        Assert.Equal("filing-quorum/2", quorum.Measurement);
        Assert.Equal("prior_cycle", quorum.ExpectedBasis);
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 5, 11, 5, TimeSpan.Zero), quorum.MeasuredAt);
        Assert.Equal(29, quorum.OutstandingCount);
        Assert.Equal(0.0025m, quorum.Materiality);
        Assert.Equal("excuses", quorum.CoverageCeilingBasis);
        Assert.Equal("index/quorum/quarter=2026Q3/data.parquet", quorum.DetailPath);
        Assert.Equal(2, quorum.Verdicts.Count);
        Assert.Equal("observed", quorum.Verdicts["floor/1"].ReachedAtBasis);

        Assert.Equal(0, quorum.FiledUnweighted);
        Assert.Equal(0, quorum.FiledByCharter);
        Assert.Equal(0, quorum.Excused);
        Assert.Equal(2519499535513L, quorum.AssetsFiled);
        Assert.Equal(1440630816L, quorum.LargestOutstandingAssets);
        Assert.Equal(0.9932558139534884m, quorum.CoverageByCount);
        Assert.Equal(0.9987243565507544m, quorum.CoverageByAssets);
        Assert.Equal(1m, quorum.CoverageByAssetsCeiling);
        Assert.Equal(0.0005710630482191507m, quorum.CoverageLargestOutstandingShare);
        Assert.Equal("60194807b6534a558c5829b2e0e8b9f0545be972d00172c3b96e19b0ba16fe9f", quorum.DetailSha256);
        Assert.Equal(new[] { "floor/1", "default/1" }, quorum.Verdicts.Keys.OrderByDescending(k => k));
        foreach (var verdict in quorum.Verdicts.Values)
        {
            Assert.Equal(QuorumStatus.Reached, verdict.Status);
            Assert.Equal(new DateTimeOffset(2026, 10, 31, 5, 14, 53, TimeSpan.Zero), verdict.ReachedAt);
            Assert.Null(verdict.BelowSince);
        }

        Assert.Empty(quorum.OutstandingMaterial);
    }

    [Fact]
    public async Task ReadsEveryTimeAsUtc()
    {
        var quorum = Published();
        quorum["measured_at"] = "2026-11-21T07:11:05+02:00";
        quorum["verdicts"] = new JsonObject
        {
            ["floor/1"] = new JsonObject
            {
                ["status"] = "reached",
                ["reached_at"] = "2026-10-31T07:14:53+02:00",
                ["below_since"] = "2026-10-30T07:14:53+02:00",
            },
        };

        var read = await ReadQuorumAsync(quorum);

        Assert.Equal(new DateTimeOffset(2026, 11, 21, 5, 11, 5, TimeSpan.Zero), read.MeasuredAt);
        Assert.Equal(TimeSpan.Zero, read.MeasuredAt!.Value.Offset);
        var verdict = read.Verdicts["floor/1"];
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 5, 14, 53, TimeSpan.Zero), verdict.ReachedAt);
        Assert.Equal(TimeSpan.Zero, verdict.ReachedAt!.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 30, 5, 14, 53, TimeSpan.Zero), verdict.BelowSince);
        Assert.Equal(TimeSpan.Zero, verdict.BelowSince!.Value.Offset);
    }

    [Fact]
    public async Task ReadsAVerdictWithoutAStatusAsUnknown()
    {
        var quorum = Published();
        quorum["verdicts"] = new JsonObject { ["floor/1"] = new JsonObject() };

        var read = await ReadQuorumAsync(quorum);

        Assert.Equal(QuorumStatus.Unknown, read.Verdicts["floor/1"].Status);
        Assert.False(read.HasReached("floor/1"));
    }

    [Theory]
    [InlineData("verdicts")]
    [InlineData("outstanding_material")]
    public async Task RefusesANullCollection(string member)
    {
        var quorum = Published();
        quorum[member] = null;

        await Assert.ThrowsAsync<DataIntegrityException>(() => ReadAsync(quorum));
    }

    [Fact]
    public async Task RefusesANullVerdict()
    {
        var quorum = Published();
        quorum["verdicts"] = new JsonObject { ["floor/1"] = null };

        await Assert.ThrowsAsync<DataIntegrityException>(() => ReadAsync(quorum));
    }

    [Fact]
    public async Task RefusesANullOutstandingFiler()
    {
        var quorum = Published();
        quorum["outstanding_material"] = new JsonArray(new JsonObject { ["cuid"] = 1 }, null);

        await Assert.ThrowsAsync<DataIntegrityException>(() => ReadAsync(quorum));
    }

    [Fact]
    public async Task KeepsCoverageExact()
    {
        var quorum = Published();
        var byCount = JsonNode.Parse("0.9914667896678966789667896679")!;
        quorum["coverage"]!["by_count"] = byCount;

        var read = await ReadQuorumAsync(quorum);

        Assert.Equal(0.9914667896678966789667896679m, read.CoverageByCount);
    }

    [Theory]
    [InlineData("reached", true, QuorumStatus.Reached)]
    [InlineData("below", false, QuorumStatus.Below)]
    [InlineData("indeterminate", false, QuorumStatus.Indeterminate)]
    [InlineData("maybe", false, QuorumStatus.Unknown)]
    public async Task ReachesOnlyOnReached(string status, bool reached, QuorumStatus expected)
    {
        var quorum = await ReadQuorumAsync(WithVerdict(status));

        Assert.Equal(expected, quorum.Verdicts["floor/1"].Status);
        Assert.Equal(reached, quorum.HasReached("floor/1"));
        Assert.False(quorum.HasReached("floor/2"));
        Assert.Throws<ArgumentNullException>(() => quorum.HasReached(null!));
    }

    [Fact]
    public async Task ComputesNoVerdict()
    {
        // Every term would pass any rule; the verdict says below, and below is what is read.
        var quorum = WithVerdict("below");
        quorum["expected"] = 100;
        quorum["filed"] = 100;
        quorum["outstanding_count"] = 0;
        quorum["assets_filed"] = quorum["assets_expected"]!.DeepClone();
        quorum["coverage"]!["by_count"] = 1;
        quorum["coverage"]!["by_assets"] = 1;

        var read = await ReadQuorumAsync(quorum);

        Assert.Equal(QuorumStatus.Below, read.Verdicts["floor/1"].Status);
        Assert.False(read.HasReached("floor/1"));
    }

    [Fact]
    public async Task ReadsTheTimesOfAVerdict()
    {
        var quorum = Published();
        quorum["verdicts"] = new JsonObject
        {
            ["floor/1"] = new JsonObject { ["status"] = "below", ["below_since"] = "2026-11-01T00:00:00Z" },
        };

        var read = await ReadQuorumAsync(quorum);

        var verdict = read.Verdicts["floor/1"];
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), verdict.BelowSince);
        Assert.Null(verdict.ReachedAt);
    }

    [Fact]
    public async Task ListsOutstandingFilers()
    {
        var quorum = Published();
        quorum["outstanding_material"] = new JsonArray(
            new JsonObject
            {
                ["cuid"] = 7,
                ["cu_number"] = 12345,
                ["last_known_assets"] = 987654321L,
                ["share"] = 0.0031m,
            },
            new JsonObject { ["cuid"] = 8, ["cu_number"] = 54321, ["name"] = "Example FCU" });

        var read = await ReadQuorumAsync(quorum);

        Assert.Equal(2, read.OutstandingMaterial.Count);
        var first = read.OutstandingMaterial[0];
        Assert.Null(first.Name);
        Assert.Equal(7, first.Cuid);
        Assert.Equal(12345, first.CuNumber);
        Assert.Equal(987654321L, first.LastKnownAssets);
        Assert.Equal(0.0031m, first.Share);
        Assert.Equal("Example FCU", read.OutstandingMaterial[1].Name);
        Assert.Null(read.OutstandingMaterial[1].Share);

        var published = await ReadQuorumAsync(Published());

        Assert.Empty(published.OutstandingMaterial);
    }

    [Fact]
    public async Task ReadsAbsentKeysAsNone()
    {
        var read = await ReadQuorumAsync(new JsonObject { ["expected"] = 10 });

        Assert.Null(read.Filed);
        Assert.Null(read.CoverageByCount);
        Assert.Null(read.WeightsFrom);
        Assert.Empty(read.Verdicts);
        Assert.Empty(read.OutstandingMaterial);
        Assert.False(read.HasReached("floor/1"));
    }

    [Fact]
    public async Task AQuarterWithoutAQuorumHasNone()
    {
        var quarter = await ReadAsync(quorum: null);

        Assert.Null(quarter.Quorum);
    }

    [Fact]
    public async Task RefusesAQuorumOfTheWrongType()
    {
        var quorum = Published();
        quorum["expected"] = "many";

        await Assert.ThrowsAsync<DataIntegrityException>(() => ReadAsync(quorum));
    }
}
