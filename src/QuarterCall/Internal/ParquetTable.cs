using System.Buffers;
using System.Runtime.CompilerServices;
using Parquet;
using Parquet.Schema;
using static System.FormattableString;

namespace QuarterCall.Internal;

/// <summary>
/// A verified Parquet file, read one row group at a time. Each column is read into a buffer rented once, at the
/// largest row group's size, and kept until the table is disposed, which returns it. A table serves one stream and
/// is not safe for concurrent use.
/// </summary>
/// <remarks>
/// Its columns are the file's top-level columns of one value a row; a nested or repeated column is not one of them.
/// Each column is read in the form its declaration calls for: a column the file declares optional through
/// Parquet.Net's nullable form, and one it declares required through its plain form. Any failure of Parquet.Net but a
/// cancellation is an <see cref="UnsupportedSchemaException"/>
/// with Parquet.Net's exception inside: the file matched the manifest, so a file that cannot be read is in a form
/// this version of the package does not read.
/// </remarks>
internal sealed class ParquetTable : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly ParquetReader _reader;
    private readonly Dictionary<string, DataField> _fields;
    private readonly int[] _groupRows;
    private readonly int _largestGroup;
    private readonly Dictionary<string, ColumnBuffer> _buffers = new(StringComparer.Ordinal);
    private ParquetRowGroupReader? _group;
    private int _rows;
    private bool _disposed;

    private ParquetTable(
        FileStream stream, ParquetReader reader, Dictionary<string, DataField> fields, string[] names, int[] groupRows)
    {
        _stream = stream;
        _reader = reader;
        _fields = fields;
        ColumnNames = names.AsReadOnly();
        _groupRows = groupRows;
        _largestGroup = groupRows.Length == 0 ? 0 : groupRows.Max();
    }

    /// <summary>The number of row groups in the file.</summary>
    public int GroupCount => _groupRows.Length;

    /// <summary>The names of the table's columns, in the file's order.</summary>
    public IReadOnlyList<string> ColumnNames { get; }

    /// <summary>
    /// How many columns the table has read, counted across its row groups: what a test counts to see that a row
    /// group was passed over.
    /// </summary>
    public int ColumnReads { get; private set; }

    /// <summary>
    /// Opens <paramref name="file"/> to read. The table closes it when disposed, and leaves it on disk.
    /// </summary>
    /// <exception cref="UnsupportedSchemaException">
    /// The file cannot be read as Parquet, or has a row group of more rows than an array holds.
    /// </exception>
    /// <exception cref="IOException">The file could not be opened.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static async Task<ParquetTable> OpenAsync(VerifiedFile file, CancellationToken ct)
    {
        var stream = file.OpenRead();
        ParquetReader? reader = null;
        try
        {
            reader = await CreateReaderAsync(stream, ct).ConfigureAwait(false);
            var (fields, names) = Columns(reader);
            return new ParquetTable(stream, reader, fields, names, GroupRows(reader));
        }
        catch
        {
            await CloseAsync(reader, null, stream).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The type of the values in <paramref name="column"/>: <see cref="ReadOnlyMemory{T}"/> of char for text.
    /// </summary>
    /// <exception cref="UnsupportedSchemaException">The file has no such column.</exception>
    public Type ColumnType(string column)
    {
        return Field(column).ClrType;
    }

    /// <summary>
    /// Throws unless the file has <paramref name="column"/>, holding values of <paramref name="type"/>.
    /// </summary>
    /// <exception cref="UnsupportedSchemaException">The file has no such column, or it holds another type.</exception>
    public void Require(string column, Type type)
    {
        _ = Field(column, type);
    }

    /// <summary>
    /// Opens the row group at <paramref name="index"/> to read, and gives its row count. The memory each read of the
    /// group before it returned is not to be read again.
    /// </summary>
    /// <exception cref="UnsupportedSchemaException">The row group cannot be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public Task<int> OpenGroupAsync(int index, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, GroupCount);
        ct.ThrowIfCancellationRequested();
        var previous = _group;
        _group = null;
        _rows = 0;
        try
        {
            previous?.Dispose();
            _group = _reader.OpenRowGroupReader(index);
        }
        catch (Exception e) when (IsParquetFailure(e))
        {
            throw Unreadable(e);
        }

        _rows = _groupRows[index];
        return Task.FromResult(_rows);
    }

    /// <summary>
    /// Reads <paramref name="column"/> of the open row group, a value a row and null where the file has none. The
    /// memory is the table's: it holds until the next <see cref="OpenGroupAsync"/> or the dispose.
    /// </summary>
    /// <exception cref="UnsupportedSchemaException">
    /// The file has no such column, or it holds values of another type than <typeparamref name="T"/>, or it cannot
    /// be read.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task<ReadOnlyMemory<T?>> ReadAsync<T>(string column, CancellationToken ct)
        where T : struct
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var group = _group ?? throw new InvalidOperationException("No row group is open: call OpenGroupAsync first.");
        var field = Field(column, typeof(T));
        ColumnReads++;
        if (_rows == 0)
        {
            return ReadOnlyMemory<T?>.Empty;
        }

        var values = Buffer<T>(column).AsMemory(0, _rows);
        try
        {
            if (field.IsNullable)
            {
                await group.ReadAsync<T>(field, values, cancellationToken: ct).ConfigureAwait(false);
            }
            else
            {
                await ReadRequiredAsync(group, field, values, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (IsParquetFailure(e))
        {
            throw Unreadable(e);
        }

        return values;
    }

    /// <summary>
    /// Reads the text column <paramref name="column"/> of the open row group, as <see cref="ReadAsync{T}"/> does.
    /// Each value is a slice of characters Parquet.Net decoded, shared by the rows that repeat it, so reading the
    /// column makes no string for each row.
    /// </summary>
    /// <exception cref="UnsupportedSchemaException">
    /// The file has no such column, or it does not hold text, or it cannot be read.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public Task<ReadOnlyMemory<ReadOnlyMemory<char>?>> ReadTextAsync(string column, CancellationToken ct)
    {
        return ReadAsync<ReadOnlyMemory<char>>(column, ct);
    }

    /// <summary>
    /// Returns the buffers, and closes the reader and the file. A second call does nothing, so no buffer is returned
    /// twice.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var buffer in _buffers.Values)
        {
            buffer.Return();
        }

        _buffers.Clear();
        var group = _group;
        _group = null;
        await CloseAsync(_reader, group, _stream).ConfigureAwait(false);
    }

    private static async Task<ParquetReader> CreateReaderAsync(FileStream stream, CancellationToken ct)
    {
        // A DATE column reads as DateOnly, as the published dates are typed.
        var options = new ParquetOptions { UseDateOnlyTypeForDates = true };
        try
        {
            // The table owns the stream, and closes it after the reader.
            return await ParquetReader.CreateAsync(stream, options, leaveStreamOpen: true, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsParquetFailure(e))
        {
            throw Unreadable(e);
        }
    }

    // Reads a column the file declares required through Parquet.Net's plain form, into an array rented for the read,
    // and copies it into values. Parquet.Net's nullable form must not read such a column: it takes each row's
    // definition level from an array it rents and does not clear, and a required column has no levels to write there,
    // so a row whose slot holds a negative number an earlier user left is read as null, and every value after it moves
    // up a row. A process's first reads find clean arrays; later ones need not.
    private static async Task ReadRequiredAsync<T>(
        ParquetRowGroupReader group, DataField field, Memory<T?> values, CancellationToken ct)
        where T : struct
    {
        var rows = values.Length;
        var plain = ArrayPool<T>.Shared.Rent(rows);
        try
        {
            await group.ReadAsync<T>(field, plain.AsMemory(0, rows), cancellationToken: ct).ConfigureAwait(false);
            Lift(plain.AsSpan(0, rows), values.Span);
        }
        finally
        {
            // An array of references is cleared, so that the pool keeps none of the file's values alive.
            ArrayPool<T>.Shared.Return(plain, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }

    // Copies a required column's values into the nullable form the table lends every column in.
    private static void Lift<T>(ReadOnlySpan<T> from, Span<T?> to)
        where T : struct
    {
        for (var row = 0; row < from.Length; row++)
        {
            to[row] = from[row];
        }
    }

    // The file's columns of one value a row, by name, and their names in the file's order.
    private static (Dictionary<string, DataField> Fields, string[] Names) Columns(ParquetReader reader)
    {
        var fields = new Dictionary<string, DataField>(StringComparer.Ordinal);
        var names = new List<string>();
        try
        {
            foreach (var field in reader.Schema.Fields)
            {
                if (field is DataField { IsArray: false } data && fields.TryAdd(data.Name, data))
                {
                    names.Add(data.Name);
                }
            }
        }
        catch (Exception e) when (IsParquetFailure(e))
        {
            throw Unreadable(e);
        }

        return (fields, [.. names]);
    }

    // Each row group's row count, which must fit an array.
    private static int[] GroupRows(ParquetReader reader)
    {
        long[] counts;
        try
        {
            counts = new long[reader.RowGroupCount];
            for (var index = 0; index < counts.Length; index++)
            {
                using var group = reader.OpenRowGroupReader(index);
                counts[index] = group.RowCount;
            }
        }
        catch (Exception e) when (IsParquetFailure(e))
        {
            throw Unreadable(e);
        }

        var rows = new int[counts.Length];
        for (var index = 0; index < counts.Length; index++)
        {
            if (counts[index] is < 0 or > int.MaxValue)
            {
                throw new UnsupportedSchemaException(
                    Invariant($"The published file has a row group of {counts[index]} rows, ")
                    + $"which this version of QuarterCall cannot hold at once. {ReadRules.Remedy}");
            }

            rows[index] = (int)counts[index];
        }

        return rows;
    }

    // Closes the row group, then the reader, then the file, which is closed whatever the others do.
    private static async ValueTask CloseAsync(ParquetReader? reader, ParquetRowGroupReader? group, FileStream stream)
    {
        try
        {
            group?.Dispose();
            if (reader is not null)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e) when (IsParquetFailure(e))
        {
            throw Unreadable(e);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Every exception Parquet.Net throws is a file this version cannot read, but a cancellation, which is the caller's.
    private static bool IsParquetFailure(Exception e)
    {
        return e is not OperationCanceledException;
    }

    private static UnsupportedSchemaException Unreadable(Exception inner)
    {
        return new UnsupportedSchemaException(
            "The published file matched the manifest but could not be read as Parquet, so it may be of a newer form "
            + $"than this version of QuarterCall reads. {ReadRules.Remedy}",
            inner);
    }

    // How a message names a column's type: text for the characters a text column reads as.
    private static string Describe(Type type)
    {
        return type == typeof(ReadOnlyMemory<char>) ? "text" : type.Name;
    }

    private DataField Field(string column)
    {
        if (_fields.TryGetValue(column, out var field))
        {
            return field;
        }

        throw new UnsupportedSchemaException(
            $"The published file has no column \"{column}\" of one value a row, which this version of QuarterCall "
            + $"reads. The file may be of a newer form. {ReadRules.Remedy}");
    }

    private DataField Field(string column, Type type)
    {
        var field = Field(column);
        if (field.ClrType == type)
        {
            return field;
        }

        throw new UnsupportedSchemaException(
            $"The published file's column \"{column}\" holds {Describe(field.ClrType)}, where this version of "
            + $"QuarterCall reads {Describe(type)}. The file may be of a newer form. {ReadRules.Remedy}");
    }

    // The column's buffer, rented at its first read at the largest row group's size.
    private T?[] Buffer<T>(string column)
        where T : struct
    {
        if (_buffers.TryGetValue(column, out var buffer))
        {
            return ((ColumnBuffer<T?>)buffer).Values;
        }

        var rented = new ColumnBuffer<T?>(_largestGroup);
        _buffers.Add(column, rented);
        return rented.Values;
    }

    // A buffer rented for one column, returned once, when the table is disposed.
    private abstract class ColumnBuffer
    {
        public abstract void Return();
    }

    private sealed class ColumnBuffer<T>(int length) : ColumnBuffer
    {
        public T[] Values { get; } = ArrayPool<T>.Shared.Rent(length);

        // A buffer of references is cleared, so that the pool keeps none of the file's values alive.
        public override void Return()
        {
            ArrayPool<T>.Shared.Return(Values, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }
}
