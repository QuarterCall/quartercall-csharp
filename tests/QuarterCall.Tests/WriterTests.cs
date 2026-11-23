using Parquet;
using Parquet.Schema;
using QuarterCall.Tests.Support;
using Xunit;

namespace QuarterCall.Tests;

/// <summary>
/// Parquet.Net reads both writers' bytes: DuckDB's, as the published files carry them, and its own, as the tests
/// write them.
/// </summary>
public sealed class WriterTests
{
    // A release of the peer-quarter file, as DuckDB wrote it.
    private const string PeerQuarterFixture = "peer-quarter-v2026.3.22.parquet";

    private static readonly ParquetOptions Options = new() { UseDateOnlyTypeForDates = true };

    // Carries ticks below the microsecond, which an INT96 timestamp keeps.
    private static readonly DateTime IngestedAt =
        new DateTime(2026, 10, 28, 14, 3, 7, DateTimeKind.Utc).AddTicks(1_234_567);

    [Fact]
    public async Task DuckDbBytesAreReadable()
    {
        await using var reader = await ParquetReader.CreateAsync(Fixtures.Path(PeerQuarterFixture), Options);

        Assert.Equal(1, reader.RowGroupCount);
        Assert.Equal(21, reader.Schema.DataFields.Length);
        var rows = await ReadPeerQuarterAsync(reader.Schema, reader.OpenRowGroupReader(0));
        Assert.Equal(17_640, rows.Count);
        Assert.Equal(("ALL", "asset_growth", "2019Q2"), (rows[0].PeerGroup, rows[0].Measure, rows[0].Quarter));
        Assert.Equal(
            ("TIER_LT_10M", "yield_on_avg_loans", "2026Q3"),
            (rows[^1].PeerGroup, rows[^1].Measure, rows[^1].Quarter));
        Assert.Equal(1_554, rows.Count(candidate => candidate.Median is null));

        var row = Assert.Single(
            rows, candidate => candidate is { PeerGroup: "ALL", Measure: "total_assets", Quarter: "2026Q3" });
        Assert.Equal(new DateOnly(2026, 9, 30), row.CycleDate);
        Assert.False(row.Preliminary);
        Assert.Null(row.RatioOfSums);
        Assert.Equal(2511232413199.0, row.Sum);
        Assert.Equal(68367446.50000000m, row.Median);
        Assert.Null(row.WeightedMedian);
        Assert.Equal(4565547.00000000m, row.P10);
        Assert.Equal(1147251312.50000000m, row.P90);
        Assert.Equal((4186, 4215, 4186), (row.N, row.ExpectedN, row.NValued));
        Assert.Equal(("reached", null, "provisional"), (row.QuorumStatus, row.NullReason, row.Status));
    }

    [Fact]
    public async Task ParquetNetBytesAreReadable()
    {
        var cycleDate = new DateOnly(2026, 6, 30);
        AccountRow[] written =
        [
            new("2026Q2", cycleDate, 5308, 912_004L, 1, "010", 123456789012345.12345678m, IngestedAt, 61),
            new("2026Q2", cycleDate, 5308, 912_004L, 1, "018", null, IngestedAt, null),
            new("2026Q2", cycleDate, 24212, 913_587L, 2, "010", -0.00000001m, IngestedAt, 4170),
        ];
        using var stream = new MemoryStream(EvidenceFiles.Accounts(written, rowsPerGroup: 2));
        await using var reader = await ParquetReader.CreateAsync(stream, Options);

        Assert.Equal(2, reader.RowGroupCount);
        var read = new List<AccountRow>();
        foreach (var group in reader.RowGroups)
        {
            read.AddRange(await ReadAccountsAsync(reader.Schema, group));
        }

        Assert.Equal(123456789012345.12345678m, read[0].AccountValue);
        Assert.Null(read[1].AccountValue);
        Assert.Null(read[1].Cuid);
        Assert.All(read, row => Assert.Equal(IngestedAt.Ticks, row.IngestedAt.Ticks));
        Assert.Equal(written, read);
    }

    private static async Task<List<PeerQuarterRow>> ReadPeerQuarterAsync(
        ParquetSchema schema, ParquetRowGroupReader group)
    {
        var columns = new ColumnReader(schema, group);
        var peerGroup = await columns.StringsAsync("peer_group");
        var measure = await columns.StringsAsync("measure");
        var quarter = await columns.StringsAsync("quarter");
        var cycleDate = await columns.ValuesAsync<DateOnly>("cycle_date");
        var preliminary = await columns.ValuesAsync<bool>("preliminary");
        var ratioOfSums = await columns.NullablesAsync<double>("ratio_of_sums");
        var sum = await columns.NullablesAsync<double>("sum");
        var mean = await columns.NullablesAsync<double>("mean");
        var stddev = await columns.NullablesAsync<double>("stddev");
        var median = await columns.NullablesAsync<decimal>("median");
        var weightedMedian = await columns.NullablesAsync<decimal>("weighted_median");
        var p10 = await columns.NullablesAsync<decimal>("p10");
        var p25 = await columns.NullablesAsync<decimal>("p25");
        var p75 = await columns.NullablesAsync<decimal>("p75");
        var p90 = await columns.NullablesAsync<decimal>("p90");
        var n = await columns.ValuesAsync<int>("n");
        var expectedN = await columns.ValuesAsync<int>("expected_n");
        var nValued = await columns.ValuesAsync<int>("n_valued");
        var quorumStatus = await columns.StringsAsync("quorum_status");
        var nullReason = await columns.StringsAsync("null_reason");
        var status = await columns.StringsAsync("status");

        // The four string columns without nulls are REQUIRED in the file.
        return
        [
            .. Enumerable.Range(0, columns.RowCount).Select(i => new PeerQuarterRow(
                peerGroup[i]!, measure[i]!, quarter[i]!, cycleDate[i], preliminary[i],
                ratioOfSums[i], sum[i], mean[i], stddev[i],
                median[i], weightedMedian[i], p10[i], p25[i], p75[i], p90[i],
                n[i], expectedN[i], nValued[i], quorumStatus[i]!, nullReason[i], status[i])),
        ];
    }

    private static async Task<AccountRow[]> ReadAccountsAsync(
        ParquetSchema schema, ParquetRowGroupReader group)
    {
        var columns = new ColumnReader(schema, group);
        var quarter = await columns.StringsAsync("quarter");
        var cycleDate = await columns.ValuesAsync<DateOnly>("cycle_date");
        var cuNumber = await columns.ValuesAsync<int>("cu_number");
        var submissionId = await columns.ValuesAsync<long>("submission_id");
        var version = await columns.ValuesAsync<short>("version");
        var accountCode = await columns.StringsAsync("account_code");
        var accountValue = await columns.NullablesAsync<decimal>("account_value");
        var ingestedAt = await columns.ValuesAsync<DateTime>("ingested_at");
        var cuid = await columns.NullablesAsync<int>("cuid");

        // Every row written has a quarter and an account code.
        return
        [
            .. Enumerable.Range(0, columns.RowCount).Select(i => new AccountRow(
                quarter[i]!, cycleDate[i], cuNumber[i], submissionId[i], version[i],
                accountCode[i]!, accountValue[i], ingestedAt[i], cuid[i])),
        ];
    }

    /// <summary>Reads whole columns of one row group, each into an array of its row count.</summary>
    private sealed class ColumnReader(ParquetSchema schema, ParquetRowGroupReader group)
    {
        public int RowCount { get; } = checked((int)group.RowCount);

        /// <summary>A column without nulls.</summary>
        public async Task<T[]> ValuesAsync<T>(string column) where T : struct
        {
            var values = new T[RowCount];
            await group.ReadAsync<T>(schema.FindDataField(column), values);
            return values;
        }

        /// <summary>A column that may hold nulls; Parquet.Net requires the nullable form for one.</summary>
        public async Task<T?[]> NullablesAsync<T>(string column) where T : struct
        {
            var values = new T?[RowCount];
            await group.ReadAsync<T>(schema.FindDataField(column), values);
            return values;
        }

        /// <summary>A string column; a null reads as null.</summary>
        public async Task<string?[]> StringsAsync(string column)
        {
            var values = new string?[RowCount];
            await group.ReadAsync(schema.FindDataField(column), values);
            return values;
        }
    }

    private sealed record PeerQuarterRow(
        string PeerGroup,
        string Measure,
        string Quarter,
        DateOnly CycleDate,
        bool Preliminary,
        double? RatioOfSums,
        double? Sum,
        double? Mean,
        double? Stddev,
        decimal? Median,
        decimal? WeightedMedian,
        decimal? P10,
        decimal? P25,
        decimal? P75,
        decimal? P90,
        int N,
        int ExpectedN,
        int NValued,
        string QuorumStatus,
        string? NullReason,
        string? Status);
}
