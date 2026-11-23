using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Serialization;

namespace QuarterCall;

/// <summary>A reporting period, a calendar quarter such as <c>2026Q3</c>.</summary>
[JsonConverter(typeof(QuarterJsonConverter))]
public readonly record struct Quarter : IComparable<Quarter>, ISpanParsable<Quarter>, ISpanFormattable
{
    private const int MinYear = 1;
    private const int MaxYear = 9999;
    private const int QuartersPerYear = 4;
    private const int LabelLength = 6;
    private const char QuarterMark = 'Q';

    // Quarters since the start of year 0. Zero is the first quarter of year 0, which no valid quarter has,
    // so default(Quarter) is never equal to a valid one.
    private readonly int _index;

    /// <summary>Creates a quarter from its year (1 to 9999) and its number in the year (1 to 4).</summary>
    /// <param name="year">The calendar year, 1 to 9999.</param>
    /// <param name="number">The quarter of the year, 1 to 4.</param>
    /// <exception cref="ArgumentOutOfRangeException">The year or the number is out of range.</exception>
    public Quarter(int year, int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(year, MinYear);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(year, MaxYear);
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, QuartersPerYear);
        _index = (year * QuartersPerYear) + (number - 1);
    }

    /// <summary>The calendar year of the quarter.</summary>
    public int Year => _index / QuartersPerYear;

    /// <summary>The quarter's number in its year, 1 to 4.</summary>
    public int Number => (_index % QuartersPerYear) + 1;

    /// <summary>The quarter's last day, the date its Call Reports are as of.</summary>
    /// <exception cref="InvalidOperationException">This is the default quarter, which names no period.</exception>
    public DateOnly CycleDate
    {
        get
        {
            if (_index < MinYear * QuartersPerYear)
            {
                throw new InvalidOperationException("The default Quarter names no quarter.");
            }

            var month = Number * 3;
            return new DateOnly(Year, month, DateTime.DaysInMonth(Year, month));
        }
    }

    /// <summary>Parses a label such as <c>2026Q3</c>.</summary>
    /// <param name="s">The label: four ASCII digits, <c>Q</c>, and <c>1</c> to <c>4</c>.</param>
    /// <returns>The quarter the label names.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="s"/> is null.</exception>
    /// <exception cref="FormatException"><paramref name="s"/> is not a label.</exception>
    public static Quarter Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan());
    }

    /// <summary>Parses a label such as <c>2026Q3</c>, returning false when the text is not one.</summary>
    /// <param name="s">The label to parse.</param>
    /// <param name="result">The quarter, or the default quarter when this returns false.</param>
    /// <returns>True when <paramref name="s"/> is a label.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, out Quarter result)
    {
        if (s is null)
        {
            result = default;
            return false;
        }

        return TryParse(s.AsSpan(), out result);
    }

    /// <summary>Parses a label; the provider is ignored.</summary>
    /// <param name="s">The label to parse.</param>
    /// <param name="provider">Ignored.</param>
    /// <returns>The quarter the label names.</returns>
    public static Quarter Parse(string s, IFormatProvider? provider)
    {
        return Parse(s);
    }

    /// <summary>Parses a label, returning false when the text is not one; the provider is ignored.</summary>
    /// <param name="s">The label to parse.</param>
    /// <param name="provider">Ignored.</param>
    /// <param name="result">The quarter, or the default quarter when this returns false.</param>
    /// <returns>True when <paramref name="s"/> is a label.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out Quarter result)
    {
        return TryParse(s, out result);
    }

    /// <summary>Parses a label held in a span; the provider is ignored.</summary>
    /// <param name="s">The label to parse.</param>
    /// <param name="provider">Ignored.</param>
    /// <returns>The quarter the label names.</returns>
    public static Quarter Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
    {
        return Parse(s);
    }

    /// <summary>Parses a label held in a span, returning false when it is not one; the provider is ignored.</summary>
    /// <param name="s">The label to parse.</param>
    /// <param name="provider">Ignored.</param>
    /// <param name="result">The quarter, or the default quarter when this returns false.</param>
    /// <returns>True when <paramref name="s"/> is a label.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out Quarter result)
    {
        return TryParse(s, out result);
    }

    /// <summary>Compares two quarters by time.</summary>
    /// <param name="other">The quarter to compare with.</param>
    /// <returns>A negative number when this quarter is earlier, zero when equal, positive when later.</returns>
    public int CompareTo(Quarter other)
    {
        return _index.CompareTo(other._index);
    }

    /// <summary>Tells whether one quarter is earlier than another.</summary>
    /// <param name="left">The first quarter.</param>
    /// <param name="right">The second quarter.</param>
    /// <returns>True when <paramref name="left"/> is earlier.</returns>
    public static bool operator <(Quarter left, Quarter right) => left._index < right._index;

    /// <summary>Tells whether one quarter is earlier than or the same as another.</summary>
    /// <param name="left">The first quarter.</param>
    /// <param name="right">The second quarter.</param>
    /// <returns>True when <paramref name="left"/> is not later.</returns>
    public static bool operator <=(Quarter left, Quarter right) => left._index <= right._index;

    /// <summary>Tells whether one quarter is later than another.</summary>
    /// <param name="left">The first quarter.</param>
    /// <param name="right">The second quarter.</param>
    /// <returns>True when <paramref name="left"/> is later.</returns>
    public static bool operator >(Quarter left, Quarter right) => left._index > right._index;

    /// <summary>Tells whether one quarter is later than or the same as another.</summary>
    /// <param name="left">The first quarter.</param>
    /// <param name="right">The second quarter.</param>
    /// <returns>True when <paramref name="left"/> is not earlier.</returns>
    public static bool operator >=(Quarter left, Quarter right) => left._index >= right._index;

    /// <summary>The quarter's label, such as <c>2026Q3</c>.</summary>
    /// <returns>The label.</returns>
    public override string ToString()
    {
        return string.Create(LabelLength, this, static (span, q) => q.WriteLabel(span));
    }

    /// <summary>The quarter's label; the format and the provider are ignored.</summary>
    /// <param name="format">Ignored.</param>
    /// <param name="formatProvider">Ignored.</param>
    /// <returns>The label.</returns>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        return ToString();
    }

    /// <summary>Writes the label into a span; the format and the provider are ignored.</summary>
    /// <param name="destination">Where to write the six characters of the label.</param>
    /// <param name="charsWritten">Six when this returns true, otherwise zero.</param>
    /// <param name="format">Ignored.</param>
    /// <param name="provider">Ignored.</param>
    /// <returns>True when <paramref name="destination"/> held the label.</returns>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (destination.Length < LabelLength)
        {
            charsWritten = 0;
            return false;
        }

        WriteLabel(destination);
        charsWritten = LabelLength;
        return true;
    }

    /// <summary>Throws when this is <c>default(Quarter)</c>, which names no period.</summary>
    /// <param name="paramName">The name of the parameter that held this quarter.</param>
    /// <exception cref="ArgumentException">This is the default quarter.</exception>
    internal void ThrowIfDefault(string paramName)
    {
        if (_index < MinYear * QuartersPerYear)
        {
            throw new ArgumentException("The default Quarter names no period.", paramName);
        }
    }

    // Writes the six label characters; the span is at least that long.
    private void WriteLabel(Span<char> destination)
    {
        var year = Year;
        destination[0] = (char)('0' + (year / 1000));
        destination[1] = (char)('0' + (year / 100 % 10));
        destination[2] = (char)('0' + (year / 10 % 10));
        destination[3] = (char)('0' + (year % 10));
        destination[4] = QuarterMark;
        destination[5] = (char)('0' + Number);
    }

    private static Quarter Parse(ReadOnlySpan<char> s)
    {
        if (TryParse(s, out var result))
        {
            return result;
        }

        throw new FormatException("A quarter is four digits, Q and 1 to 4, such as 2026Q3.");
    }

    private static bool TryParse(ReadOnlySpan<char> s, out Quarter result)
    {
        result = default;
        if (s.Length != LabelLength || s[4] != QuarterMark)
        {
            return false;
        }

        var year = 0;
        for (var i = 0; i < 4; i++)
        {
            if (s[i] is < '0' or > '9')
            {
                return false;
            }

            year = (year * 10) + (s[i] - '0');
        }

        var number = s[5] - '0';
        if (year < MinYear || number < 1 || number > QuartersPerYear)
        {
            return false;
        }

        result = new Quarter(year, number);
        return true;
    }
}
