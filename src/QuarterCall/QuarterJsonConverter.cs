using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuarterCall;

/// <summary>Reads and writes a <see cref="Quarter"/> as its label, also as a JSON property name.</summary>
public sealed class QuarterJsonConverter : JsonConverter<Quarter>
{
    /// <summary>Reads a quarter from a JSON string holding its label.</summary>
    /// <param name="reader">The reader, positioned on a string.</param>
    /// <param name="typeToConvert">The type being read.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The quarter.</returns>
    /// <exception cref="JsonException">The value is not a string holding a label.</exception>
    public override Quarter Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("A quarter is a JSON string such as \"2026Q3\".");
        }

        return ReadLabel(ref reader);
    }

    /// <summary>Writes a quarter as a JSON string holding its label.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The quarter.</param>
    /// <param name="options">The serializer options.</param>
    public override void Write(Utf8JsonWriter writer, Quarter value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(Label(value));
    }

    /// <summary>Reads a quarter from a JSON property name holding its label.</summary>
    /// <param name="reader">The reader, positioned on a property name.</param>
    /// <param name="typeToConvert">The type being read.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The quarter.</returns>
    /// <exception cref="JsonException">The name is not a label.</exception>
    public override Quarter ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return ReadLabel(ref reader);
    }

    /// <summary>Writes a quarter as a JSON property name holding its label.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The quarter.</param>
    /// <param name="options">The serializer options.</param>
    public override void WriteAsPropertyName(Utf8JsonWriter writer, Quarter value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(Label(value));
    }

    private static string Label(Quarter value)
    {
        value.ThrowIfDefault(nameof(value));
        return value.ToString();
    }

    private static Quarter ReadLabel(ref Utf8JsonReader reader)
    {
        // Copying at most one label's worth of characters keeps the read free of heap allocation.
        Span<char> chars = stackalloc char[8];
        if (reader.HasValueSequence || reader.ValueSpan.Length > chars.Length)
        {
            throw new JsonException("A quarter is a label such as \"2026Q3\".");
        }

        var length = reader.CopyString(chars);
        if (Quarter.TryParse(chars[..length], null, out var quarter))
        {
            return quarter;
        }

        throw new JsonException("A quarter is a label such as \"2026Q3\".");
    }
}
