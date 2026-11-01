using System.Text.Json;
using DotnetTest.Mcp.Models;
using DotnetTest.Mcp.Terminal;
using DotnetTest.Mcp.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetTest.Mcp.Tests;

public sealed class RunnerToolTests
{
    [Fact]
    public async Task RunAllTestsForProject_WhenVSTestUsesConsoleSummary()
    {
        var projectPath = CreateVSTestProject();
        try
        {
            var runner = new FakeCommandRunner(
                "Passed! - /repo/tests/bin/Debug/net10.0/DotnetTest.Mcp.Tests.dll (net10.0|x64)\n"
                + "Passed: 2, Failed: 1, Skipped: 0, Total: 3, Duration: 1 s");
            var tool = new RunAllTestsForProjectTool(
                runner,
                new JsonSerializerOptions(),
                Options.Create(new McpOptions()));

            var result = await tool.RunAllTestsForProject(projectPath);

            Assert.Equal(TestOutcome.Failed, result.Outcome);
            Assert.Equal(3, result.TestCount);
            Assert.Equal(1, result.Failed);
            Assert.Equal(
                new[] { "test", projectPath },
                Assert.Single(runner.Requests).Arguments);
        }
        finally
        {
            File.Delete(projectPath);
        }
    }

    [Fact]
    public async Task RunAllTests_WhenCtrfIsUnavailable_UsesConsoleSummaryAfterRetry()
    {
        var runner = new FakeCommandRunner(
            "Passed! - /repo/tests/bin/Debug/net10.0/DotnetTest.Mcp.Tests.dll (net10.0|x64)\n"
            + "Passed: 3, Failed: 0, Skipped: 0, Total: 3, Duration: 1 s");
        var tool = new RunAllTestsTool(
            runner,
            new JsonSerializerOptions(),
            Options.Create(new McpOptions()));

        var result = await tool.RunAllTests();

        Assert.Equal(TestOutcome.Passed, result.Outcome);
        Assert.Equal(3, result.TestCount);
        Assert.Equal(2, runner.Requests.Count);
        Assert.Contains("--report-ctrf", runner.Requests[0].Arguments);
        Assert.DoesNotContain("--report-ctrf", runner.Requests[1].Arguments);
    }

    [Fact]
    public async Task RunAllTests_WhenNoConsoleSummary_ReturnsError()
    {
        var runner = new FakeCommandRunner("Build FAILED.\nerror MSB1001: Unknown switch.");
        var tool = new RunAllTestsTool(
            runner,
            new JsonSerializerOptions(),
            Options.Create(new McpOptions()));

        var result = await tool.RunAllTests();

        Assert.Equal(TestOutcome.Error, result.Outcome);
        Assert.Equal(ErrorKind.BuildFailed, Assert.IsType<ErrorInfo>(result.Error).Kind);
    }

    private static string CreateVSTestProject()
    {
        var projectPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csproj");
        File.WriteAllText(
            projectPath,
            """
            <Project>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
              </ItemGroup>
            </Project>
            """);
        return projectPath;
    }

    private sealed class FakeCommandRunner(string output) : ICommandRunner
    {
        public List<CommandRequest> Requests { get; } = [];

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var invocationError = request.Arguments.Contains("--report-ctrf", StringComparer.Ordinal);
            var commandOutput = invocationError
                ? "MSBUILD : error MSB1001: Unknown switch.\nSwitch: --report-ctrf"
                : output;
            var exitCode = invocationError ? 1 : output.Contains("Failed: 1", StringComparison.Ordinal) ? 1 : 0;
            var lines = commandOutput.Split('\n', StringSplitOptions.None);

            return Task.FromResult(
                new CommandResult(exitCode, commandOutput, string.Empty, lines, [], TimeSpan.Zero)
                {
                    FileName = request.FileName,
                    Arguments = request.Arguments,
                    WorkingDirectory = request.WorkingDirectory,
                });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
