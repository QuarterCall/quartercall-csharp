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

/// <summary>How an accounts file departs from the published schema, for a test of a file read or refused.</summary>
internal sealed record AccountsShape
{
    /// <summary>A column left out of the file, or null to keep every one.</summary>
    public string? Without { get; init; }

    /// <summary>Whether the file has one more column, <c>note</c>, after the published ones.</summary>
    public bool WithExtraColumn { get; init; }

    /// <summary>Whether <c>cu_number</c> is declared nullable, as <c>int?</c>.</summary>
    public bool NullableCuNumber { get; init; }

    /// <summary>
    /// The row, counted from the first of the file, whose <c>cu_number</c> is written as a null; it needs
    /// <see cref="NullableCuNumber"/>.
    /// </summary>
    public int? NullCuNumberAt { get; init; }
}

/// <summary>Writes Parquet files with Parquet.Net, so a reader can be tested on that writer's bytes.</summary>
internal static class EvidenceFiles
{
    /// <summary>
    /// The bytes of an accounts file holding <paramref name="rows"/>, Snappy-compressed, with a row group for each
    /// <paramref name="rowsPerGroup"/> rows.
    /// </summary>
    public static byte[] Accounts(IReadOnlyList<AccountRow> rows, int rowsPerGroup = int.MaxValue)
    {
        return Accounts(rows, new AccountsShape(), rowsPerGroup);
    }

    /// <summary>
    /// The bytes of an accounts file holding <paramref name="rows"/> in the schema <paramref name="shape"/> gives,
    /// Snappy-compressed, with a row group for each <paramref name="rowsPerGroup"/> rows.
    /// </summary>
    public static byte[] Accounts(IReadOnlyList<AccountRow> rows, AccountsShape shape, int rowsPerGroup = int.MaxValue)
    {
        // Parquet.Net writes only asynchronously. Running the write on the thread pool keeps this wait clear of
        // the caller's synchronisation context.
        return Task.Run(() => WriteAccountsAsync(rows, shape, rowsPerGroup)).GetAwaiter().GetResult();
    }

    private static async Task<byte[]> WriteAccountsAsync(
        IReadOnlyList<AccountRow> rows, AccountsShape shape, int rowsPerGroup)
    {
        var schema = new ParquetSchema([.. Fields(shape)]);
        var options = new ParquetOptions { CompressionMethod = CompressionMethod.Snappy };
        using var output = new MemoryStream();
        await using (var writer = await ParquetWriter.CreateAsync(schema, output, options))
        {
            var first = 0;
            foreach (var group in rows.Chunk(rowsPerGroup))
            {
                await WriteGroupAsync(writer, schema, group, first, shape);
                first += group.Length;
            }
        }

        return output.ToArray();
    }

    // The published accounts file's fields in order, less the one left out, with cu_number as declared and the extra
    // one last. They are made afresh for each file: building a schema changes its fields, so a schema built from
    // fields another test is writing with breaks that write, which has read back one column's values in another.
    private static IEnumerable<Field> Fields(AccountsShape shape)
    {
        DataField[] published =
        [
            new DataField<string>("quarter"),
            new DateTimeDataField("cycle_date", DateTimeFormat.Date),
            shape.NullableCuNumber ? new DataField<int?>("cu_number") : new DataField<int>("cu_number"),
            new DataField<long>("submission_id"),
            new DataField<short>("version"),
            new DataField<string>("account_code"),
            new DecimalDataField("account_value", 24, 8, isNullable: true),
            new DataField<DateTime>("ingested_at"),
            new DataField<int?>("cuid"),
        ];
        foreach (var field in published)
        {
            if (field.Name != shape.Without)
            {
                yield return field;
            }
        }

        if (shape.WithExtraColumn)
        {
            yield return new DataField<string>("note");
        }
    }

    private static async Task WriteGroupAsync(
        ParquetWriter writer, ParquetSchema schema, AccountRow[] rows, int first, AccountsShape shape)
    {
        // Disposing the row group writer completes the row group. Columns are written in the schema's order.
        using var group = writer.CreateRowGroup();
        foreach (var field in schema.DataFields)
        {
            await WriteColumnAsync(group, field, rows, first, shape);
        }
    }

    // Writes the column field names, through the schema's own field.
    private static Task WriteColumnAsync(
        ParquetRowGroupWriter group, DataField field, AccountRow[] rows, int first, AccountsShape shape)
    {
        return field.Name switch
        {
            "quarter" => group.WriteAsync(field, rows.Select(row => row.Quarter).ToArray()),
            // A DATE column takes DateTime values; the time of day is dropped.
            "cycle_date" => group.WriteAsync<DateTime>(
                field, rows.Select(row => row.CycleDate.ToDateTime(TimeOnly.MinValue)).ToArray()),
            "cu_number" when shape.NullableCuNumber => group.WriteAsync<int>(
                field,
                rows.Select((row, i) => first + i == shape.NullCuNumberAt ? null : (int?)row.CuNumber).ToArray()),
            "cu_number" => group.WriteAsync<int>(field, rows.Select(row => row.CuNumber).ToArray()),
            "submission_id" => group.WriteAsync<long>(field, rows.Select(row => row.SubmissionId).ToArray()),
            "version" => group.WriteAsync<short>(field, rows.Select(row => row.Version).ToArray()),
            "account_code" => group.WriteAsync(field, rows.Select(row => row.AccountCode).ToArray()),
            "account_value" => group.WriteAsync<decimal>(field, rows.Select(row => row.AccountValue).ToArray()),
            "ingested_at" => group.WriteAsync<DateTime>(field, rows.Select(row => row.IngestedAt).ToArray()),
            "cuid" => group.WriteAsync<int>(field, rows.Select(row => row.Cuid).ToArray()),
            "note" => group.WriteAsync(field, rows.Select(_ => "extra").ToArray()),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field.Name, null),
        };
    }
}
