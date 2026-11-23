using System.Diagnostics;
using System.Globalization;
using QuarterCall.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace QuarterCall.Tests;

/// <summary>One temporary cache for the live tests, so each file is downloaded once.</summary>
public sealed class LiveCache : IDisposable
{
    /// <summary>The cache directory's path. The library creates the directory on first use.</summary>
    public string Directory { get; } =
        Path.Combine(Path.GetTempPath(), "quartercall-live-" + Guid.NewGuid().ToString("N"));

    /// <summary>Deletes the cache.</summary>
    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was cached.
        }
        catch (IOException)
        {
            // A file still open, as on Windows: a leftover in the temporary directory is tolerable.
        }
        catch (UnauthorizedAccessException)
        {
            // As above: a leftover in the temporary directory is tolerable.
        }
    }
}

/// <summary>Reads of the live host. Each prints its figures; none runs unless QUARTERCALL_LIVE is 1.</summary>
public class LiveTests(ITestOutputHelper output, LiveCache cache) : IClassFixture<LiveCache>
{
    private const long OneGiB = 1024L * 1024 * 1024;
    // A ceiling, in decimal megabytes (256 x 1,000,000 bytes), on what a filtered stream holds above the baseline
    // taken before it starts.
    private const long FilteredBudget = 256L * 1000 * 1000;
    private const int ForcedSampleEvery = 500;

    private QuarterCallClient NewClient() => new(new QuarterCallOptions { CacheDirectory = cache.Directory });

    private void Print(string line) => output.WriteLine(line);

    private static string F(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    [LiveFact]
    public async Task ListsTheSeries()
    {
        using var qc = NewClient();
        var quarters = await qc.Data.GetQuartersAsync();

        Print($"quarters: {quarters.Count}; newest {quarters[0].Quarter}; oldest {quarters[^1].Quarter}");
        Assert.Contains(quarters, q => q.Quarter == Quarter.Parse("2019Q1"));
        Assert.Contains(quarters, q => q.Quarter == Quarter.Parse("2026Q3"));
        for (int i = 1; i < quarters.Count; i++)
        {
            Assert.True(quarters[i - 1].Quarter > quarters[i].Quarter);
        }
    }

    [LiveFact]
    public async Task ReadsOneCode()
    {
        using var qc = NewClient();
        var filter = new AccountFilter { Codes = ["A010"] };
        int rows = 0;
        decimal sum = 0;
        await foreach (Account a in qc.Data.ReadAccountsAsync(Quarter.Parse("2019Q1"), filter))
        {
            rows++;
            sum += a.AccountValue ?? 0m;
        }

        Print($"2019Q1 A010 rows: {rows}");
        Print($"2019Q1 A010 sum: {sum.ToString(CultureInfo.InvariantCulture)}");
        Print($"2019Q1 A010 sum rounded to two places: {Math.Round(sum, 2).ToString("F2", CultureInfo.InvariantCulture)}");
        Assert.True(rows > 0);
    }

    [LiveFact]
    public async Task ReadsEveryTable()
    {
        using var qc = NewClient();
        var quarter = Quarter.Parse("2026Q3");
        var manifest = (await qc.Data.GetQuartersAsync()).Single(q => q.Quarter == quarter);

        int textRows = 0;
        await foreach (AccountText _ in qc.Data.ReadAccountTextAsync(quarter))
        {
            textRows++;
        }

        var profiles = new List<FilingProfile>();
        await foreach (FilingProfile p in qc.Data.ReadProfilesAsync(quarter))
        {
            profiles.Add(p);
        }

        long held = HeldAfterCollection();
        GC.KeepAlive(profiles);
        Print($"2026Q3 text rows: {textRows}");
        Print($"2026Q3 profile rows: {profiles.Count}; manifest ProfileRows: {manifest.ProfileRows}");
        Print($"2026Q3 columns on a profile: {profiles[0].Columns.Count}");
        Print($"2026Q3 bytes the whole process holds after a full collection, every profile in a list: {held}; that divided by the profile count: {held / (double)profiles.Count:F1}");

        string directory = Path.Combine(cache.Directory, "saved");
        var quorum = await qc.Data.DownloadAsync(quarter, EvidenceTable.Quorum, Path.Combine(directory, "quorum.parquet"));
        var index = await qc.Data.DownloadIndexAsync(Path.Combine(directory, "submissions.parquet"));
        long quorumBytes = new FileInfo(quorum.Path).Length;
        long indexBytes = new FileInfo(index.Path).Length;
        Print($"2026Q3 quorum file bytes: {quorumBytes}; manifest DetailBytes: {manifest.Quorum?.DetailBytes}");
        Print($"submissions index bytes: {indexBytes}; manifest bytes: {index.Bytes}");

        Assert.True(textRows > 0);
        Assert.Equal(manifest.ProfileRows, profiles.Count);
        Assert.Equal(manifest.Quorum!.DetailBytes, quorumBytes);
        Assert.Equal(index.Bytes, indexBytes);
    }

    [LiveFact]
    public async Task ReadsAFilterAfterAnotherRead()
    {
        using var qc = NewClient();
        var quarter = Quarter.Parse("2026Q3");
        var first = new AccountFilter { Codes = ["A010"] };
        var second = new AccountFilter { CuNumbers = [5536], Codes = ["A010", "A018"] };

        // The README's first example, then its second, three times over in one process.
        var rounds = new List<List<Account>>();
        for (int round = 0; round < 3; round++)
        {
            var five = new List<Account>();
            await foreach (Account a in qc.Data.ReadAccountsAsync(quarter, first).Take(5))
            {
                five.Add(a);
            }

            Assert.Equal(5, five.Count);
            Assert.All(five, a => Assert.Equal("A010", a.AccountCode));

            var rows = new List<Account>();
            await foreach (Account a in qc.Data.ReadAccountsAsync(quarter, second))
            {
                rows.Add(a);
            }

            rounds.Add(rows);
        }

        // The same rows, kept from a pass that reads every row.
        var expected = new List<Account>();
        await foreach (Account a in qc.Data.ReadAccountsAsync(quarter))
        {
            if (a.CuNumber == 5536 && a.AccountCode is "A010" or "A018")
            {
                expected.Add(a);
            }
        }

        Print($"2026Q3 rows of charter 5536 with A010 or A018: {expected.Count}");
        Assert.NotEmpty(expected);
        for (int round = 0; round < rounds.Count; round++)
        {
            Assert.True(expected.SequenceEqual(rounds[round]), $"round {round}: the filtered rows differ");
        }
    }

    [LiveFact]
    public async Task HoldsAQuarterInTheBudget()
    {
        using var qc = NewClient();
        var quarter = Quarter.Parse("2026Q2");

        // The download goes first and fills the cache, so the reads below time the reading alone.
        string scratch = Path.Combine(cache.Directory, "scratch", "accounts.parquet");
        var clock = Stopwatch.StartNew();
        var file = await qc.Data.DownloadAsync(quarter, EvidenceTable.Accounts, scratch);
        double downloadSeconds = clock.Elapsed.TotalSeconds;
        Print($"2026Q2 accounts file bytes: {new FileInfo(file.Path).Length}; seconds to download and verify, manifest included: {F(downloadSeconds)}");

        // One short untimed read pays for the reader's JIT, so the timed reads below compare with each other.
        await foreach (Account _ in qc.Data.ReadAccountsAsync(quarter).Take(1000))
        {
        }

        int counted = 0;
        clock.Restart();
        await foreach (Account _ in qc.Data.ReadAccountsAsync(quarter))
        {
            counted++;
        }

        double countSeconds = clock.Elapsed.TotalSeconds;
        Print($"2026Q2 plain enumeration rows: {counted}; seconds: {F(countSeconds)}; rows per second: {counted / countSeconds:F0}");

        var all = new List<Account>();
        long allocatedBefore = GC.GetTotalAllocatedBytes(true);
        int[] gcBefore = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        clock.Restart();
        await foreach (Account a in qc.Data.ReadAccountsAsync(quarter))
        {
            all.Add(a);
        }

        double readSeconds = clock.Elapsed.TotalSeconds;
        long allocated = GC.GetTotalAllocatedBytes(true) - allocatedBefore;
        int[] gcAfter = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
        long held = HeldAfterCollection();
        int rows = all.Count;
        GC.KeepAlive(all);
        Print($"2026Q2 unfiltered rows: {rows}; seconds: {F(readSeconds)}; rows per second: {rows / readSeconds:F0}");
        Print($"2026Q2 bytes held after a full collection: {held}; per row: {held / (double)rows:F1}");
        Print($"2026Q2 bytes allocated across the read per row: {allocated / (double)rows:F1}");
        Print($"2026Q2 collections across the read: gen0 {gcAfter[0] - gcBefore[0]}, gen1 {gcAfter[1] - gcBefore[1]}, gen2 {gcAfter[2] - gcBefore[2]}");

        Assert.True(held < OneGiB, $"{held} bytes held after a full collection");
    }

    [LiveFact]
    public async Task StreamsAFilteredQuarterInTheBudget()
    {
        using var qc = NewClient();
        var quarter = Quarter.Parse("2026Q2");

        // Untimed: makes sure the file is in the cache, so the read below is the stream alone.
        await qc.Data.DownloadAsync(quarter, EvidenceTable.Accounts, Path.Combine(cache.Directory, "scratch", "accounts.parquet"));

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long baseline = GC.GetTotalMemory(true);

        var filter = new AccountFilter { Codes = ["A010"] };
        int filtered = 0;
        long peak = 0;
        long forcedPeak = 0;
        var clock = Stopwatch.StartNew();
        await foreach (Account _ in qc.Data.ReadAccountsAsync(quarter, filter))
        {
            filtered++;
            peak = Math.Max(peak, GC.GetTotalMemory(false));
            if (filtered % ForcedSampleEvery == 0)
            {
                forcedPeak = Math.Max(forcedPeak, GC.GetTotalMemory(true));
            }
        }

        double seconds = clock.Elapsed.TotalSeconds;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        long after = GC.GetTotalMemory(true);

        Print($"2026Q2 A010 rows: {filtered}; seconds: {F(seconds)}");
        Print($"baseline GetTotalMemory(true) before the read: {baseline}");
        Print($"largest unforced sample: {peak}; above baseline: {peak - baseline}");
        Print($"largest forced sample (every {ForcedSampleEvery}th row): {forcedPeak}; above baseline: {forcedPeak - baseline}");
        Print($"held after the read and a full collection: {after}; above baseline: {after - baseline}");

        Assert.True(filtered >= ForcedSampleEvery, "too few rows to take a forced sample");
        Assert.True(forcedPeak - baseline < FilteredBudget, $"{forcedPeak - baseline} bytes above the baseline");
    }

    private static long HeldAfterCollection() => GC.GetTotalMemory(true);
}
