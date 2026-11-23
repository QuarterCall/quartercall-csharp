using Xunit;

namespace QuarterCall.Tests.Support;

/// <summary>
/// A fact that reads the live host. It is reported as skipped unless the environment variable
/// <c>QUARTERCALL_LIVE</c> is <c>1</c>, so the default run never touches the network.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    /// <summary>The environment variable that switches the live facts on.</summary>
    public const string Variable = "QUARTERCALL_LIVE";

    /// <summary>Skips the fact unless the variable is <c>1</c>.</summary>
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"Reads the live host. Set {Variable}=1 to run it.";
        }
    }
}
