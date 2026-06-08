using Xunit;

namespace PooledMailKit.StressTests.Helpers;

[AttributeUsage(AttributeTargets.Method)]
internal sealed class ManualStressFactAttribute : FactAttribute
{
    private const string RunStressEnvironmentVariable = "MAILKIT_POOLING_RUN_STRESS";

    public ManualStressFactAttribute()
    {
        var isEnabled = string.Equals(
            Environment.GetEnvironmentVariable(RunStressEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

        if (!isEnabled)
        {
            Skip = $"Set {RunStressEnvironmentVariable}=1 to run manual stress/resource validation.";
        }
    }
}
