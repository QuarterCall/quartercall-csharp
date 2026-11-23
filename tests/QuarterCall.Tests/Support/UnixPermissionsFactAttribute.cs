using Xunit;

namespace QuarterCall.Tests.Support;

/// <summary>
/// A fact that needs Unix file modes to be enforced: one that makes a file unreadable, or a directory one in which
/// nothing can be deleted. It is reported as skipped on Windows, which has no such modes, and in a privileged
/// process, which they do not bind.
/// </summary>
public sealed class UnixPermissionsFactAttribute : FactAttribute
{
    /// <summary>Skips the fact where file modes would not hold.</summary>
    public UnixPermissionsFactAttribute()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            Skip = "Needs Unix file modes that bind the process: Windows has none, and root ignores them.";
        }
    }
}
