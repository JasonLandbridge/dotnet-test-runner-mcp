namespace DotnetTestRunnerMcp.Models;

public sealed record TruncatedText(
    string? Text,
    bool Truncated,
    int? OriginalCharCount,
    int? OriginalLineCount
);
