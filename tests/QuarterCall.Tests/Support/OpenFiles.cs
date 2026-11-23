namespace QuarterCall.Tests.Support;

/// <summary>Which files this process holds open, for a test that a read releases what it opened.</summary>
internal static class OpenFiles
{
    /// <summary>
    /// The files beneath <paramref name="directory"/> that this process holds open, a deleted one included. Linux
    /// lists them in /proc/self/fd; elsewhere there is no such list, and this is empty.
    /// </summary>
    public static List<string> Beneath(string directory)
    {
        var open = new List<string>();
        if (!OperatingSystem.IsLinux())
        {
            return open;
        }

        var prefix = Path.TrimEndingDirectorySeparator(directory) + "/";
        foreach (var descriptor in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            string? target;
            try
            {
                target = new FileInfo(descriptor).LinkTarget;
            }
            catch (IOException)
            {
                // Closed since it was listed.
                continue;
            }

            if (target is not null && target.StartsWith(prefix, StringComparison.Ordinal))
            {
                open.Add(target);
            }
        }

        return open;
    }
}
