using DotnetTestRunnerMcp.Terminal;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class ProcessCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_WhenProcessTimesOut_ReturnsCapturedStdoutAndStderr()
    {
        await using var runner = new ProcessCommandRunner(Options.Create(new McpOptions()));

        var request = OperatingSystem.IsWindows()
            ? new CommandRequest(
                "cmd.exe",
                "/d",
                "/c",
                "echo raw-stdout & echo raw-stderr 1>&2 & ping -n 6 127.0.0.1 >nul"
            )
            : new CommandRequest(
                "bash",
                "-c",
                "printf 'raw-stdout\\n'; printf 'raw-stderr\\n' >&2; sleep 5"
            );

        var result = await runner.RunAsync(
            request with
            {
                Timeout = TimeSpan.FromSeconds(1),
                ThrowOnNonZeroExitCode = false,
            }
        );

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("raw-stdout", result.StandardOutput);
        Assert.Contains("raw-stderr", result.StandardError);
        Assert.Contains("timed out", result.StandardError);
    }
}
