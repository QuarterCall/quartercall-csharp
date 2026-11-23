using QuarterCall.Internal;

namespace QuarterCall;

/// <summary>
/// One credit union's profile for one filing: a row of a quarter's profiles file, as
/// <see cref="DataClient.ReadProfilesAsync"/> reads it. The file's first eight columns are properties; the regulator's
/// own columns, about 179 of them, are read by name with <see cref="GetString"/> and <see cref="GetInt32"/>, or with
/// <see cref="GetValue"/> for either kind, so a text or integer column the regulator adds is readable without a new
/// version of the package. A caller cannot make one.
/// </summary>
public sealed class FilingProfile
{
    // The regulator's columns, in file order, and where each one's value is held. One for every row of a stream.
    private readonly ProfileColumns _columns;

    // This row's values: text columns in texts, integer columns in numbers, at the slots the index gives.
    private readonly string?[] _texts;
    private readonly int?[] _numbers;

    internal FilingProfile(Leading leading, ProfileColumns columns, string?[] texts, int?[] numbers)
    {
        Quarter = leading.Quarter;
        CycleDate = leading.CycleDate;
        CuNumber = leading.CuNumber;
        Cuid = leading.Cuid;
        SubmissionId = leading.SubmissionId;
        Version = leading.Version;
        ObservedAt = leading.ObservedAt;
        ObservedAtSource = leading.ObservedAtSource;
        _columns = columns;
        _texts = texts;
        _numbers = numbers;
    }

    // The eight columns the file leads with, which a row of a stream sets once.
    internal readonly record struct Leading(
        Quarter Quarter,
        DateOnly CycleDate,
        int CuNumber,
        int? Cuid,
        long SubmissionId,
        short Version,
        DateTimeOffset ObservedAt,
        string ObservedAtSource);

    /// <summary>The quarter the filing is for, <c>quarter</c>.</summary>
    public Quarter Quarter { get; }

    /// <summary>The quarter's last day, the date the filing is as of, <c>cycle_date</c>.</summary>
    public DateOnly CycleDate { get; }

    /// <summary>The charter number the filing was made under, <c>cu_number</c>.</summary>
    public int CuNumber { get; }

    /// <summary>
    /// The institution that filed, <c>cuid</c>, which follows it across charter conversions; null where the file
    /// gives none.
    /// </summary>
    public int? Cuid { get; }

    /// <summary>The filing the profile is from, <c>submission_id</c>.</summary>
    public long SubmissionId { get; }

    /// <summary>The version of the filing, <c>version</c>.</summary>
    public short Version { get; }

    /// <summary>When the host fetched the filing, in UTC, <c>observed_at</c>.</summary>
    public DateTimeOffset ObservedAt { get; }

    /// <summary>
    /// How <see cref="ObservedAt"/> was known, <c>observed_at_source</c>: <c>first_seen</c> or <c>archived</c> today.
    /// It is a string, and not an enum, because the set of sources is open.
    /// </summary>
    public string ObservedAtSource { get; }

    /// <summary>
    /// The names of the regulator's columns the file holds, in the file's order: the columns after the eight that are
    /// properties of this type. Text and integer columns are listed; a column of another type is left out until a
    /// version of the package reads it.
    /// </summary>
    public IReadOnlyList<string> Columns => _columns.Names;

    /// <summary>
    /// Reads one of the regulator's columns whatever it holds: for printing or copying every column of
    /// <see cref="Columns"/>. <see cref="GetString"/> and <see cref="GetInt32"/> are the typed readers.
    /// </summary>
    /// <param name="name">A name in <see cref="Columns"/>, matched exactly, case included.</param>
    /// <returns>
    /// A <see cref="string"/> for a text column, an <see cref="int"/> for an integer column; null where the file has
    /// none.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is not in <see cref="Columns"/>. One of the eight leading columns is a property.
    /// </exception>
    public object? GetValue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var column = _columns.Find(name);
        return column.IsText ? _texts[column.Slot] : _numbers[column.Slot];
    }

    /// <summary>Reads one of the regulator's text columns.</summary>
    /// <param name="name">A name in <see cref="Columns"/>, matched exactly, case included.</param>
    /// <returns>The value as filed; null where the file has none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is not in <see cref="Columns"/>. One of the eight leading columns is a property.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The column holds integers: read it with <see cref="GetInt32"/>.
    /// </exception>
    public string? GetString(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var column = _columns.Find(name);
        if (column.IsText)
        {
            return _texts[column.Slot];
        }

        throw new InvalidOperationException(
            $"The column \"{name}\" holds integers; read it with {nameof(GetInt32)}.");
    }

    /// <summary>Reads one of the regulator's integer columns.</summary>
    /// <param name="name">A name in <see cref="Columns"/>, matched exactly, case included.</param>
    /// <returns>The value; null where the file has none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is not in <see cref="Columns"/>. One of the eight leading columns is a property.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The column holds text: read it with <see cref="GetString"/>.
    /// </exception>
    public int? GetInt32(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var column = _columns.Find(name);
        if (!column.IsText)
        {
            return _numbers[column.Slot];
        }

        throw new InvalidOperationException(
            $"The column \"{name}\" holds text; read it with {nameof(GetString)}.");
    }
}
