namespace DotnetTestRunnerMcp.Terminal;

public interface ICommandRunner : IAsyncDisposable
{
    Task<CommandResult> RunAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default
    );
}

public sealed record CommandRequest(string FileName, params string[] Arguments)
{
    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    public TimeSpan? Timeout { get; init; }

    public bool ThrowOnNonZeroExitCode { get; init; } = true;

    public int? MaxOutputChars { get; init; }
}

public sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    string[] StandardOutputLines,
    string[] StandardErrorLines,
    TimeSpan Duration
)
{
    public string? FileName { get; init; }

    public string[] Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public bool StandardOutputTruncated { get; init; }

    public bool StandardErrorTruncated { get; init; }

    public int StandardOutputOriginalCharacterCount { get; init; }

    public int StandardErrorOriginalCharacterCount { get; init; }

    public string? ExceptionType { get; init; }

    public string? ExceptionMessage { get; init; }

    public string? ExceptionStackTrace { get; init; }
}
