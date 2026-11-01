namespace DotnetTestRunnerMcp.Models;

public enum TestOutcome
{
    Passed,
    Partial,
    Failed,
    Skipped,
    NotFound,
    Ambiguous,
    Error,
}
