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

/// <summary>One row of the published accounts-text file.</summary>
internal sealed record AccountTextRow(
    string Quarter,
    DateOnly CycleDate,
    int CuNumber,
    long SubmissionId,
    string AccountCode,
    string AccountText,
    DateTime IngestedAt);

/// <summary>
/// One row of the published profiles file: its eight leading columns, and the regulator's columns it holds a value
/// for, by name; a column not named is null. A null <see cref="CuNumber"/> is written as the file's null.
/// </summary>
internal sealed record ProfileRow(
    string Quarter,
    DateOnly CycleDate,
    int? CuNumber,
    int? Cuid,
    long SubmissionId,
    short Version,
    DateTime ObservedAt,
    string ObservedAtSource)
{
    /// <summary>The regulator's columns' values: a string, an int or a double, by the column's type.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; init; } = new Dictionary<string, object?>();
}

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

    /// <summary>The bytes of an accounts-text file holding <paramref name="rows"/>, one row group for each
    /// <paramref name="rowsPerGroup"/> rows.</summary>
    public static byte[] AccountText(IReadOnlyList<AccountTextRow> rows, int rowsPerGroup = int.MaxValue)
    {
        // Fresh fields for each file: see Fields.
        DataField[] fields =
        [
            new DataField<string>("quarter"),
            new DateTimeDataField("cycle_date", DateTimeFormat.Date),
            new DataField<int>("cu_number"),
            new DataField<long>("submission_id"),
            new DataField<string>("account_code"),
            new DataField<string>("account_text"),
            new DataField<DateTime>("ingested_at"),
        ];
        return WriteGroups(fields, rows, rowsPerGroup, (group, field, chunk) => field.Name switch
        {
            "quarter" => group.WriteAsync(field, chunk.Select(row => row.Quarter).ToArray()),
            "cycle_date" => group.WriteAsync<DateTime>(
                field, chunk.Select(row => row.CycleDate.ToDateTime(TimeOnly.MinValue)).ToArray()),
            "cu_number" => group.WriteAsync<int>(field, chunk.Select(row => row.CuNumber).ToArray()),
            "submission_id" => group.WriteAsync<long>(field, chunk.Select(row => row.SubmissionId).ToArray()),
            "account_code" => group.WriteAsync(field, chunk.Select(row => row.AccountCode).ToArray()),
            "account_text" => group.WriteAsync(field, chunk.Select(row => row.AccountText).ToArray()),
            "ingested_at" => group.WriteAsync<DateTime>(field, chunk.Select(row => row.IngestedAt).ToArray()),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field.Name, null),
        });
    }

    /// <summary>
    /// The bytes of a profiles file holding <paramref name="rows"/>: the eight leading columns, every one nullable
    /// but <c>quarter</c>, and then <paramref name="ncuaColumns"/> in the order given, each nullable. A column's
    /// type is text, <see cref="int"/> or, for a test of a file that is refused, <see cref="double"/>.
    /// <c>observed_at</c> is a timestamp in microseconds, not adjusted to UTC.
    /// </summary>
    public static byte[] Profiles(
        IReadOnlyList<ProfileRow> rows,
        IReadOnlyList<(string Name, Type Type)> ncuaColumns,
        int rowsPerGroup = int.MaxValue)
    {
        List<DataField> fields =
        [
            new DataField<string>("quarter", nullable: false),
            new DateTimeDataField("cycle_date", DateTimeFormat.Date, isNullable: true),
            new DataField<int?>("cu_number"),
            new DataField<int?>("cuid"),
            new DataField<long?>("submission_id"),
            new DataField<short?>("version"),
            new DateTimeDataField(
                "observed_at", DateTimeFormat.DateAndTimeMicros, isAdjustedToUTC: false, isNullable: true),
            new DataField<string>("observed_at_source"),
        ];
        foreach (var (name, type) in ncuaColumns)
        {
            fields.Add(type switch
            {
                _ when type == typeof(int) => new DataField<int?>(name),
                _ when type == typeof(string) => new DataField<string>(name),
                _ when type == typeof(double) => new DataField<double?>(name),
                _ => throw new ArgumentOutOfRangeException(nameof(ncuaColumns), type, null),
            });
        }

        return WriteGroups(fields, rows, rowsPerGroup, (group, field, chunk) => field.Name switch
        {
            "quarter" => group.WriteAsync(field, chunk.Select(row => row.Quarter).ToArray()),
            "cycle_date" => group.WriteAsync<DateTime>(
                field, chunk.Select(row => (DateTime?)row.CycleDate.ToDateTime(TimeOnly.MinValue)).ToArray()),
            "cu_number" => group.WriteAsync<int>(field, chunk.Select(row => row.CuNumber).ToArray()),
            "cuid" => group.WriteAsync<int>(field, chunk.Select(row => row.Cuid).ToArray()),
            "submission_id" => group.WriteAsync<long>(field, chunk.Select(row => (long?)row.SubmissionId).ToArray()),
            "version" => group.WriteAsync<short>(field, chunk.Select(row => (short?)row.Version).ToArray()),
            "observed_at" => group.WriteAsync<DateTime>(
                field, chunk.Select(row => (DateTime?)row.ObservedAt).ToArray()),
            "observed_at_source" => group.WriteAsync(field, chunk.Select(row => row.ObservedAtSource).ToArray()),
            _ => WriteNcuaColumn(group, field, chunk),
        });
    }

    private static Task WriteNcuaColumn(ParquetRowGroupWriter group, DataField field, ProfileRow[] chunk)
    {
        object? Value(ProfileRow row)
        {
            return row.Values.GetValueOrDefault(field.Name);
        }

        if (field.ClrType == typeof(int))
        {
            return group.WriteAsync<int>(field, chunk.Select(row => (int?)Value(row)).ToArray());
        }

        if (field.ClrType == typeof(double))
        {
            return group.WriteAsync<double>(field, chunk.Select(row => (double?)Value(row)).ToArray());
        }

        return group.WriteAsync(field, chunk.Select(row => (string?)Value(row)).ToArray());
    }

    // Writes rows in row groups of rowsPerGroup, each column by write. Parquet.Net writes only asynchronously, so the
    // write runs on the thread pool, which keeps this wait clear of the caller's synchronisation context.
    private static byte[] WriteGroups<TRow>(
        IReadOnlyList<DataField> fields,
        IReadOnlyList<TRow> rows,
        int rowsPerGroup,
        Func<ParquetRowGroupWriter, DataField, TRow[], Task> write)
    {
        return Task.Run(async () =>
        {
            var schema = new ParquetSchema([.. fields]);
            var options = new ParquetOptions { CompressionMethod = CompressionMethod.Snappy };
            using var output = new MemoryStream();
            await using (var writer = await ParquetWriter.CreateAsync(schema, output, options))
            {
                foreach (var chunk in rows.Chunk(rowsPerGroup))
                {
                    using var group = writer.CreateRowGroup();
                    foreach (var field in schema.DataFields)
                    {
                        await write(group, field, chunk);
                    }
                }
            }

            return output.ToArray();
        }).GetAwaiter().GetResult();
    }
}
