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

    [Fact]
    public async Task RunAsync_BoundsHugeSingleLineStreamsAndRetainsHeadAndTail()
    {
        await using var runner = new ProcessCommandRunner(Options.Create(new McpOptions()));
        var request = OperatingSystem.IsWindows()
            ? new CommandRequest(
                "powershell.exe",
                "-NoProfile",
                "-Command",
                "$value = ('x' * 200000); Write-Output ('HEAD' + $value + 'TAIL'); [Console]::Error.WriteLine('ERRHEAD' + $value + 'ERRTAIL')"
            )
            : new CommandRequest(
                "bash",
                "-c",
                "value=$(printf '%200000s' | tr ' ' x); printf 'HEAD%sTAIL' \"$value\"; printf 'ERRHEAD%sERRTAIL' \"$value\" >&2"
            );

        var result = await runner.RunAsync(
            request with
            {
                MaxOutputChars = 128,
                ThrowOnNonZeroExitCode = false,
            }
        );

        Assert.True(result.StandardOutputTruncated);
        Assert.True(result.StandardErrorTruncated);
        Assert.True(result.StandardOutputOriginalCharacterCount > 128);
        Assert.True(result.StandardErrorOriginalCharacterCount > 128);
        Assert.Contains("HEAD", result.StandardOutput);
        Assert.Contains("TAIL", result.StandardOutput);
        Assert.Contains("ERRHEAD", result.StandardError);
        Assert.Contains("ERRTAIL", result.StandardError);
        Assert.True(result.StandardOutputLines.Length >= 3);
        Assert.True(result.StandardErrorLines.Length >= 3);
        Assert.Equal(
            result.StandardOutput,
            string.Join(Environment.NewLine, result.StandardOutputLines)
        );
        Assert.Equal(
            result.StandardError,
            string.Join(Environment.NewLine, result.StandardErrorLines)
        );
    }

    [Fact]
    public async Task RunAsync_WhenExternallyCanceled_PropagatesCancellation()
    {
        await using var runner = new ProcessCommandRunner(Options.Create(new McpOptions()));
        var request = OperatingSystem.IsWindows()
            ? new CommandRequest("cmd.exe", "/d", "/c", "ping -n 6 127.0.0.1 >nul")
            : new CommandRequest("bash", "-c", "sleep 5");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(
                request with { ThrowOnNonZeroExitCode = false },
                cancellation.Token
            )
        );
    }
}

