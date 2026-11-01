namespace DotnetTestRunnerMcp.Models;

public sealed record TestFailure(string TestName, string? Message, string? StackTrace);
