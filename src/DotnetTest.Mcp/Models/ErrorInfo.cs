namespace DotnetTest.Mcp.Models;

public sealed record ErrorInfo(
    ErrorKind Kind,
    string Reason,
    TruncatedText? Stdout,
    TruncatedText? Stderr)
{
    public string? ToolName { get; init; }

    public string? ProjectPath { get; init; }

    public string? Command { get; init; }

    public string[] Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public int? ExitCode { get; init; }

    public string? ExceptionType { get; init; }

    public string? ExceptionMessage { get; init; }

    public string? ExceptionStackTrace { get; init; }
}
