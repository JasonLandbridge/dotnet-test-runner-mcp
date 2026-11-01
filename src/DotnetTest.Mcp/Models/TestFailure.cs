namespace DotnetTest.Mcp.Models;

public sealed record TestFailure(
    string TestName,
    string? Message,
    string? StackTrace);
