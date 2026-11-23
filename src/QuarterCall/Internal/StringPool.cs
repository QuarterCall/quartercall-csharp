namespace QuarterCall.Internal;

/// <summary>
/// One string instance for each distinct value, so a value repeated on millions of rows is held once. One pool
/// serves one stream: it is not safe for concurrent use, and it lives no longer than its stream.
/// </summary>
internal sealed class StringPool
{
    private readonly HashSet<string> _strings = new(StringComparer.Ordinal);
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _spans;

    /// <summary>Creates an empty pool.</summary>
    public StringPool()
    {
        _spans = _strings.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    /// <summary>The pool's instance of <paramref name="value"/>, which becomes it if the pool has none.</summary>
    public string Get(string value)
    {
        if (_strings.TryGetValue(value, out var pooled))
        {
            return pooled;
        }

        _strings.Add(value);
        return value;
    }

    /// <summary>
    /// The pool's instance of the characters <paramref name="value"/> holds; a string is made of them only when the
    /// pool has none.
    /// </summary>
    public string Get(ReadOnlySpan<char> value)
    {
        if (_spans.TryGetValue(value, out var pooled))
        {
            return pooled;
        }

        pooled = value.ToString();
        _strings.Add(pooled);
        return pooled;
    }
}
