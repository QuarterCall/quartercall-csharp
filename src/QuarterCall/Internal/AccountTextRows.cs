using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace QuarterCall.Internal;

/// <summary>
/// The rows of an accounts-text file that a filter selects, read as <see cref="AccountText"/> records. The filter is
/// checked and copied when this is made. Each read of a table is a stream of its own, with its own strings.
/// </summary>
internal sealed class AccountTextRows
{
    private const string TableName = "accounts-text";

    // The published columns.
    private const string QuarterColumn = "quarter";
    private const string CycleDateColumn = "cycle_date";
    private const string CuNumberColumn = "cu_number";
    private const string SubmissionIdColumn = "submission_id";
    private const string AccountCodeColumn = "account_code";
    private const string AccountTextColumn = "account_text";
    private const string IngestedAtColumn = "ingested_at";

    // Every column a record is read from, and the type the file must hold it as.
    private static readonly (string Name, Type Type)[] Columns =
    [
        (QuarterColumn, typeof(ReadOnlyMemory<char>)),
        (CycleDateColumn, typeof(DateOnly)),
        (CuNumberColumn, typeof(int)),
        (SubmissionIdColumn, typeof(long)),
        (AccountCodeColumn, typeof(ReadOnlyMemory<char>)),
        (AccountTextColumn, typeof(ReadOnlyMemory<char>)),
        (IngestedAtColumn, typeof(DateTime)),
    ];

    private static readonly AccountTextRows Every = new(null, null);

    // Each set is null where the filter does not restrict its column.
    private readonly FrozenSet<string>? _codes;
    private readonly FrozenSet<int>? _cuNumbers;

    // The codes probed by a row's characters, so a row that does not match makes no string.
    private readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _codeSpans;

    private AccountTextRows(FrozenSet<string>? codes, FrozenSet<int>? cuNumbers)
    {
        _codes = codes;
        _cuNumbers = cuNumbers;
        if (codes is not null)
        {
            _codeSpans = codes.GetAlternateLookup<ReadOnlySpan<char>>();
        }
    }

    /// <summary>Whether a set of the filter is empty, so that no row is selected.</summary>
    public bool SelectsNone => _codes is { Count: 0 } || _cuNumbers is { Count: 0 };

    private bool Filters => _codes is not null || _cuNumbers is not null;

    /// <summary>The rows <paramref name="filter"/> selects, every row when it is null.</summary>
    /// <param name="filter">The filter, copied here.</param>
    /// <param name="paramName">The parameter that held the filter, for the exception.</param>
    /// <exception cref="ArgumentException">A code is null or empty, or a charter number is less than 1.</exception>
    public static AccountTextRows Select(AccountTextFilter? filter, string paramName)
    {
        if (filter is null)
        {
            return Every;
        }

        return new AccountTextRows(
            ReadRules.FreezeCodes(filter.Codes, paramName),
            ReadRules.FreezeIdentifiers(filter.CuNumbers, nameof(AccountTextFilter.CuNumbers), paramName));
    }

    /// <summary>
    /// Reads the selected rows of <paramref name="table"/>, row group by row group, in the file's order. The filter's
    /// columns are read first, and a row group in which no row matches reads no other.
    /// </summary>
    /// <exception cref="UnsupportedSchemaException">
    /// The file lacks a column, or holds one as another type, or holds a null where a record requires a value, or a
    /// <c>quarter</c> that is not a quarter's label, or cannot be read.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async IAsyncEnumerable<AccountText> ReadAsync(
        ParquetTable table, [EnumeratorCancellation] CancellationToken ct)
    {
        // Every column is checked before any is read, so a file without one is refused whatever the filter.
        foreach (var (name, type) in Columns)
        {
            table.Require(name, type);
        }

        var group = new Group(this);
        for (var index = 0; index < table.GroupCount; index++)
        {
            var rows = await table.OpenGroupAsync(index, ct).ConfigureAwait(false);
            if (rows == 0)
            {
                continue;
            }

            await group.ReadFilterAsync(table, ct).ConfigureAwait(false);
            var row = group.NextMatch(0, rows);
            if (row == rows)
            {
                continue;
            }

            await group.ReadOthersAsync(table, ct).ConfigureAwait(false);
            for (; row < rows; row = group.NextMatch(row + 1, rows))
            {
                // Rows are built without an await, so a cancellation between them is seen here.
                ct.ThrowIfCancellationRequested();
                yield return group.Build(row);
            }
        }
    }

    private static T Required<T>(T? value, string column)
        where T : struct
    {
        return ReadRules.Required(value, TableName, column);
    }

    // Whether the filter selects the row. Only the filter's own columns have been read when this is asked.
    private bool Selects(ReadOnlySpan<ReadOnlyMemory<char>?> codes, ReadOnlySpan<int?> cuNumbers, int row)
    {
        if (_codes is not null && !_codeSpans.Contains(Required(codes[row], AccountCodeColumn).Span))
        {
            return false;
        }

        return _cuNumbers is null || _cuNumbers.Contains(Required(cuNumbers[row], CuNumberColumn));
    }

    // One stream's reading: the open row group's columns, and the strings and quarters met so far. Each column is
    // memory the table lends until its next row group.
    private sealed class Group(AccountTextRows filter)
    {
        private readonly StringPool _codes = new();
        private readonly QuarterLabels _quarters = new(TableName);
        private ReadOnlyMemory<ReadOnlyMemory<char>?> _quarter;
        private ReadOnlyMemory<DateOnly?> _cycleDate;
        private ReadOnlyMemory<int?> _cuNumber;
        private ReadOnlyMemory<long?> _submissionId;
        private ReadOnlyMemory<ReadOnlyMemory<char>?> _accountCode;
        private ReadOnlyMemory<ReadOnlyMemory<char>?> _accountText;
        private ReadOnlyMemory<DateTime?> _ingestedAt;

        // Reads the columns the filter matches on.
        public async Task ReadFilterAsync(ParquetTable table, CancellationToken ct)
        {
            if (filter._codes is not null)
            {
                _accountCode = await table.ReadTextAsync(AccountCodeColumn, ct).ConfigureAwait(false);
            }

            if (filter._cuNumbers is not null)
            {
                _cuNumber = await table.ReadAsync<int>(CuNumberColumn, ct).ConfigureAwait(false);
            }
        }

        // Reads the columns the filter did not, once a row of the group is selected.
        public async Task ReadOthersAsync(ParquetTable table, CancellationToken ct)
        {
            _quarter = await table.ReadTextAsync(QuarterColumn, ct).ConfigureAwait(false);
            _cycleDate = await table.ReadAsync<DateOnly>(CycleDateColumn, ct).ConfigureAwait(false);
            if (filter._cuNumbers is null)
            {
                _cuNumber = await table.ReadAsync<int>(CuNumberColumn, ct).ConfigureAwait(false);
            }

            _submissionId = await table.ReadAsync<long>(SubmissionIdColumn, ct).ConfigureAwait(false);
            if (filter._codes is null)
            {
                _accountCode = await table.ReadTextAsync(AccountCodeColumn, ct).ConfigureAwait(false);
            }

            _accountText = await table.ReadTextAsync(AccountTextColumn, ct).ConfigureAwait(false);
            _ingestedAt = await table.ReadAsync<DateTime>(IngestedAtColumn, ct).ConfigureAwait(false);
        }

        // The first row from row on that the filter selects, or rows when none does.
        public int NextMatch(int row, int rows)
        {
            if (!filter.Filters)
            {
                return row;
            }

            var codes = _accountCode.Span;
            var cuNumbers = _cuNumber.Span;
            for (; row < rows; row++)
            {
                if (filter.Selects(codes, cuNumbers, row))
                {
                    return row;
                }
            }

            return rows;
        }

        public AccountText Build(int row)
        {
            return new AccountText
            {
                Quarter = _quarters.Parse(Required(_quarter.Span[row], QuarterColumn).Span),
                CycleDate = Required(_cycleDate.Span[row], CycleDateColumn),
                CuNumber = Required(_cuNumber.Span[row], CuNumberColumn),
                SubmissionId = Required(_submissionId.Span[row], SubmissionIdColumn),
                // A code repeats on every filing; the text is the value as filed, made a string as it is.
                AccountCode = _codes.Get(Required(_accountCode.Span[row], AccountCodeColumn).Span),
                Text = Required(_accountText.Span[row], AccountTextColumn).ToString(),
                // The file holds UTC times, which Parquet.Net gives as of no zone, or of the local one. The time is
                // kept and marked UTC: converting it from local time would move it.
                IngestedAt = new DateTimeOffset(
                    DateTime.SpecifyKind(Required(_ingestedAt.Span[row], IngestedAtColumn), DateTimeKind.Utc)),
            };
        }
    }
}
