using System.Collections.Frozen;
using System.Runtime.CompilerServices;

namespace QuarterCall.Internal;

/// <summary>
/// Where each of a profiles file's regulator columns is held in a <see cref="FilingProfile"/>: the file's columns
/// after the eight that lead it, in file order, each text or a 32-bit integer. One index serves every row of a stream.
/// </summary>
internal sealed class ProfileColumns
{
    private readonly Dictionary<string, (bool IsText, int Slot)> _slots = new(StringComparer.Ordinal);

    private ProfileColumns(IReadOnlyList<string> names, IReadOnlyList<(string Name, bool IsText)> kinds)
    {
        Names = names;
        var texts = new List<string>();
        var numbers = new List<string>();
        foreach (var (name, isText) in kinds)
        {
            var list = isText ? texts : numbers;
            _slots.Add(name, (isText, list.Count));
            list.Add(name);
        }

        TextNames = [.. texts];
        NumberNames = [.. numbers];
    }

    /// <summary>The regulator's columns, in the file's order.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The text columns, in the order of their slots.</summary>
    public string[] TextNames { get; }

    /// <summary>The integer columns, in the order of their slots.</summary>
    public string[] NumberNames { get; }

    /// <summary>
    /// The columns of <paramref name="table"/> after <paramref name="leading"/>, which it must hold in the types
    /// given.
    /// </summary>
    /// <exception cref="UnsupportedSchemaException">
    /// A leading column is missing or of another type, or another column is neither text nor a 32-bit integer.
    /// </exception>
    public static ProfileColumns Of(ParquetTable table, IReadOnlyList<(string Name, Type Type)> leading)
    {
        foreach (var (name, type) in leading)
        {
            table.Require(name, type);
        }

        var skipped = leading.Select(column => column.Name).ToFrozenSet(StringComparer.Ordinal);
        var names = new List<string>();
        var kinds = new List<(string Name, bool IsText)>();
        foreach (var name in table.ColumnNames)
        {
            if (skipped.Contains(name))
            {
                continue;
            }

            var type = table.ColumnType(name);
            if (type != typeof(ReadOnlyMemory<char>) && type != typeof(int))
            {
                throw new UnsupportedSchemaException(
                    $"The profiles file's column \"{name}\" holds {type.Name}, where this version of QuarterCall "
                    + $"reads text or a 32-bit integer in the regulator's columns. The file may be of a newer form. "
                    + ReadRules.Remedy);
            }

            names.Add(name);
            kinds.Add((name, type == typeof(ReadOnlyMemory<char>)));
        }

        return new ProfileColumns(names.AsReadOnly(), kinds);
    }

    /// <summary>Where the column <paramref name="name"/> is held.</summary>
    /// <exception cref="ArgumentException">The file has no regulator's column of that name.</exception>
    public (bool IsText, int Slot) Find(string name)
    {
        if (_slots.TryGetValue(name, out var slot))
        {
            return slot;
        }

        throw new ArgumentException(
            $"The profile has no column \"{name}\" among its {nameof(FilingProfile.Columns)}, which are the "
            + "regulator's, matched exactly. One of the eight leading columns is read from its property.",
            nameof(name));
    }
}

/// <summary>
/// The rows of a profiles file that a filter selects, read as <see cref="FilingProfile"/> records. The filter is
/// checked and copied when this is made. Each read of a table is a stream of its own, with its own strings.
/// </summary>
internal sealed class ProfileRows
{
    private const string TableName = "profiles";

    // The published columns that lead the file.
    private const string QuarterColumn = "quarter";
    private const string CycleDateColumn = "cycle_date";
    private const string CuNumberColumn = "cu_number";
    private const string CuidColumn = "cuid";
    private const string SubmissionIdColumn = "submission_id";
    private const string VersionColumn = "version";
    private const string ObservedAtColumn = "observed_at";
    private const string ObservedAtSourceColumn = "observed_at_source";

    // A text value of at most this many characters is pooled: a state, a type or a code repeats on thousands of rows.
    // A longer one, a name or an address, is nearly always its own.
    private const int PooledLength = 12;

    // Every column that leads the file, and the type the file must hold it as.
    private static readonly (string Name, Type Type)[] Leading =
    [
        (QuarterColumn, typeof(ReadOnlyMemory<char>)),
        (CycleDateColumn, typeof(DateOnly)),
        (CuNumberColumn, typeof(int)),
        (CuidColumn, typeof(int)),
        (SubmissionIdColumn, typeof(long)),
        (VersionColumn, typeof(short)),
        (ObservedAtColumn, typeof(DateTime)),
        (ObservedAtSourceColumn, typeof(ReadOnlyMemory<char>)),
    ];

    private static readonly ProfileRows Every = new(null, null);

    // Each set is null where the filter does not restrict its column.
    private readonly FrozenSet<int>? _cuNumbers;
    private readonly FrozenSet<int>? _cuids;

    private ProfileRows(FrozenSet<int>? cuNumbers, FrozenSet<int>? cuids)
    {
        _cuNumbers = cuNumbers;
        _cuids = cuids;
    }

    /// <summary>Whether a set of the filter is empty, so that no row is selected.</summary>
    public bool SelectsNone => _cuNumbers is { Count: 0 } || _cuids is { Count: 0 };

    private bool Filters => _cuNumbers is not null || _cuids is not null;

    /// <summary>The rows <paramref name="filter"/> selects, every row when it is null.</summary>
    /// <param name="filter">The filter, copied here.</param>
    /// <param name="paramName">The parameter that held the filter, for the exception.</param>
    /// <exception cref="ArgumentException">A charter number or cuid is less than 1.</exception>
    public static ProfileRows Select(ProfileFilter? filter, string paramName)
    {
        if (filter is null)
        {
            return Every;
        }

        return new ProfileRows(
            ReadRules.FreezeIdentifiers(filter.CuNumbers, nameof(ProfileFilter.CuNumbers), paramName),
            ReadRules.FreezeIdentifiers(filter.Cuids, nameof(ProfileFilter.Cuids), paramName));
    }

    /// <summary>
    /// Reads the selected rows of <paramref name="table"/>, row group by row group, in the file's order. The filter's
    /// columns are read first, and a row group in which no row matches reads no other.
    /// </summary>
    /// <exception cref="UnsupportedSchemaException">
    /// The file lacks a leading column, or holds one as another type, or holds a regulator's column that is neither
    /// text nor a 32-bit integer, or a null where a record requires a value, or a <c>quarter</c> that is not a
    /// quarter's label, or cannot be read. The columns are checked before the first row.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async IAsyncEnumerable<FilingProfile> ReadAsync(
        ParquetTable table, [EnumeratorCancellation] CancellationToken ct)
    {
        // Every column is checked before any is read, so a file without one is refused whatever the filter.
        var columns = ProfileColumns.Of(table, Leading);
        var group = new Group(this, columns);
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
    private bool Selects(ReadOnlySpan<int?> cuNumbers, ReadOnlySpan<int?> cuids, int row)
    {
        if (_cuNumbers is not null && !_cuNumbers.Contains(Required(cuNumbers[row], CuNumberColumn)))
        {
            return false;
        }

        // A null cuid is in no set.
        return _cuids is null || (cuids[row] is { } cuid && _cuids.Contains(cuid));
    }

    // One stream's reading: the open row group's columns, and the strings and quarters met so far. Each column is
    // memory the table lends until its next row group.
    private sealed class Group(ProfileRows filter, ProfileColumns columns)
    {
        private readonly StringPool _strings = new();
        private readonly QuarterLabels _quarters = new(TableName);
        private readonly ReadOnlyMemory<ReadOnlyMemory<char>?>[] _texts =
            new ReadOnlyMemory<ReadOnlyMemory<char>?>[columns.TextNames.Length];
        private readonly ReadOnlyMemory<int?>[] _numbers = new ReadOnlyMemory<int?>[columns.NumberNames.Length];
        private ReadOnlyMemory<ReadOnlyMemory<char>?> _quarter;
        private ReadOnlyMemory<DateOnly?> _cycleDate;
        private ReadOnlyMemory<int?> _cuNumber;
        private ReadOnlyMemory<int?> _cuid;
        private ReadOnlyMemory<long?> _submissionId;
        private ReadOnlyMemory<short?> _version;
        private ReadOnlyMemory<DateTime?> _observedAt;
        private ReadOnlyMemory<ReadOnlyMemory<char>?> _observedAtSource;

        // Reads the columns the filter matches on.
        public async Task ReadFilterAsync(ParquetTable table, CancellationToken ct)
        {
            if (filter._cuNumbers is not null)
            {
                _cuNumber = await table.ReadAsync<int>(CuNumberColumn, ct).ConfigureAwait(false);
            }

            if (filter._cuids is not null)
            {
                _cuid = await table.ReadAsync<int>(CuidColumn, ct).ConfigureAwait(false);
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

            if (filter._cuids is null)
            {
                _cuid = await table.ReadAsync<int>(CuidColumn, ct).ConfigureAwait(false);
            }

            _submissionId = await table.ReadAsync<long>(SubmissionIdColumn, ct).ConfigureAwait(false);
            _version = await table.ReadAsync<short>(VersionColumn, ct).ConfigureAwait(false);
            _observedAt = await table.ReadAsync<DateTime>(ObservedAtColumn, ct).ConfigureAwait(false);
            _observedAtSource = await table.ReadTextAsync(ObservedAtSourceColumn, ct).ConfigureAwait(false);
            for (var slot = 0; slot < _texts.Length; slot++)
            {
                _texts[slot] = await table.ReadTextAsync(columns.TextNames[slot], ct).ConfigureAwait(false);
            }

            for (var slot = 0; slot < _numbers.Length; slot++)
            {
                _numbers[slot] = await table.ReadAsync<int>(columns.NumberNames[slot], ct).ConfigureAwait(false);
            }
        }

        // The first row from row on that the filter selects, or rows when none does.
        public int NextMatch(int row, int rows)
        {
            if (!filter.Filters)
            {
                return row;
            }

            var cuNumbers = _cuNumber.Span;
            var cuids = _cuid.Span;
            for (; row < rows; row++)
            {
                if (filter.Selects(cuNumbers, cuids, row))
                {
                    return row;
                }
            }

            return rows;
        }

        public FilingProfile Build(int row)
        {
            var texts = new string?[_texts.Length];
            for (var slot = 0; slot < texts.Length; slot++)
            {
                texts[slot] = _texts[slot].Span[row] is { } text ? Text(text.Span) : null;
            }

            var numbers = new int?[_numbers.Length];
            for (var slot = 0; slot < numbers.Length; slot++)
            {
                numbers[slot] = _numbers[slot].Span[row];
            }

            var leading = new FilingProfile.Leading(
                _quarters.Parse(Required(_quarter.Span[row], QuarterColumn).Span),
                Required(_cycleDate.Span[row], CycleDateColumn),
                Required(_cuNumber.Span[row], CuNumberColumn),
                _cuid.Span[row],
                Required(_submissionId.Span[row], SubmissionIdColumn),
                Required(_version.Span[row], VersionColumn),
                // The file holds UTC times, which Parquet.Net gives as of no zone, or of the local one. The time is
                // kept and marked UTC: converting it from local time would move it.
                new DateTimeOffset(
                    DateTime.SpecifyKind(Required(_observedAt.Span[row], ObservedAtColumn), DateTimeKind.Utc)),
                _strings.Get(Required(_observedAtSource.Span[row], ObservedAtSourceColumn).Span));
            return new FilingProfile(leading, columns, texts, numbers);
        }

        // A string of the characters: shared when short, as a code or a state repeats, and otherwise its own.
        private string Text(ReadOnlySpan<char> value)
        {
            return value.Length <= PooledLength ? _strings.Get(value) : value.ToString();
        }
    }
}
