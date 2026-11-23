using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using QuarterCall.Internal;
using QuarterCall.Tests.Support;
using Xunit;

namespace QuarterCall.Tests;

public sealed class AccountsTests : IDisposable
{
    // Rows 0 to 2 fall in the first row group, 3 and 4 in the second.
    private const int RowsPerGroup = 3;

    private static readonly Quarter Q2026Q3 = Quarter.Parse("2026Q3");

    private static readonly DateOnly CycleDate = new(2026, 9, 30);

    // A July time: in a zone with summer time it is not UTC, so a reader that took it for local time would move
    // it. It carries ticks below the microsecond, which an INT96 timestamp keeps.
    private static readonly DateTime IngestedAt =
        new DateTime(2026, 7, 14, 9, 30, 0, DateTimeKind.Utc).AddTicks(1_234_567);

    private static readonly AccountRow[] Rows =
    [
        new("2026Q3", CycleDate, 5163, 912_004L, 1, "A010", 123456789012345.12345678m, IngestedAt, 61),
        new("2026Q3", CycleDate, 5163, 912_004L, 1, "A018", null, IngestedAt, 61),
        new("2026Q3", CycleDate, 5163, 912_004L, 1, "A025", 0m, IngestedAt, 61),
        new("2026Q3", CycleDate, 24212, 913_587L, 2, "A010", -0.00000001m, IngestedAt.AddHours(1), 4170),
        new("2026Q3", CycleDate, 24212, 913_587L, 2, "A018", 42m, IngestedAt.AddHours(1), null),
    ];

    // Each test works beneath a directory of its own, removed afterwards. Its clients keep the files they read
    // without a cache in _temporary, so a test sees every file a read leaves there.
    private readonly string _root;
    private readonly string _temporary;

    public AccountsTests()
    {
        _root = Directory.CreateTempSubdirectory("accounts-tests-").FullName;
        _temporary = Directory.CreateDirectory(Path.Combine(_root, "tmp")).FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ReadsEveryRowAsPublished()
    {
        var host = Publish(EvidenceFiles.Accounts(Rows, RowsPerGroup));
        using var client = Client(host);

        var read = await ReadAsync(client);

        Assert.Equal(Rows.Select(ToAccount), read);
        var value = read[0].AccountValue!.Value;
        Assert.Equal(123456789012345.12345678m, value);
        Assert.Equal("123456789012345.12345678", value.ToString(CultureInfo.InvariantCulture));
        Assert.All(read, account => Assert.Equal(TimeSpan.Zero, account.IngestedAt.Offset));
        // The time written, to the tick, whatever the local time zone.
        Assert.Equal(IngestedAt.Ticks, read[0].IngestedAt.Ticks);
        Assert.Equal(new[] { ManifestUri(host), FileUri(host) }, host.Requests.Select(r => r.Uri));
        Assert.Empty(Directory.GetFileSystemEntries(_temporary));
    }

    [Fact]
    public async Task ReadsAFileWithoutARowAsAnEmptyStream()
    {
        var host = Publish(EvidenceFiles.Accounts([]));
        using var client = Client(host);

        Assert.Empty(await ReadAsync(client));
        Assert.Empty(Directory.GetFileSystemEntries(_temporary));
    }

    [Fact]
    public async Task KeepsNullApartFromZero()
    {
        var host = Publish(EvidenceFiles.Accounts(Rows, RowsPerGroup));
        using var client = Client(host);

        var read = await ReadAsync(client);

        Assert.Null(read[1].AccountValue);
        Assert.Equal(0m, read[2].AccountValue);
        Assert.Equal(4170, read[3].Cuid);
        Assert.Null(read[4].Cuid);
    }

    [Fact]
    public async Task CombinesAFiltersSetsWithAnd()
    {
        var host = Publish(EvidenceFiles.Accounts(Rows, RowsPerGroup));
        using var client = Client(host);

        Assert.Equal(Expected(0), await ReadAsync(client, new() { Codes = ["A010"], CuNumbers = [5163] }));
        Assert.Equal(Expected(0, 3), await ReadAsync(client, new() { Codes = ["A010"] }));
        Assert.Equal(Expected(0, 1, 2), await ReadAsync(client, new() { CuNumbers = [5163] }));
        Assert.Equal(Expected(1, 4), await ReadAsync(client, new() { Codes = ["A018", "Z999"] }));
        // Credit union 24212's first row has cuid 4170; its second, whose cuid is null, is never selected by one.
        Assert.Equal(Expected(3), await ReadAsync(client, new() { Cuids = [4170] }));
        Assert.Equal(Expected(0, 1, 2), await ReadAsync(client, new() { Cuids = [61, 4170], CuNumbers = [5163] }));
        Assert.Equal(
            Expected(3),
            await ReadAsync(client, new() { Codes = ["A010", "A018"], CuNumbers = [5163, 24212], Cuids = [4170] }));
        Assert.Empty(await ReadAsync(client, new() { Codes = ["A025"], CuNumbers = [24212] }));
        // A code matches exactly: in case, and without a character more or less.
        Assert.Empty(await ReadAsync(client, new() { Codes = ["a010", "A01", "A0100", " A010"] }));
    }

    [Fact]
    public async Task CopiesTheFilterAtTheCall()
    {
        var host = Publish(EvidenceFiles.Accounts(Rows, RowsPerGroup));
        using var client = Client(host);
        List<string> codes = ["A010"];
        List<int> cuNumbers = [5163, 24212];

        var filter = new AccountFilter { Codes = codes, CuNumbers = cuNumbers };

        var accounts = client.Data.ReadAccountsAsync(Q2026Q3, filter);
        codes[0] = "A018";
        cuNumbers.Clear();

        Assert.Equal(Expected(0, 3), await accounts.ToListAsync());
    }

    [Fact]
    public async Task AnEmptySetSelectsNothing()
    {
        AccountFilter[] filters =
        [
            new() { Codes = [] },
            new() { CuNumbers = [] },
            new() { Cuids = [] },
            new() { Codes = ["A010"], CuNumbers = [5163], Cuids = [] },
        ];
        foreach (var filter in filters)
        {
            var host = Publish(EvidenceFiles.Accounts(Rows));
            using var client = Client(host);

            Assert.Empty(await ReadAsync(client, filter));
            Assert.Equal(new[] { ManifestUri(host) }, host.Requests.Select(r => r.Uri));

            await Assert.ThrowsAsync<DataUnavailableException>(
                () => ReadAsync(client, filter, Quarter.Parse("2031Q1")));
            Assert.Equal(2, host.Requests.Count);
            Assert.All(host.Requests, request => Assert.Equal(ManifestUri(host), request.Uri));
        }
    }

    [Fact]
    public void RefusesBadArgumentsAtTheCall()
    {
        var host = Publish(EvidenceFiles.Accounts(Rows));
        using var client = Client(host);

        var quarter = Assert.Throws<ArgumentException>(() => client.Data.ReadAccountsAsync(default));

        Assert.Equal("quarter", quarter.ParamName);
        AccountFilter[] filters =
        [
            new() { Codes = [""] },
            new() { Codes = ["A010", null!] },
            new() { CuNumbers = [0] },
            new() { CuNumbers = [5163, -1] },
            new() { Cuids = [0] },
            new() { Codes = ["A010"], Cuids = [int.MinValue] },
        ];
        foreach (var filter in filters)
        {
            var e = Assert.Throws<ArgumentException>(() => client.Data.ReadAccountsAsync(Q2026Q3, filter));
            Assert.Equal("filter", e.ParamName);
        }

        Assert.Empty(host.Requests);
    }

    [Theory]
    [InlineData("quarter")]
    [InlineData("cycle_date")]
    [InlineData("cu_number")]
    [InlineData("submission_id")]
    [InlineData("version")]
    [InlineData("account_code")]
    [InlineData("account_value")]
    [InlineData("ingested_at")]
    [InlineData("cuid")]
    public async Task RefusesAFileWithoutAColumn(string column)
    {
        var host = Publish(EvidenceFiles.Accounts(Rows, new AccountsShape { Without = column }, RowsPerGroup));
        using var client = Client(host);

        var e = await Assert.ThrowsAsync<UnsupportedSchemaException>(() => ReadAsync(client));

        Assert.Contains("upgrade", e.Message);
        Assert.Contains($"\"{column}\"", e.Message);
        // Refused whatever the filter, one that selects no row included.
        await Assert.ThrowsAsync<UnsupportedSchemaException>(() => ReadAsync(client, new() { CuNumbers = [1] }));
        Assert.Empty(Directory.GetFileSystemEntries(_temporary));

        var extra = Publish(EvidenceFiles.Accounts(Rows, new AccountsShape { WithExtraColumn = true }, RowsPerGroup));
        using var extraClient = Client(extra);

        Assert.Equal(Rows.Select(ToAccount), await ReadAsync(extraClient));
    }

    [Theory]
    [InlineData("64 arbitrary bytes")]
    [InlineData("64 bytes framed as Parquet")]
    public async Task RefusesAFileThatIsNotParquet(string body)
    {
        var bytes = new byte[64];
        new Random(1).NextBytes(bytes);
        if (body == "64 bytes framed as Parquet")
        {
            // The magic number at both ends, and a footer length that lands on the arbitrary bytes between.
            "PAR1"u8.CopyTo(bytes);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56), 40);
            "PAR1"u8.CopyTo(bytes.AsSpan(60));
        }

        var host = Publish(bytes);
        using var client = Client(host);

        var e = await Assert.ThrowsAsync<UnsupportedSchemaException>(() => ReadAsync(client));

        Assert.Contains("upgrade", e.Message);
        // Parquet.Net's exception is inside the package's own, and only there.
        Assert.NotNull(e.InnerException);
        Assert.Equal(typeof(QuarterCallClient).Namespace, e.GetType().Namespace);
        Assert.Empty(Directory.GetFileSystemEntries(_temporary));
    }

    [Fact]
    public async Task ReadsAColumnDeclaredNullable()
    {
        var shape = new AccountsShape { NullableCuNumber = true };
        var nullable = Publish(EvidenceFiles.Accounts(Rows, shape, RowsPerGroup));
        using var client = Client(nullable);

        Assert.Equal(Rows.Select(ToAccount), await ReadAsync(client));
        Assert.Equal(Expected(0, 1, 2), await ReadAsync(client, new() { CuNumbers = [5163] }));

        var withNull = Publish(EvidenceFiles.Accounts(Rows, shape with { NullCuNumberAt = 4 }, RowsPerGroup));
        using var refusing = Client(withNull);

        var e = await Assert.ThrowsAsync<UnsupportedSchemaException>(() => ReadAsync(refusing));

        Assert.Contains("\"cu_number\"", e.Message);
        Assert.Contains("upgrade", e.Message);
        // So it is when cu_number is the column a filter reads.
        await Assert.ThrowsAsync<UnsupportedSchemaException>(() => ReadAsync(refusing, new() { CuNumbers = [5163] }));
    }

    [Fact]
    public async Task ReadsTheQuarterTheFileNames()
    {
        // The rows are reported as the file has them, even under another quarter's entry.
        var host = Publish(EvidenceFiles.Accounts([Rows[0] with { Quarter = "2026Q2" }]));
        using var client = Client(host);

        Assert.Equal(Quarter.Parse("2026Q2"), Assert.Single(await ReadAsync(client)).Quarter);

        foreach (var label in new[] { "2026Q5", "2026q3", "", null })
        {
            var refused = Publish(EvidenceFiles.Accounts([Rows[0], Rows[1] with { Quarter = label! }]));
            using var refusing = Client(refused);

            var e = await Assert.ThrowsAsync<UnsupportedSchemaException>(() => ReadAsync(refusing));

            Assert.Contains("\"quarter\"", e.Message);
            Assert.Contains("upgrade", e.Message);
        }
    }

    [Fact]
    public async Task SharesOneStringPerCode()
    {
        // Two rows of one code in the first row group, and a third in the second.
        AccountRow[] rows = [Rows[0], Rows[0] with { CuNumber = 1 }, Rows[0] with { CuNumber = 2 }];
        var host = Publish(EvidenceFiles.Accounts(rows, rowsPerGroup: 2));
        using var client = Client(host);

        var read = await ReadAsync(client);
        var again = await ReadAsync(client);

        Assert.Same(read[0].AccountCode, read[1].AccountCode);
        Assert.Same(read[0].AccountCode, read[2].AccountCode);
        // Each stream pools its own strings, so no pool outlives its stream.
        Assert.Equal(read, again);
        Assert.NotSame(read[0].AccountCode, again[0].AccountCode);
    }

    [Theory]
    [InlineData("a break after the first row", null)]
    [InlineData("a break after the first row", "cache")]
    [InlineData("a token cancelled after the first row", null)]
    [InlineData("a token cancelled after the first row", "cache")]
    [InlineData("a token given to WithCancellation, cancelled after the first row", null)]
    [InlineData("a token given to WithCancellation, cancelled after the first row", "cache")]
    [InlineData("an enumerator never advanced", null)]
    [InlineData("an enumerator never advanced", "cache")]
    [InlineData("the end of the rows", null)]
    [InlineData("the end of the rows", "cache")]
    public async Task LeavesNothingBehindWhenTheCallerStops(string stop, string? cacheName)
    {
        var body = EvidenceFiles.Accounts(Rows, RowsPerGroup);
        var host = Publish(body);
        var cache = cacheName is null ? null : Path.Combine(_root, cacheName);
        var cached = cache is null ? null : Path.Combine(cache, Digest(body) + ".parquet");
        using var client = Client(host, cache);
        using var cancel = new CancellationTokenSource();
        var read = new List<Account>();

        // While the stream is part way through, its file is open: the checks after it ends can see a handle.
        void AssertReading()
        {
            if (OperatingSystem.IsLinux())
            {
                Assert.NotEmpty(OpenFilesBeneath(_root));
            }

            if (cached is not null)
            {
                Assert.ThrowsAny<IOException>(() => OpenAlone(cached));
            }
        }

        switch (stop)
        {
            case "a break after the first row":
                await foreach (var account in client.Data.ReadAccountsAsync(Q2026Q3))
                {
                    read.Add(account);
                    AssertReading();
                    break;
                }

                break;
            case "a token cancelled after the first row":
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    await foreach (var account in client.Data.ReadAccountsAsync(Q2026Q3, null, cancel.Token))
                    {
                        read.Add(account);
                        AssertReading();
                        await cancel.CancelAsync();
                    }
                });
                break;
            case "a token given to WithCancellation, cancelled after the first row":
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    await foreach (var account in client.Data.ReadAccountsAsync(Q2026Q3).WithCancellation(cancel.Token))
                    {
                        read.Add(account);
                        AssertReading();
                        await cancel.CancelAsync();
                    }
                });
                break;
            case "an enumerator never advanced":
                await client.Data.ReadAccountsAsync(Q2026Q3).GetAsyncEnumerator().DisposeAsync();
                break;
            default:
                await foreach (var account in client.Data.ReadAccountsAsync(Q2026Q3))
                {
                    read.Add(account);
                    if (read.Count == 1)
                    {
                        AssertReading();
                    }
                }

                break;
        }

        var rows = stop switch
        {
            "an enumerator never advanced" => 0,
            "the end of the rows" => Rows.Length,
            _ => 1,
        };
        Assert.Equal(Rows.Select(ToAccount).Take(rows), read);
        // Nothing the read made is left in the temporary directory, and no file beneath the test's is held open.
        Assert.Empty(Directory.GetFileSystemEntries(_temporary));
        Assert.Empty(OpenFilesBeneath(_root));
        if (rows == 0)
        {
            Assert.Empty(host.Requests);
            return;
        }

        if (cached is not null)
        {
            Assert.Equal(new[] { cached }, Directory.GetFileSystemEntries(cache!));
            // No handle is open on it: an open that shares nothing succeeds, which an open reader refuses, on Unix too.
            OpenAlone(cached).Dispose();
            File.Delete(cached);
            Assert.False(File.Exists(cached));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cache")]
    public async Task ReadsTwiceAtOnce(string? cacheName)
    {
        var host = Publish(EvidenceFiles.Accounts(Rows, RowsPerGroup));
        using var client = Client(host, cacheName is null ? null : Path.Combine(_root, cacheName));
        var accounts = client.Data.ReadAccountsAsync(Q2026Q3);
        var one = new List<Account>();
        var two = new List<Account>();

        // Two enumerations of the one stream, a row of each in turn, so each holds its file and its buffers at once.
        await using (var first = accounts.GetAsyncEnumerator())
        await using (var second = accounts.GetAsyncEnumerator())
        {
            while (await first.MoveNextAsync())
            {
                one.Add(first.Current);
                Assert.True(await second.MoveNextAsync());
                two.Add(second.Current);
            }

            Assert.False(await second.MoveNextAsync());
        }

        Assert.Equal(Rows.Select(ToAccount), one);
        Assert.Equal(one, two);
        // Each enumeration is a read of its own, from its own reading of the manifest.
        Assert.Equal(2, host.Requests.Count(r => r.Uri == ManifestUri(host)));
        Assert.Equal(cacheName is null ? 2 : 1, host.Requests.Count(r => r.Uri == FileUri(host)));

        var both = await Task.WhenAll(ReadAsync(client), ReadAsync(client));

        Assert.All(both, read => Assert.Equal(one, read));
        Assert.Empty(Directory.GetFileSystemEntries(_temporary));
    }

    [Fact]
    public async Task ComposesWithLinq()
    {
        var host = Publish(EvidenceFiles.Accounts(Rows, RowsPerGroup));
        using var client = Client(host);

        var count = await client.Data.ReadAccountsAsync(Q2026Q3).Where(a => a.AccountValue > 0).CountAsync();

        Assert.Equal(2, count);
        // The operators are the framework's own: no package supplies them.
        Assert.StartsWith(RuntimeEnvironment.GetRuntimeDirectory(), typeof(AsyncEnumerable).Assembly.Location);
    }

    [Fact]
    public async Task SkipsARowGroupWithNoMatch()
    {
        var path = Path.Combine(_root, "accounts.parquet");
        await File.WriteAllBytesAsync(path, EvidenceFiles.Accounts(Rows, RowsPerGroup));
        await using var file = new VerifiedFile(path, FileKeeper.Cache);

        // Unfiltered, each of the two row groups reads its nine columns.
        var all = await ReadTableAsync(file, filter: null);

        Assert.Equal(Rows.Select(ToAccount), all.Rows);
        Assert.Equal(9 + 9, all.ColumnReads);

        // A025 is in the first row group only, and cuid 4170 in the second only. The group without a match reads
        // the filter's columns, and no other.
        var first = await ReadTableAsync(file, new() { Codes = ["A025"] });
        var second = await ReadTableAsync(file, new() { Cuids = [4170] });
        var both = await ReadTableAsync(file, new() { Codes = ["A010"], Cuids = [4170] });

        Assert.Equal(Expected(2), first.Rows);
        Assert.Equal(9 + 1, first.ColumnReads);
        Assert.Equal(Expected(3), second.Rows);
        Assert.Equal(1 + 9, second.ColumnReads);
        Assert.Equal(Expected(3), both.Rows);
        Assert.Equal(2 + 9, both.ColumnReads);
    }

    [Fact]
    public async Task ReadsAColumnOnlyAsTheTypeItHolds()
    {
        var path = Path.Combine(_root, "accounts.parquet");
        var shape = new AccountsShape { WithExtraColumn = true };
        await File.WriteAllBytesAsync(path, EvidenceFiles.Accounts(Rows, shape, RowsPerGroup));
        await using var file = new VerifiedFile(path, FileKeeper.Cache);
        await using var table = await ParquetTable.OpenAsync(file, CancellationToken.None);

        Assert.Equal(
            new[]
            {
                "quarter", "cycle_date", "cu_number", "submission_id", "version", "account_code", "account_value",
                "ingested_at", "cuid", "note",
            },
            table.ColumnNames);
        Assert.Equal(2, table.GroupCount);
        Assert.Equal(typeof(ReadOnlyMemory<char>), table.ColumnType("note"));
        Assert.Equal(typeof(decimal), table.ColumnType("account_value"));
        Assert.Contains("upgrade", Assert.Throws<UnsupportedSchemaException>(() => table.ColumnType("absent")).Message);

        Assert.Equal(3, await table.OpenGroupAsync(0, CancellationToken.None));
        var e = await Assert.ThrowsAsync<UnsupportedSchemaException>(
            () => table.ReadAsync<long>("cu_number", CancellationToken.None));

        Assert.Contains("upgrade", e.Message);
        var cuNumbers = await table.ReadAsync<int>("cu_number", CancellationToken.None);
        Assert.Equal(new int?[] { 5163, 5163, 5163 }, cuNumbers.ToArray());
        Assert.Equal(2, await table.OpenGroupAsync(1, CancellationToken.None));
        var notes = await table.ReadTextAsync("note", CancellationToken.None);
        Assert.Equal(new[] { "extra", "extra" }, notes.ToArray().Select(note => note.ToString()));
    }

    private static async Task<(List<Account> Rows, int ColumnReads)> ReadTableAsync(
        VerifiedFile file, AccountFilter? filter)
    {
        await using var table = await ParquetTable.OpenAsync(file, CancellationToken.None);
        var selection = AccountRows.Select(filter, nameof(filter));
        var rows = await selection.ReadAsync(table, CancellationToken.None).ToListAsync();
        return (rows, table.ColumnReads);
    }

    private static Account ToAccount(AccountRow row)
    {
        return new Account
        {
            Quarter = Quarter.Parse(row.Quarter),
            CycleDate = row.CycleDate,
            CuNumber = row.CuNumber,
            SubmissionId = row.SubmissionId,
            Version = row.Version,
            AccountCode = row.AccountCode,
            AccountValue = row.AccountValue,
            // Written in UTC, read in UTC.
            IngestedAt = new DateTimeOffset(row.IngestedAt.Ticks, TimeSpan.Zero),
            Cuid = row.Cuid,
        };
    }

    private static Account[] Expected(params int[] rows)
    {
        return [.. rows.Select(row => ToAccount(Rows[row]))];
    }

    private static FixtureHost Publish(byte[] body)
    {
        var host = new FixtureHost();
        host.AddFile(host.AddQuarter("2026Q3"), EvidenceTable.Accounts, body);
        return host;
    }

    private static Task<List<Account>> ReadAsync(
        QuarterCallClient client, AccountFilter? filter = null, Quarter? quarter = null)
    {
        return client.Data.ReadAccountsAsync(quarter ?? Q2026Q3, filter).ToListAsync().AsTask();
    }

    private static Uri ManifestUri(FixtureHost host)
    {
        return new Uri(host.BaseAddress, FixtureHost.ManifestPath);
    }

    private static Uri FileUri(FixtureHost host)
    {
        return new Uri(host.BaseAddress, "curated/accounts/quarter=2026Q3/data.parquet");
    }

    private static string Digest(byte[] body)
    {
        return Convert.ToHexStringLower(SHA256.HashData(body));
    }

    // Opens a file sharing nothing. Windows refuses it while another handle is open; so does .NET on Unix, which
    // locks each file it opens.
    private static FileStream OpenAlone(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    // The files beneath directory that this process holds open, a deleted one included. Linux lists them in
    // /proc/self/fd; elsewhere there is no such list, and this is empty.
    private static List<string> OpenFilesBeneath(string directory)
    {
        var open = new List<string>();
        if (!OperatingSystem.IsLinux())
        {
            return open;
        }

        var prefix = Path.TrimEndingDirectorySeparator(directory) + "/";
        foreach (var descriptor in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            string? target;
            try
            {
                target = new FileInfo(descriptor).LinkTarget;
            }
            catch (IOException)
            {
                // Closed since it was listed.
                continue;
            }

            if (target is not null && target.StartsWith(prefix, StringComparison.Ordinal))
            {
                open.Add(target);
            }
        }

        return open;
    }

    private QuarterCallClient Client(FixtureHost host, string? cacheDirectory = null)
    {
        return host.CreateClient(cacheDirectory, _temporary);
    }
}
