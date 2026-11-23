using Parquet;
using Parquet.Schema;

namespace QuarterCall.Tests.Support;

/// <summary>One row of the published accounts file.</summary>
internal sealed record AccountRow(
    string Quarter,
    DateOnly CycleDate,
    int CuNumber,
    long SubmissionId,
    short Version,
    string AccountCode,
    decimal? AccountValue,
    DateTime IngestedAt,
    int? Cuid);

/// <summary>Writes Parquet files with Parquet.Net, so a reader can be tested on that writer's bytes.</summary>
internal static class EvidenceFiles
{
    // The published accounts file's schema, column by column.
    private static readonly DataField<string> Quarter = new("quarter");
    private static readonly DateTimeDataField CycleDate = new("cycle_date", DateTimeFormat.Date);
    private static readonly DataField<int> CuNumber = new("cu_number");
    private static readonly DataField<long> SubmissionId = new("submission_id");
    private static readonly DataField<short> Version = new("version");
    private static readonly DataField<string> AccountCode = new("account_code");
    private static readonly DecimalDataField AccountValue = new("account_value", 24, 8, isNullable: true);
    private static readonly DataField<DateTime> IngestedAt = new("ingested_at");
    private static readonly DataField<int?> Cuid = new("cuid");

    private static readonly ParquetSchema AccountsSchema = new(
        Quarter, CycleDate, CuNumber, SubmissionId, Version, AccountCode, AccountValue, IngestedAt, Cuid);

    /// <summary>
    /// The bytes of an accounts file holding <paramref name="rows"/>, Snappy-compressed, with a row group for each
    /// <paramref name="rowsPerGroup"/> rows.
    /// </summary>
    public static byte[] Accounts(IReadOnlyList<AccountRow> rows, int rowsPerGroup = int.MaxValue)
    {
        // Parquet.Net writes only asynchronously. Running the write on the thread pool keeps this wait clear of
        // the caller's synchronisation context.
        return Task.Run(() => WriteAccountsAsync(rows, rowsPerGroup)).GetAwaiter().GetResult();
    }

    private static async Task<byte[]> WriteAccountsAsync(IReadOnlyList<AccountRow> rows, int rowsPerGroup)
    {
        var options = new ParquetOptions { CompressionMethod = CompressionMethod.Snappy };
        using var output = new MemoryStream();
        await using (var writer = await ParquetWriter.CreateAsync(AccountsSchema, output, options))
        {
            foreach (var group in rows.Chunk(rowsPerGroup))
            {
                await WriteGroupAsync(writer, group);
            }
        }

        return output.ToArray();
    }

    private static async Task WriteGroupAsync(ParquetWriter writer, AccountRow[] rows)
    {
        // Disposing the row group writer completes the row group.
        using var group = writer.CreateRowGroup();
        await group.WriteAsync(Quarter, rows.Select(row => row.Quarter).ToArray());
        // A DATE column takes DateTime values; the time of day is dropped.
        await group.WriteAsync<DateTime>(
            CycleDate, rows.Select(row => row.CycleDate.ToDateTime(TimeOnly.MinValue)).ToArray());
        await group.WriteAsync<int>(CuNumber, rows.Select(row => row.CuNumber).ToArray());
        await group.WriteAsync<long>(SubmissionId, rows.Select(row => row.SubmissionId).ToArray());
        await group.WriteAsync<short>(Version, rows.Select(row => row.Version).ToArray());
        await group.WriteAsync(AccountCode, rows.Select(row => row.AccountCode).ToArray());
        await group.WriteAsync<decimal>(AccountValue, rows.Select(row => row.AccountValue).ToArray());
        await group.WriteAsync<DateTime>(IngestedAt, rows.Select(row => row.IngestedAt).ToArray());
        await group.WriteAsync<int>(Cuid, rows.Select(row => row.Cuid).ToArray());
    }
}
