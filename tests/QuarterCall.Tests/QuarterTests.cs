using Xunit;
using System.Globalization;
using System.Text.Json;

namespace QuarterCall.Tests;

public class QuarterTests
{
    private sealed record Holder(Quarter Quarter);

    [Fact]
    public void ParsesALabel()
    {
        var q = Quarter.Parse("2026Q3");

        Assert.Equal(2026, q.Year);
        Assert.Equal(3, q.Number);
        Assert.Equal(new DateOnly(2026, 9, 30), q.CycleDate);
        Assert.Equal("2026Q3", q.ToString());
        Assert.Equal(new DateOnly(2024, 3, 31), new Quarter(2024, 1).CycleDate);
        Assert.Equal(new DateOnly(2024, 6, 30), new Quarter(2024, 2).CycleDate);
        Assert.Equal(new DateOnly(2024, 12, 31), new Quarter(2024, 4).CycleDate);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10000, 1)]
    [InlineData(2026, 0)]
    [InlineData(2026, 5)]
    public void RefusesOutOfRangeParts(int year, int number)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Quarter(year, number));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026Q5")]
    [InlineData("2026Q0")]
    [InlineData("2026q3")]
    [InlineData(" 2026Q3")]
    [InlineData("2026Q3 ")]
    [InlineData("26Q3")]
    [InlineData("2026-Q3")]
    [InlineData("٢٠٢٦Q3")]
    [InlineData(null)]
    public void RefusesWhatIsNotALabel(string? text)
    {
        Assert.False(Quarter.TryParse(text, out _));
        Assert.False(Quarter.TryParse(text, null, out _));
        if (text is not null)
        {
            Assert.False(Quarter.TryParse(text.AsSpan(), null, out _));
            Assert.Throws<FormatException>(() => Quarter.Parse(text));
            Assert.Throws<FormatException>(() => Quarter.Parse(text, null));
            Assert.Throws<FormatException>(() => Quarter.Parse(text.AsSpan(), null));
        }
        else
        {
            Assert.Throws<ArgumentNullException>(() => Quarter.Parse(null!));
        }
    }

    [Fact]
    public void SpanOverloadsParseALabel()
    {
        Assert.True(Quarter.TryParse("2026Q3".AsSpan(), null, out var q));
        Assert.Equal(new Quarter(2026, 3), q);
        Assert.Equal(q, Quarter.Parse("2026Q3".AsSpan(), null));
        Assert.Equal(q, Quarter.Parse("2026Q3", null));
    }

    [Fact]
    public void OrdersByTime()
    {
        Assert.True(Quarter.Parse("2025Q4") < Quarter.Parse("2026Q1"));
        Assert.True(Quarter.Parse("2025Q4") <= Quarter.Parse("2025Q4"));
        Assert.True(Quarter.Parse("2026Q1") > Quarter.Parse("2025Q4"));
        Assert.True(Quarter.Parse("2026Q1") >= Quarter.Parse("2026Q1"));

        string[] labels = ["2024Q4", "2025Q1", "2025Q3", "2026Q2", "2026Q3"];
        var shuffled = new List<Quarter>
        {
            Quarter.Parse("2026Q2"), Quarter.Parse("2024Q4"), Quarter.Parse("2026Q3"),
            Quarter.Parse("2025Q1"), Quarter.Parse("2025Q3"),
        };
        shuffled.Sort();

        Assert.Equal(labels, shuffled.Select(q => q.ToString()));
    }

    [Fact]
    public void FormatsWithoutAString()
    {
        var q = new Quarter(2026, 3);
        Span<char> five = stackalloc char[5];
        Span<char> six = stackalloc char[6];

        Assert.Equal("2026Q3", $"{q}");
        Assert.False(q.TryFormat(five, out _, default, null));
        Assert.True(q.TryFormat(six, out var written, default, null));
        Assert.Equal(6, written);
        Assert.Equal("2026Q3", new string(six));

        var arabic = new CultureInfo("ar-SA");
        Assert.Equal("2026Q3", q.ToString("G", arabic));
        Assert.Equal("2026Q3", string.Create(arabic, $"{q}"));
        Assert.True(q.TryFormat(six, out written, default, arabic));
        Assert.Equal("2026Q3", new string(six[..written]));
    }

    [Fact]
    public void IsItsLabelInJson()
    {
        var json = JsonSerializer.Serialize(new Holder(new Quarter(2026, 3)));
        Assert.Equal("""{"Quarter":"2026Q3"}""", json);
        Assert.Equal(new Quarter(2026, 3), JsonSerializer.Deserialize<Holder>(json)!.Quarter);

        var map = new Dictionary<Quarter, int> { [new Quarter(2026, 2)] = 7, [new Quarter(2026, 3)] = 9 };
        var mapJson = JsonSerializer.Serialize(map);
        Assert.Equal("""{"2026Q2":7,"2026Q3":9}""", mapJson);
        Assert.Equal(map, JsonSerializer.Deserialize<Dictionary<Quarter, int>>(mapJson));

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Holder>("""{"Quarter":20263}"""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Holder>("""{"Quarter":"2026q3"}"""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Dictionary<Quarter, int>>("""{"2026q3":1}"""));
    }

    [Fact]
    public void DefaultIsNotAQuarter()
    {
        Assert.Throws<ArgumentException>(() => default(Quarter).ThrowIfDefault("quarter"));
        Quarter.Parse("2019Q1").ThrowIfDefault("quarter");
        Assert.NotNull(default(Quarter).ToString());
    }
}
