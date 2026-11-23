using System.Globalization;
using System.Text.Json;

namespace QuarterCall.Internal;

/// <summary>
/// The rule for a published <c>schema_version</c>, <c>major.minor</c>: a major this version of the package does not
/// read is refused, and any minor of a major it reads is read on.
/// </summary>
internal static class SchemaVersion
{
    private const char Separator = '.';

    /// <summary>Throws unless <paramref name="published"/> is <paramref name="major"/>, a dot and a minor.</summary>
    /// <param name="published">The document's <c>schema_version</c>, or null when it has none.</param>
    /// <param name="major">The major version this package reads.</param>
    /// <param name="document">What the document is, for the message, such as "the manifest at …".</param>
    /// <exception cref="UnsupportedSchemaException">The version is not one of <paramref name="major"/>.</exception>
    public static void Require(string? published, int major, string document)
    {
        if (IsOfMajor(published, major))
        {
            return;
        }

        var found = published is null
            ? "does not say which version it is"
            : $"is version \"{published}\"";
        throw Refused(major, document, found);
    }

    /// <summary>
    /// Throws unless <paramref name="published"/> is a JSON string holding <paramref name="major"/>, a dot and a
    /// minor. A version that is not a string, a number included, is refused as a version.
    /// </summary>
    /// <param name="published">The document's <c>schema_version</c>; undefined when the key is absent.</param>
    /// <param name="major">The major version this package reads.</param>
    /// <param name="document">What the document is, for the message, such as "the manifest at …".</param>
    /// <exception cref="UnsupportedSchemaException">The version is not one of <paramref name="major"/>.</exception>
    public static void Require(JsonElement published, int major, string document)
    {
        switch (published.ValueKind)
        {
            case JsonValueKind.String:
                Require(published.GetString(), major, document);
                return;
            case JsonValueKind.Undefined or JsonValueKind.Null:
                Require((string?)null, major, document);
                return;
            default:
                throw Refused(major, document, "does not give its version as text, such as \"1.2\"");
        }
    }

    private static UnsupportedSchemaException Refused(int major, string document, string found)
    {
        return new UnsupportedSchemaException(
            $"This version of QuarterCall reads version {major} of {document}, and the one published {found}. "
            + ReadRules.Remedy);
    }

    private static bool IsOfMajor(string? published, int major)
    {
        if (published is null)
        {
            return false;
        }

        var dot = published.IndexOf(Separator, StringComparison.Ordinal);
        if (dot < 0)
        {
            return false;
        }

        var minor = published.AsSpan(dot + 1);
        if (minor.IsEmpty || minor.ContainsAnyExceptInRange('0', '9'))
        {
            return false;
        }

        // NumberStyles.None takes ASCII digits only: no sign, no space.
        return int.TryParse(published.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var found)
            && found == major;
    }
}
