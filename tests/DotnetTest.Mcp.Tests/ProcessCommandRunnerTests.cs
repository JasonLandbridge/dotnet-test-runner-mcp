using DotnetTest.Mcp.Terminal;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetTest.Mcp.Tests;

public sealed class ProcessCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_WhenProcessTimesOut_ReturnsCapturedStdoutAndStderr()
    {
        await using var runner = new ProcessCommandRunner(Options.Create(new McpOptions()));

        var result = await runner.RunAsync(
            new CommandRequest(
                "bash",
                "-lc",
                "printf 'raw-stdout\\n'; printf 'raw-stderr\\n' >&2; sleep 5")
            {
                Timeout = TimeSpan.FromSeconds(1),
                ThrowOnNonZeroExitCode = false,
            });

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("raw-stdout", result.StandardOutput);
        Assert.Contains("raw-stderr", result.StandardError);
        Assert.Contains("timed out", result.StandardError);
    }
}
