using System.Collections.Frozen;
using static System.FormattableString;

namespace QuarterCall.Internal;

/// <summary>
/// What every reader of a published table shares: the sentence that sends a reader to a newer package, the checking
/// and freezing of a filter's sets, and the refusal of a null where a record requires a value.
/// </summary>
internal static class ReadRules
{
    /// <summary>Ends every message that says a file is of a form this version of the package does not read.</summary>
    public const string Remedy = "To read it, upgrade the QuarterCall package.";

    /// <summary>
    /// A copy of <paramref name="codes"/> as a set, or null where it is null. The copy is checked, not the caller's
    /// collection, so a change made meanwhile cannot slip a bad code past the check.
    /// </summary>
    /// <param name="codes">The account codes of a filter.</param>
    /// <param name="paramName">The parameter that held the filter, for the exception.</param>
    /// <exception cref="ArgumentException">A code is null or empty.</exception>
    public static FrozenSet<string>? FreezeCodes(IReadOnlyCollection<string>? codes, string paramName)
    {
        if (codes is null)
        {
            return null;
        }

        string[] copy = [.. codes];
        foreach (var code in copy)
        {
            if (string.IsNullOrEmpty(code))
            {
                throw new ArgumentException(
                    "The filter's Codes hold a null or empty code; an account code has at least one character.",
                    paramName);
            }
        }

        return copy.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// A copy of <paramref name="identifiers"/> as a set, or null where it is null, checked as
    /// <see cref="FreezeCodes"/> checks.
    /// </summary>
    /// <param name="identifiers">The charter numbers or cuids of a filter.</param>
    /// <param name="set">The name of the filter's set, for the message.</param>
    /// <param name="paramName">The parameter that held the filter, for the exception.</param>
    /// <exception cref="ArgumentException">An identifier is less than 1.</exception>
    public static FrozenSet<int>? FreezeIdentifiers(
        IReadOnlyCollection<int>? identifiers, string set, string paramName)
    {
        if (identifiers is null)
        {
            return null;
        }

        var frozen = identifiers.ToFrozenSet();
        foreach (var identifier in frozen)
        {
            if (identifier < 1)
            {
                throw new ArgumentException(
                    Invariant($"The filter's {set} hold {identifier}; each is 1 or more."), paramName);
            }
        }

        return frozen;
    }

    /// <summary>The value of a column a record requires, which a null in the file leaves without one.</summary>
    /// <param name="value">The value read.</param>
    /// <param name="table">The file, as a message names it: "accounts", say.</param>
    /// <param name="column">The column.</param>
    /// <exception cref="UnsupportedSchemaException"><paramref name="value"/> is null.</exception>
    public static T Required<T>(T? value, string table, string column)
        where T : struct
    {
        return value ?? throw new UnsupportedSchemaException(
            $"The {table} file has a null in its \"{column}\" column, where this version of QuarterCall reads a "
            + $"value on every row. The file may be of a newer form. {Remedy}");
    }
}

/// <summary>
/// The quarters a stream's rows name, each label parsed once. One serves one stream: it is not safe for concurrent
/// use.
/// </summary>
internal sealed class QuarterLabels(string table)
{
    // Probed by a label's characters, so a label already met makes no string.
    private readonly Dictionary<string, Quarter>.AlternateLookup<ReadOnlySpan<char>> _quarters =
        new Dictionary<string, Quarter>(StringComparer.Ordinal).GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>The quarter <paramref name="label"/> names.</summary>
    /// <exception cref="UnsupportedSchemaException">The label is not a quarter's.</exception>
    public Quarter Parse(ReadOnlySpan<char> label)
    {
        if (_quarters.TryGetValue(label, out var quarter))
        {
            return quarter;
        }

        if (!Quarter.TryParse(label, null, out quarter))
        {
            throw new UnsupportedSchemaException(
                $"The {table} file's \"quarter\" column holds \"{label}\", which is not a quarter such as "
                + $"2026Q3. The file may be of a newer form. {ReadRules.Remedy}");
        }

        _quarters[label] = quarter;
        return quarter;
    }
}
