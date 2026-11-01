using System.ComponentModel;

namespace DotnetTest.Mcp.Models;

[Description("Per-project discovery result.")]
public sealed record ProjectDiscoveryResult(
    [property: Description("Project path for this discovery run.")] string ProjectPath,
    [property: Description("Outcome for this project discovery.")] TestOutcome Outcome,
    [property: Description("Short summary for this project discovery.")] string Message,
    [property: Description("Process exit code from dotnet test for this project.")] int ExitCode,
    [property: Description("Discovered test count for this project after filtering.")]
    int TestCount,
    [property: Description("Optional warning message when Outcome is Passed.")] string? Warning,
    [property: Description("Structured error details when Outcome is Error; otherwise null.")]
    ErrorInfo? Error);