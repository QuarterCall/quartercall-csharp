# QuarterCall

Credit-union Call Report data (NCUA Form 5300) as typed C# records. The data is published as static
Parquet files on `data.quartercall.com`; this package reads them. For the data's own documentation, see
<https://data.quartercall.com/>.

```csharp
using QuarterCall;

using var qc = new QuarterCallClient();
var filter = new AccountFilter { Codes = ["A010"] };

await foreach (Account a in qc.Data.ReadAccountsAsync(Quarter.Parse("2026Q3"), filter).Take(5))
{
    Console.WriteLine($"{a.CuNumber} {a.AccountCode} {a.AccountValue}");
}
```

This is a complete `Program.cs` for a console project with implicit usings on, which is the default for a new one. It prints five charter numbers with account `A010` and its value, and downloads one file of about 25 MB.

## Install

Requires .NET 10.

```
dotnet add package QuarterCall
```

## Quarters

Every read names a quarter. List what is published, newest first:

```csharp
foreach (PublishedQuarter q in await qc.Data.GetQuartersAsync())
{
    Console.WriteLine($"{q.Quarter} {q.Status} {q.CuCount}");
}
```

`Quarter.Parse("2026Q3")` or `new Quarter(2026, 3)` makes one. Quarters compare by time.

## Readers

Each reader streams one quarter, one row group at a time, so memory does not grow with the quarter.

| Method | Rows |
|---|---|
| `ReadAccountsAsync` | `Account`: one value of one filing |
| `ReadAccountTextAsync` | `AccountText`: account values that are text |
| `ReadProfilesAsync` | `FilingProfile`: one profile for each credit union's filing |

Account values:

```csharp
var filter = new AccountFilter { CuNumbers = [5536], Codes = ["A010", "A018"] };
await foreach (Account a in qc.Data.ReadAccountsAsync(Quarter.Parse("2026Q3"), filter))
{
    Console.WriteLine($"{a.AccountCode} {a.AccountValue}");
}
```

Text values. A quarter with none yields no rows:

```csharp
var filter = new AccountTextFilter { CuNumbers = [5536] };
await foreach (AccountText t in qc.Data.ReadAccountTextAsync(Quarter.Parse("2026Q3"), filter))
{
    Console.WriteLine($"{t.AccountCode} {t.Text}");
}
```

Profiles. The first eight columns are properties. The regulator's own columns, about 179, are read by
name: `Columns` lists them, `GetString` reads a text column and `GetInt32` an integer one. `GetValue`
reads a column whatever it holds, to print or copy every column. Text and integer columns are listed, so
one the regulator adds is readable without a new version of the package; a column of another type is
left out until a version of the package reads it.

```csharp
var filter = new ProfileFilter { CuNumbers = [5536] };
await foreach (FilingProfile p in qc.Data.ReadProfilesAsync(Quarter.Parse("2026Q3"), filter))
{
    Console.WriteLine($"{p.CuNumber} {p.Columns.Count} columns, first: {p.Columns[0]}");
}
```

A filter's sets are combined: a row must be in every set given. A null set does not filter; an empty set
selects no row.

## Selection reduces what is returned, not what is downloaded

A filter drops rows after the file has arrived. Reading one account of one credit union downloads the
whole quarter's file.

## Downloads

Save a quarter's file as the host publishes it, to read with another tool:

```csharp
PublishedFile f = await qc.Data.DownloadAsync(
    Quarter.Parse("2026Q3"), EvidenceTable.Accounts, "accounts-2026Q3.parquet");
Console.WriteLine($"{f.Bytes} bytes, SHA-256 {f.Sha256}");
```

`EvidenceTable` names `Accounts`, `AccountText`, `Profiles` and `Quorum`. `DownloadIndexAsync` saves the
index of every filing the host holds.

## Integrity

Every file is checked against the length and SHA-256 digest in the host's manifest before it is read or
written. A file that does not match is fetched again from a fresh manifest, three attempts in all.
`DataIntegrityException` means all three failed, or the manifest could not be read. Most often the quarter
is being republished: try again later.

## Missing values

A value the filing does not give is `null`, never zero. `Account.AccountValue` is `decimal?`, exact to its
eight decimal places.

## No quarter is final

An amendment can arrive for a quarter of any age. `PublishedQuarter.Status` is `Open` while filings are
still arriving, and `Dormant` when none has been validated for more than a year. A dormant quarter
becomes open again if a late amendment arrives. Read the same quarter twice and the answers may differ.

`PublishedQuarter.Quorum` says how much of the system has filed. The verdicts are the host's; the package
computes none:

```csharp
PublishedQuarter q = (await qc.Data.GetQuartersAsync())[0];
if (q.Quorum is { } quorum)
{
    Console.WriteLine($"{q.Quarter}: {quorum.Filed} of {quorum.Expected} filed");
    Console.WriteLine(quorum.HasReached("floor/1") ? "floor/1 reached" : "floor/1 not reached");
}
```

`HasReached` is false for any other status, and for a policy the quorum does not name. Each member is
null when the host leaves it out.

## Cache

```csharp
using var qc = new QuarterCallClient(new QuarterCallOptions { CacheDirectory = "qc-cache" });
```

With a cache directory, a verified file is kept, named by its digest, and a later read of the same file
does not download it again. A changed file has a new digest, so it is fetched. The client never removes
a file: delete the directory when you want the space back. The layout is the `quartercall` Python
package's, so the two can share one directory.

Without a cache, a download is kept in the temporary directory only while it is read.

## Options

`QuarterCallOptions` has three settings: `DataBaseAddress` (default `https://data.quartercall.com/`),
`CacheDirectory` (default none) and `Timeout`.

`Timeout` covers one request to its last byte, and defaults to 100 seconds. A slow connection may need a
longer one for a file of tens of megabytes. A request that takes longer throws `TaskCanceledException`
holding a `TimeoutException`.

To send through an `HttpClient` you manage, such as one from `IHttpClientFactory`, pass it in. It is used
as given: its settings are not changed and the `QuarterCallClient` does not dispose it. Its own
`Timeout` still applies.

```csharp
using var qc = new QuarterCallClient(httpClient, new QuarterCallOptions { CacheDirectory = "qc-cache" });
```

## Exceptions

Three exceptions, each a `QuarterCallException`, so catching the base catches any of them. The table lists the base and the three.

| Exception | Meaning |
|---|---|
| `QuarterCallException` | The base of the three below. |
| `DataUnavailableException` | The quarter, table or file asked for is not published. |
| `DataIntegrityException` | What the host published does not match its description. |
| `UnsupportedSchemaException` | The host publishes a form this version does not read. Upgrade the package. |

Network failures are `HttpRequestException`; cancellation is `OperationCanceledException`.

## Not in this release

Measures and ratios.

## Licence

Apache-2.0. See [LICENSE](https://github.com/QuarterCall/quartercall-csharp/blob/main/LICENSE).
