namespace QuarterCall.Tests.Support;

/// <summary>Locates the fixture files the test project copies beside its assembly.</summary>
internal static class Fixtures
{
    private const string FolderName = "Fixtures";

    /// <summary>The path of the fixture file <paramref name="name"/>.</summary>
    public static string Path(string name) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, FolderName, name);
}
