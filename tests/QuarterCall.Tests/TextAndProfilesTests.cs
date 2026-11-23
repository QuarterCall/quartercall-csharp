using QuarterCall.Internal;
using QuarterCall.Tests.Support;
using Xunit;

namespace QuarterCall.Tests;

public sealed class TextAndProfilesTests : IDisposable
{
    // Rows 0 and 1 fall in the first row group, 2 in the second.
    private const int RowsPerGroup = 2;

    private static readonly Quarter Q2026Q3 = Quarter.Parse("2026Q3");

    private static readonly DateOnly CycleDate = new(2026, 9, 30);

    // A July time with ticks below the microsecond: a reader that took it for local time would move it in a zone with
    // summer time. Whole microseconds, which is what a TIMESTAMP in microseconds holds.
    private static readonly DateTime IngestedAt = new DateTime(2026, 7, 14, 9, 30, 0, DateTimeKind.Utc).AddTicks(1_230);

    private static readonly AccountTextRow[] TextRows =
    [
        new("2026Q3", CycleDate, 5163, 912_004L, "A700", "Well Capitalized", IngestedAt),
        new("2026Q3", CycleDate, 5163, 912_004L, "A999", "  n/a ", IngestedAt),
        new("2026Q3", CycleDate, 24212, 913_587L, "A700", "Adequately Capitalized", IngestedAt.AddHours(1)),
        new("2026Q3", CycleDate, 24212, 913_587L, "A701", "Yes", IngestedAt.AddHours(1)),
    ];

    private static readonly (string Name, Type Type)[] Ncua =
    [
        ("credit_union_name", typeof(string)),
        ("state", typeof(string)),
        ("acct_892a", typeof(int)),
    ];

    private static readonly ProfileRow[] ProfileRows =
    [
        Profile(5163, 61, 912_004L, "NORTHERN", "TX", 17),
        Profile(24212, 4170, 913_587L, "SOUTHERN", "TX", null),
        Profile(30000, null, 914_001L, "WESTERN", "CA", 5),
    ];

    private readonly string _root;
    private readonly string _temporary;

    public TextAndProfilesTests()
    {
        _root = Directory.CreateTempSubdirectory("text-tests-").FullName;
        _temporary = Directory.CreateDirectory(Path.Combine(_root, "tmp")).FullName;
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ReadsTextAsFiled()
    {
        var host = PublishText(EvidenceFiles.AccountText(TextRows, RowsPerGroup));
        using var client = Client(host);

        var read = await client.Data.ReadAccountTextAsync(Q2026Q3).ToListAsync();

        Assert.Equal(TextRows.Select(ToText), read);
        Assert.Equal("Well Capitalized", read[0].Text);
        // Neither trimmed nor parsed.
        Assert.Equal("  n/a ", read[1].Text);
        Assert.All(read, text => Assert.Equal(TimeSpan.Zero, text.IngestedAt.Offset));
        Assert.Equal(IngestedAt.Ticks, read[0].IngestedAt.Ticks);
        Assert.Equal(
            new[] { ManifestUri(host), new Uri(host.BaseAddress, TextPath) },
            host.Requests.Select(r => r.Uri));
        Assert.Empty(Directory.GetFileSystemEntries(_temporary));
        Assert.Empty(OpenFiles.Beneath(_root));
    }

    [Fact]
    public async Task AQuarterWithoutTextYieldsNothing()
    {
        var host = new FixtureHost();
        var entry = host.AddQuarter("2026Q3");
        host.AddFile(entry, EvidenceTable.Accounts, EvidenceFiles.Accounts([]));
        using var client = Client(host);
        var requests = host.Requests.Count;

        Assert.Empty(await client.Data.ReadAccountTextAsync(Q2026Q3).ToListAsync());

        // The manifest, and no file.
        Assert.Equal(requests + 1, host.Requests.Count);
        Assert.Equal(new Uri(host.BaseAddress, FixtureHost.ManifestPath), host.Requests[^1].Uri);
        await Assert.ThrowsAsync<DataUnavailableException>(
            () => client.Data.DownloadAsync(
                Q2026Q3, EvidenceTable.AccountText, Path.Combine(_root, "text.parquet")));
    }

    [Fact]
    public async Task FiltersTextAndCopiesTheFilterAtTheCall()
    {
        var host = PublishText(EvidenceFiles.AccountText(TextRows, RowsPerGroup));
        using var client = Client(host);

        Assert.Equal(Texts(0, 2), await ReadTextAsync(client, new() { Codes = ["A700"] }));
        Assert.Equal(Texts(0, 1), await ReadTextAsync(client, new() { CuNumbers = [5163] }));
        Assert.Equal(Texts(2), await ReadTextAsync(client, new() { Codes = ["A700"], CuNumbers = [24212] }));
        Assert.Empty(await ReadTextAsync(client, new() { Codes = ["a700", "A70", " A700"] }));

        List<string> codes = ["A701"];
        var stream = client.Data.ReadAccountTextAsync(Q2026Q3, new() { Codes = codes });
        codes[0] = "A700";
        Assert.Equal(Texts(3), await stream.ToListAsync());
    }

    [Fact]
    public async Task AnEmptySetSelectsNothingOnEachReader()
    {
        AccountTextFilter[] textFilters = [new() { Codes = [] }, new() { CuNumbers = [], Codes = ["A700"] }];
        foreach (var filter in textFilters)
        {
            var host = PublishText(EvidenceFiles.AccountText(TextRows));
            using var client = Client(host);

            Assert.Empty(await ReadTextAsync(client, filter));
            Assert.Equal(new[] { ManifestUri(host) }, host.Requests.Select(r => r.Uri));
            await Assert.ThrowsAsync<DataUnavailableException>(
                () => client.Data.ReadAccountTextAsync(Quarter.Parse("2031Q1"), filter).ToListAsync().AsTask());
            Assert.Equal(2, host.Requests.Count);
        }

        ProfileFilter[] profileFilters = [new() { CuNumbers = [] }, new() { Cuids = [], CuNumbers = [5163] }];
        foreach (var filter in profileFilters)
        {
            var host = PublishProfiles(EvidenceFiles.Profiles(ProfileRows, Ncua));
            using var client = Client(host);

            Assert.Empty(await ReadProfilesAsync(client, filter));
            Assert.Equal(new[] { ManifestUri(host) }, host.Requests.Select(r => r.Uri));
            await Assert.ThrowsAsync<DataUnavailableException>(
                () => client.Data.ReadProfilesAsync(Quarter.Parse("2031Q1"), filter).ToListAsync().AsTask());
            Assert.Equal(2, host.Requests.Count);
        }
    }

    [Fact]
    public void RefusesBadArgumentsAtTheCall()
    {
        var host = PublishText(EvidenceFiles.AccountText(TextRows));
        using var client = Client(host);

        var text = Assert.Throws<ArgumentException>(() => client.Data.ReadAccountTextAsync(default));
        var profiles = Assert.Throws<ArgumentException>(() => client.Data.ReadProfilesAsync(default));
        Assert.Equal("quarter", text.ParamName);
        Assert.Equal("quarter", profiles.ParamName);
        AccountTextFilter[] textFilters =
        [
            new() { Codes = [""] },
            new() { Codes = ["A700", null!] },
            new() { CuNumbers = [0] },
            new() { CuNumbers = [5163, -1] },
        ];
        foreach (var filter in textFilters)
        {
            var e = Assert.Throws<ArgumentException>(() => client.Data.ReadAccountTextAsync(Q2026Q3, filter));
            Assert.Equal("filter", e.ParamName);
        }

        ProfileFilter[] profileFilters =
        [
            new() { CuNumbers = [0] },
            new() { Cuids = [0] },
            new() { CuNumbers = [5163], Cuids = [int.MinValue] },
        ];
        foreach (var filter in profileFilters)
        {
            var e = Assert.Throws<ArgumentException>(() => client.Data.ReadProfilesAsync(Q2026Q3, filter));
            Assert.Equal("filter", e.ParamName);
        }

        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task ReadsAProfilesEightColumns()
    {
        var host = PublishProfiles(EvidenceFiles.Profiles(ProfileRows, Ncua, RowsPerGroup));
        using var client = Client(host);

        var read = await ReadProfilesAsync(client);

        Assert.Equal(3, read.Count);
        for (var i = 0; i < read.Count; i++)
        {
            var expected = ProfileRows[i];
            Assert.Equal(Q2026Q3, read[i].Quarter);
            Assert.Equal(CycleDate, read[i].CycleDate);
            Assert.Equal(expected.CuNumber, read[i].CuNumber);
            Assert.Equal(expected.Cuid, read[i].Cuid);
            Assert.Equal(expected.SubmissionId, read[i].SubmissionId);
            Assert.Equal(expected.Version, read[i].Version);
            Assert.Equal(expected.ObservedAtSource, read[i].ObservedAtSource);
        }

        Assert.Null(read[2].Cuid);
        Assert.Equal(61, read[0].Cuid);
        Assert.Empty(Directory.GetFileSystemEntries(_temporary));
        Assert.Empty(OpenFiles.Beneath(_root));
    }

    [Fact]
    public async Task ReadsTheProfilesAsWrittenOnEveryRead()
    {
        // Three row groups of 22,060 rows, quarter declared required, which the published file does not do but a
        // file may, read again and again in one process, with and without a filter.
        const int count = 66_180;
        var rows = Enumerable.Range(1, count).Select(ManyProfiles).ToArray();
        var host = PublishProfiles(EvidenceFiles.Profiles(rows, Ncua, rowsPerGroup: count / 3));
        using var client = Client(host);
        int[] cuNumbers = [1, count / 2, count];
        int[] cuids = [8, (count / 2) + 7];
        (string Name, ProfileFilter? Filter, Func<ProfileRow, bool> Keeps)[] reads =
        [
            ("by CuNumbers", new() { CuNumbers = cuNumbers }, row => cuNumbers.Contains(row.CuNumber!.Value)),
            ("by Cuids", new() { Cuids = cuids }, row => row.Cuid is { } cuid && cuids.Contains(cuid)),
            ("unfiltered", null, _ => true),
        ];

        for (var round = 0; round < 6; round++)
        {
            foreach (var (name, filter, keeps) in reads)
            {
                var expected = rows.Where(keeps).Select(Fields).ToList();
                // A full collection before each read, as a long session has between its reads: memory it frees
                // can hold a buffer made after it, and such a buffer is not cleared.
                GC.Collect();
                var read = (await ReadProfilesAsync(client, filter)).Select(Fields).ToList();

                Assert.True(expected.SequenceEqual(read), $"round {round}, {name}: the rows differ from those written");
            }
        }
    }

    [Fact]
    public async Task ObservedAtIsUtcAndKeepsTheWallClock()
    {
        var host = PublishProfiles(EvidenceFiles.Profiles(ProfileRows, Ncua));
        using var client = Client(host);

        var read = await ReadProfilesAsync(client);

        Assert.All(read, profile => Assert.Equal(TimeSpan.Zero, profile.ObservedAt.Offset));
        Assert.Equal(ProfileRows[0].ObservedAt.Ticks, read[0].ObservedAt.Ticks);
        Assert.Equal(ProfileRows[2].ObservedAt.Ticks, read[2].ObservedAt.Ticks);
    }

    [Fact]
    public async Task ReadsNcuasColumnsByName()
    {
        var host = PublishProfiles(EvidenceFiles.Profiles(ProfileRows, Ncua, RowsPerGroup));
        using var client = Client(host);

        var read = await ReadProfilesAsync(client);

        Assert.Equal(new[] { "credit_union_name", "state", "acct_892a" }, read[0].Columns);
        Assert.Equal("TX", read[0].GetString("state"));
        Assert.Equal("NORTHERN", read[0].GetString("credit_union_name"));
        Assert.Equal("CA", read[2].GetString("state"));
        Assert.Equal(17, read[0].GetInt32("acct_892a"));
        Assert.Null(read[1].GetInt32("acct_892a"));
        Assert.Equal(5, read[2].GetInt32("acct_892a"));
        // The columns' index is the file's, shared by its rows.
        Assert.Same(read[0].Columns, read[2].Columns);
    }

    [Fact]
    public async Task ANullTextValueReadsNull()
    {
        ProfileRow[] rows = [ProfileRows[0] with { Values = new Dictionary<string, object?> { ["acct_892a"] = 3 } }];
        var host = PublishProfiles(EvidenceFiles.Profiles(rows, Ncua));
        using var client = Client(host);

        var profile = Assert.Single(await ReadProfilesAsync(client));

        Assert.Null(profile.GetString("state"));
        Assert.Null(profile.GetString("credit_union_name"));
        Assert.Equal(3, profile.GetInt32("acct_892a"));
    }

    [Fact]
    public async Task RefusesAColumnByTheWrongNameOrType()
    {
        var host = PublishProfiles(EvidenceFiles.Profiles(ProfileRows, Ncua));
        using var client = Client(host);
        var profile = (await ReadProfilesAsync(client))[0];

        Assert.Throws<ArgumentException>(() => profile.GetString("nope"));
        Assert.Throws<ArgumentException>(() => profile.GetInt32("nope"));
        // Matched exactly, case included.
        Assert.Throws<ArgumentException>(() => profile.GetString("State"));
        Assert.Throws<ArgumentNullException>(() => profile.GetString(null!));
        Assert.Throws<ArgumentNullException>(() => profile.GetInt32(null!));
        // One of the eight is a property: the message says so.
        var leading = Assert.Throws<ArgumentException>(() => profile.GetInt32("cu_number"));
        Assert.Contains("property", leading.Message);
        var unknown = Assert.Throws<ArgumentException>(() => profile.GetString("nope"));
        Assert.Contains(nameof(FilingProfile.Columns), unknown.Message);
        Assert.Throws<ArgumentException>(() => profile.GetString("quarter"));

        var asInt = Assert.Throws<InvalidOperationException>(() => profile.GetInt32("state"));
        Assert.Contains("GetString", asInt.Message);
        var asText = Assert.Throws<InvalidOperationException>(() => profile.GetString("acct_892a"));
        Assert.Contains("GetInt32", asText.Message);
    }

    [Fact]
    public async Task ReadsAColumnAddedLater()
    {
        (string Name, Type Type)[] more = [.. Ncua, ("zip_code", typeof(string)), ("acct_new", typeof(int))];
        var rows = ProfileRows.Select(row => row with
        {
            Values = new Dictionary<string, object?> { ["zip_code"] = "78701", ["acct_new"] = 9 },
        }).ToArray();
        var host = PublishProfiles(EvidenceFiles.Profiles(rows, more));
        using var client = Client(host);

        var profile = (await ReadProfilesAsync(client))[0];

        Assert.Equal(new[] { "credit_union_name", "state", "acct_892a", "zip_code", "acct_new" }, profile.Columns);
        Assert.Equal("78701", profile.GetString("zip_code"));
        Assert.Equal(9, profile.GetInt32("acct_new"));
    }

    [Fact]
    public async Task FiltersProfiles()
    {
        var host = PublishProfiles(EvidenceFiles.Profiles(ProfileRows, Ncua, RowsPerGroup));
        using var client = Client(host);

        Assert.Equal(new[] { 5163 }, CuNumbers(await ReadProfilesAsync(client, new() { CuNumbers = [5163, 99] })));
        Assert.Equal(new[] { 24212 }, CuNumbers(await ReadProfilesAsync(client, new() { Cuids = [4170] })));
        var both = await ReadProfilesAsync(client, new() { CuNumbers = [5163, 30000], Cuids = [61] });
        Assert.Equal(new[] { 5163 }, CuNumbers(both));
        Assert.Empty(await ReadProfilesAsync(client, new() { CuNumbers = [5163], Cuids = [4170] }));
        // A null cuid is in no set.
        Assert.Empty(await ReadProfilesAsync(client, new() { CuNumbers = [30000], Cuids = [61, 4170] }));
        var every = await ReadProfilesAsync(client, new() { CuNumbers = [5163, 24212, 30000] });
        Assert.Equal(new[] { 5163, 24212, 30000 }, CuNumbers(every));
    }

    [Fact]
    public async Task SkipsARowGroupWithNoMatch()
    {
        var path = Path.Combine(_root, "profiles.parquet");
        await File.WriteAllBytesAsync(path, EvidenceFiles.Profiles(ProfileRows, Ncua, RowsPerGroup));
        await using var file = new VerifiedFile(path, FileKeeper.Cache);

        // Unfiltered, each of the two row groups reads its eleven columns.
        var all = await ReadTableAsync(file, filter: null);
        Assert.Equal(3, all.Rows.Count);
        Assert.Equal(11 + 11, all.ColumnReads);

        // Charter 24212 is in the first row group only: the second reads cu_number, and no other column.
        var one = await ReadTableAsync(file, new() { CuNumbers = [24212] });
        Assert.Equal(new[] { 24212 }, CuNumbers(one.Rows));
        Assert.Equal(11 + 1, one.ColumnReads);
    }

    [Fact]
    public async Task SharesOneStringPerRepeatedValue()
    {
        var host = PublishProfiles(EvidenceFiles.Profiles(ProfileRows, Ncua, RowsPerGroup));
        using var client = Client(host);

        var read = await ReadProfilesAsync(client);
        var again = await ReadProfilesAsync(client);

        // Both rows of the first row group hold TX, and a second string for it would be waste.
        Assert.Same(read[0].GetString("state"), read[1].GetString("state"));
        Assert.Equal("TX", again[0].GetString("state"));
        Assert.NotSame(read[0].GetString("state"), again[0].GetString("state"));
        Assert.Same(read[0].ObservedAtSource, read[2].ObservedAtSource);

        var textHost = PublishText(EvidenceFiles.AccountText(TextRows, RowsPerGroup));
        using var textClient = Client(textHost);
        var text = await ReadTextAsync(textClient);
        Assert.Same(text[0].AccountCode, text[2].AccountCode);
    }

    [Fact]
    public async Task RefusesANullWhereAProfileRequiresAValue()
    {
        ProfileRow[] rows = [ProfileRows[0], ProfileRows[1] with { CuNumber = null }];
        var host = PublishProfiles(EvidenceFiles.Profiles(rows, Ncua));
        using var client = Client(host);

        var e = await Assert.ThrowsAsync<UnsupportedSchemaException>(() => ReadProfilesAsync(client));

        Assert.Contains("\"cu_number\"", e.Message);
        Assert.Contains("upgrade", e.Message);
        Assert.Empty(Directory.GetFileSystemEntries(_temporary));
        Assert.Empty(OpenFiles.Beneath(_root));
    }

    [Fact]
    public async Task RefusesANullWhereTextRequiresAValue()
    {
        AccountTextRow[] rows = [TextRows[0] with { AccountText = null! }];
        var host = PublishText(EvidenceFiles.AccountText(rows));
        using var client = Client(host);

        var e = await Assert.ThrowsAsync<UnsupportedSchemaException>(() => ReadTextAsync(client));

        Assert.Contains("\"account_text\"", e.Message);
        Assert.Contains("upgrade", e.Message);
    }

    [Fact]
    public async Task LeavesOutARegulatorsColumnOfAnotherType()
    {
        // A column of a type the package does not read, between two it does.
        (string Name, Type Type)[] columns = [Ncua[0], ("ratio", typeof(double)), Ncua[1], Ncua[2]];
        var rows = ProfileRows.Select(row => row with
        {
            Values = new Dictionary<string, object?>(row.Values) { ["ratio"] = 0.25 },
        }).ToArray();
        var host = PublishProfiles(EvidenceFiles.Profiles(rows, columns, RowsPerGroup));
        using var client = Client(host);

        var read = await ReadProfilesAsync(client);

        Assert.Equal(new[] { 5163, 24212, 30000 }, CuNumbers(read));
        Assert.Equal(new[] { "credit_union_name", "state", "acct_892a" }, read[0].Columns);
        Assert.Equal("NORTHERN", read[0].GetString("credit_union_name"));
        Assert.Equal("TX", read[0].GetString("state"));
        Assert.Equal(17, read[0].GetInt32("acct_892a"));
        // The left-out name is not a column of the profile, as for any name not in Columns.
        Assert.Throws<ArgumentException>(() => read[0].GetString("ratio"));
        Assert.Throws<ArgumentException>(() => read[0].GetInt32("ratio"));
        Assert.Throws<ArgumentException>(() => read[0].GetValue("ratio"));
        Assert.Empty(Directory.GetFileSystemEntries(_temporary));
    }

    [Fact]
    public async Task RefusesALeadingColumnOfAnotherType()
    {
        var host = PublishProfiles(EvidenceFiles.Profiles(ProfileRows, Ncua, doubleLeading: "version"));
        using var client = Client(host);

        var e = await Assert.ThrowsAsync<UnsupportedSchemaException>(() => ReadProfilesAsync(client));

        Assert.Contains("\"version\"", e.Message);
        Assert.Contains("upgrade", e.Message);
        Assert.Empty(Directory.GetFileSystemEntries(_temporary));
    }

    [Fact]
    public async Task ReadsEveryColumnWhateverItHolds()
    {
        var host = PublishProfiles(EvidenceFiles.Profiles(ProfileRows, Ncua, RowsPerGroup));
        using var client = Client(host);

        var read = await ReadProfilesAsync(client);

        foreach (var profile in read)
        {
            foreach (var name in profile.Columns)
            {
                var value = profile.GetValue(name);
                var isInteger = Ncua.Single(column => column.Name == name).Type == typeof(int);
                object? typed = isInteger ? profile.GetInt32(name) : profile.GetString(name);
                Assert.Equal(typed, value);
            }
        }

        Assert.IsType<string>(read[0].GetValue("state"));
        Assert.IsType<int>(read[0].GetValue("acct_892a"));
        Assert.Equal(17, read[0].GetValue("acct_892a"));
        Assert.Null(read[1].GetValue("acct_892a"));
        Assert.Throws<ArgumentNullException>(() => read[0].GetValue(null!));
        Assert.Throws<ArgumentException>(() => read[0].GetValue("nope"));
        Assert.Contains("property", Assert.Throws<ArgumentException>(() => read[0].GetValue("cu_number")).Message);
    }

    [Fact]
    public async Task RefusesAProfilesFileWithoutALeadingColumn()
    {
        // A text file is not a profiles file: its columns are not the eight.
        var host = PublishProfiles(EvidenceFiles.AccountText(TextRows));
        using var client = Client(host);

        var e = await Assert.ThrowsAsync<UnsupportedSchemaException>(() => ReadProfilesAsync(client));

        Assert.Contains("upgrade", e.Message);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("profiles")]
    public async Task LeavesNothingBehindWhenTheCallerStops(string reader)
    {
        var host = reader == "text"
            ? PublishText(EvidenceFiles.AccountText(TextRows, RowsPerGroup))
            : PublishProfiles(EvidenceFiles.Profiles(ProfileRows, Ncua, RowsPerGroup));
        using var client = Client(host);

        // A break after the first row: the file is open then, and gone after.
        var seen = 0;
        if (reader == "text")
        {
            await foreach (var row in client.Data.ReadAccountTextAsync(Q2026Q3))
            {
                seen++;
                Assert.NotNull(row);
                AssertReading();
                break;
            }
        }
        else
        {
            await foreach (var row in client.Data.ReadProfilesAsync(Q2026Q3))
            {
                seen++;
                Assert.NotNull(row);
                AssertReading();
                break;
            }
        }

        Assert.Equal(1, seen);
        AssertReleased();

        // A token cancelled after the first row.
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (reader == "text")
            {
                await foreach (var row in client.Data.ReadAccountTextAsync(Q2026Q3, null, cancel.Token))
                {
                    await cancel.CancelAsync();
                }
            }
            else
            {
                await foreach (var row in client.Data.ReadProfilesAsync(Q2026Q3, null, cancel.Token))
                {
                    await cancel.CancelAsync();
                }
            }
        });
        AssertReleased();

        // An enumerator never advanced.
        var requests = host.Requests.Count;
        if (reader == "text")
        {
            await client.Data.ReadAccountTextAsync(Q2026Q3).GetAsyncEnumerator().DisposeAsync();
        }
        else
        {
            await client.Data.ReadProfilesAsync(Q2026Q3).GetAsyncEnumerator().DisposeAsync();
        }

        Assert.Equal(requests, host.Requests.Count);
        AssertReleased();
    }

    private void AssertReading()
    {
        if (OperatingSystem.IsLinux())
        {
            Assert.NotEmpty(OpenFiles.Beneath(_root));
        }
    }

    private void AssertReleased()
    {
        Assert.Empty(Directory.GetFileSystemEntries(_temporary));
        Assert.Empty(OpenFiles.Beneath(_root));
    }

    private static ProfileRow Profile(int cuNumber, int? cuid, long submission, string name, string state, int? acct)
    {
        return new ProfileRow(
            "2026Q3",
            CycleDate,
            cuNumber,
            cuid,
            submission,
            1,
            new DateTime(2026, 7, 14, 9, 30, 0, DateTimeKind.Utc).AddHours(cuNumber % 7).AddTicks(1_230),
            "first_seen")
        {
            Values = new Dictionary<string, object?>
            {
                ["credit_union_name"] = name,
                ["state"] = state,
                ["acct_892a"] = acct,
            },
        };
    }

    // The profile of charter cuNumber, whose columns all vary from charter to charter but quarter and cycle_date, and
    // whose optional ones hold nulls.
    private static ProfileRow ManyProfiles(int cuNumber)
    {
        string[] states = ["TX", "CA", "NY", "OH"];
        return new ProfileRow(
            "2026Q3",
            CycleDate,
            cuNumber,
            cuNumber % 5 == 0 ? null : cuNumber + 7,
            900_000L + cuNumber,
            (short)((cuNumber % 4) + 1),
            new DateTime(2026, 7, 14, 9, 30, 0, DateTimeKind.Utc).AddSeconds(cuNumber),
            cuNumber % 2 == 0 ? "first_seen" : "filed")
        {
            Values = new Dictionary<string, object?>
            {
                ["credit_union_name"] = $"CU {cuNumber}",
                ["state"] = cuNumber % 9 == 0 ? null : states[cuNumber % states.Length],
                ["acct_892a"] = cuNumber % 3 == 0 ? null : cuNumber,
            },
        };
    }

    // Every field of a profile as written, and as read.
    private static string Fields(ProfileRow row)
    {
        return string.Join(
            '|',
            row.Quarter,
            row.CycleDate,
            row.CuNumber,
            row.Cuid,
            row.SubmissionId,
            row.Version,
            row.ObservedAt.Ticks,
            row.ObservedAtSource,
            row.Values["credit_union_name"],
            row.Values["state"],
            row.Values["acct_892a"]);
    }

    private static string Fields(FilingProfile profile)
    {
        return string.Join(
            '|',
            profile.Quarter,
            profile.CycleDate,
            profile.CuNumber,
            profile.Cuid,
            profile.SubmissionId,
            profile.Version,
            profile.ObservedAt.UtcTicks,
            profile.ObservedAtSource,
            profile.GetString("credit_union_name"),
            profile.GetString("state"),
            profile.GetInt32("acct_892a"));
    }

    private static AccountText ToText(AccountTextRow row)
    {
        return new AccountText
        {
            Quarter = Quarter.Parse(row.Quarter),
            CycleDate = row.CycleDate,
            CuNumber = row.CuNumber,
            SubmissionId = row.SubmissionId,
            AccountCode = row.AccountCode,
            Text = row.AccountText,
            IngestedAt = new DateTimeOffset(row.IngestedAt.Ticks, TimeSpan.Zero),
        };
    }

    private static AccountText[] Texts(params int[] rows)
    {
        return [.. rows.Select(row => ToText(TextRows[row]))];
    }

    private static int[] CuNumbers(IEnumerable<FilingProfile> profiles)
    {
        return [.. profiles.Select(profile => profile.CuNumber)];
    }

    private static Uri ManifestUri(FixtureHost host)
    {
        return new Uri(host.BaseAddress, FixtureHost.ManifestPath);
    }

    private static string TextPath => "curated/accounts_text/quarter=2026Q3/data.parquet";

    private static FixtureHost PublishText(byte[] body)
    {
        var host = new FixtureHost();
        var entry = host.AddQuarter("2026Q3");
        host.AddFile(entry, EvidenceTable.Accounts, EvidenceFiles.Accounts([]));
        host.AddFile(entry, EvidenceTable.AccountText, body);
        return host;
    }

    private static FixtureHost PublishProfiles(byte[] body)
    {
        var host = new FixtureHost();
        var entry = host.AddQuarter("2026Q3");
        host.AddFile(entry, EvidenceTable.Accounts, EvidenceFiles.Accounts([]));
        host.AddFile(entry, EvidenceTable.Profiles, body);
        return host;
    }

    private static Task<List<AccountText>> ReadTextAsync(QuarterCallClient client, AccountTextFilter? filter = null)
    {
        return client.Data.ReadAccountTextAsync(Q2026Q3, filter).ToListAsync().AsTask();
    }

    private static Task<List<FilingProfile>> ReadProfilesAsync(QuarterCallClient client, ProfileFilter? filter = null)
    {
        return client.Data.ReadProfilesAsync(Q2026Q3, filter).ToListAsync().AsTask();
    }

    private static async Task<(List<FilingProfile> Rows, int ColumnReads)> ReadTableAsync(
        VerifiedFile file, ProfileFilter? filter)
    {
        await using var table = await ParquetTable.OpenAsync(file, CancellationToken.None);
        var selection = QuarterCall.Internal.ProfileRows.Select(filter, nameof(filter));
        var rows = await selection.ReadAsync(table, CancellationToken.None).ToListAsync();
        return (rows, table.ColumnReads);
    }

    private QuarterCallClient Client(FixtureHost host)
    {
        return host.CreateClient(null, _temporary);
    }
}
