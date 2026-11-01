using DotnetTest.Mcp.Models;

namespace DotnetTest.Mcp.Tools;

internal static class FailureFormatting
{
    internal sealed record FailureOutputOptions(bool IncludeStackTrace);

    internal static FailureOutputOptions CreateOptions(bool includeStackTrace)
        => new(includeStackTrace);

    internal static TestFailure? BuildFailure(CtrfTest test, FailureOutputOptions options)
    {
        var testName = string.IsNullOrWhiteSpace(test.Name) ? test.Id ?? string.Empty : test.Name;
        var message = string.IsNullOrWhiteSpace(test.Message) ? null : test.Message;
        var stackTrace = options.IncludeStackTrace && !string.IsNullOrWhiteSpace(test.Trace)
            ? test.Trace
            : null;

        return new TestFailure(testName, message, stackTrace);
    }
}
