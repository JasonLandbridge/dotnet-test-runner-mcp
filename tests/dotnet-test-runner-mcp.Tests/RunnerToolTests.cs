using System.Text.Json;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class RunnerToolTests
{
    [Fact]
    public async Task RunAllTestsForProject_WhenVSTestUsesConsoleSummary()
    {
        var projectPath = CreateVSTestProject();
        try
        {
            var runner = new FakeCommandRunner(
                "Passed! - /repo/tests/bin/Debug/net10.0/dotnet-test-runner-mcp.Tests.dll (net10.0|x64)\n"
                    + "Passed: 2, Failed: 1, Skipped: 0, Total: 3, Duration: 1 s"
            );
            var tool = new RunAllTestsForProjectTool(
                runner,
                new JsonSerializerOptions(),
                Options.Create(new McpOptions())
            );

            var result = await tool.RunAllTestsForProject(projectPath);

            Assert.Equal(TestOutcome.Failed, result.Outcome);
            Assert.Equal(3, result.TestCount);
            Assert.Equal(1, result.Failed);
            Assert.Equal(
                new[] { "test", projectPath },
                Assert.Single(runner.Requests.Where(request => request.Arguments[0] != "msbuild")).Arguments
            );
        }
        finally
        {
            File.Delete(projectPath);
        }
    }

    [Fact]
    public async Task RunAllTestsInClass_WhenProjectIsNotSpecified_SkipsNonTestHelperProject()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"runner-tests-{Guid.NewGuid():N}");
        var projectPath = "tests/Real.Tests/Real.Tests.csproj";
        var fullProjectPath = Path.Combine(rootDirectory, projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullProjectPath)!);
        File.WriteAllText(
            fullProjectPath,
            """
            <Project>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
              </ItemGroup>
            </Project>
            """
        );

        try
        {
            var runner = new DiscoveryFakeCommandRunner(
                """
                Project(s)
                ----------
                tests/BaseTests/BaseTests.csproj
                tests/Real.Tests/Real.Tests.csproj
                """
            );
            var tool = new RunAllTestsInClassTool(
                runner,
                new JsonSerializerOptions(),
                Options.Create(new McpOptions { WorkingDirectory = rootDirectory })
            );

            var result = await tool.RunAllTestsInClass(
                "Real.Tests.SampleTests",
                workingDirectory: rootDirectory
            );

            Assert.Equal(TestOutcome.Passed, result.Outcome);
            Assert.Equal(1, result.TestCount);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunAllTests_WhenCtrfIsUnavailable_UsesConsoleSummaryAfterRetry()
    {
        var runner = new FakeCommandRunner(
            "Passed! - /repo/tests/bin/Debug/net10.0/dotnet-test-runner-mcp.Tests.dll (net10.0|x64)\n"
                + "Passed: 3, Failed: 0, Skipped: 0, Total: 3, Duration: 1 s"
        );
        var tool = new RunAllTestsTool(
            runner,
            new JsonSerializerOptions(),
            Options.Create(new McpOptions())
        );

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
            Options.Create(new McpOptions())
        );

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
            """
        );
        return projectPath;
    }
    private static string Metadata(string package, bool isTest = true) =>
        JsonSerializer.Serialize(
            new
            {
                Properties = new Dictionary<string, string>
                {
                    ["IsTestProject"] = isTest ? "true" : "false",
                    ["IsTestingPlatformApplication"] = "false",
                    ["OutputType"] = "Library",
                },
                Items = new
                {
                    PackageReference = new[] { new { Identity = package } },
                    ProjectReference = Array.Empty<object>(),
                    Compile = Array.Empty<object>(),
                },
            }
        );


    private sealed class DiscoveryFakeCommandRunner(string solutionOutput) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var isSolutionListing = request.Arguments.SequenceEqual(
                ["sln", "list"],
                StringComparer.Ordinal
            );
            var isMetadataEvaluation =
                request.Arguments.Length > 1 && request.Arguments[0] == "msbuild";
            var isRealProject = request.Arguments.Contains(
                "tests/Real.Tests/Real.Tests.csproj",
                StringComparer.Ordinal
            );
            var output = isSolutionListing
                ? solutionOutput
                : isMetadataEvaluation
                    ? request.Arguments[1].Replace('\\', '/').EndsWith(
                        "BaseTests/BaseTests.csproj",
                        StringComparison.Ordinal
                    )
                        ? Metadata("Microsoft.NET.Test.Sdk", false)
                        : Metadata("Microsoft.NET.Test.Sdk")
                    : isRealProject
                        ? "Passed! - /repo/tests/Real.Tests/bin/Debug/net10.0/Real.Tests.dll\n"
                            + "Passed: 1, Failed: 0, Skipped: 0, Total: 1, Duration: 1 s"
                        : "The selected project is not runnable.";
            var exitCode = isSolutionListing || isMetadataEvaluation || isRealProject ? 0 : 1;
            var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

            return Task.FromResult(
                new CommandResult(exitCode, output, string.Empty, lines, [], TimeSpan.Zero)
                {
                    FileName = request.FileName,
                    Arguments = request.Arguments,
                    WorkingDirectory = request.WorkingDirectory,
                }
            );
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCommandRunner(string output) : ICommandRunner
    {
        public List<CommandRequest> Requests { get; } = [];

        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            if (request.Arguments.Length > 0 && request.Arguments[0] == "msbuild")
                return Task.FromResult(
                    new CommandResult(
                        0,
                        Metadata("Microsoft.NET.Test.Sdk"),
                        string.Empty,
                        [Metadata("Microsoft.NET.Test.Sdk")],
                        [],
                        TimeSpan.Zero
                    )
                    {
                        FileName = request.FileName,
                        Arguments = request.Arguments,
                        WorkingDirectory = request.WorkingDirectory,
                    }
                );
            var invocationError = request.Arguments.Contains(
                "--report-ctrf",
                StringComparer.Ordinal
            );
            var commandOutput = invocationError
                ? "MSBUILD : error MSB1001: Unknown switch.\nSwitch: --report-ctrf"
                : output;
            var exitCode =
                invocationError ? 1
                : output.Contains("Failed: 1", StringComparison.Ordinal) ? 1
                : 0;
            var lines = commandOutput.Split('\n', StringSplitOptions.None);

            return Task.FromResult(
                new CommandResult(exitCode, commandOutput, string.Empty, lines, [], TimeSpan.Zero)
                {
                    FileName = request.FileName,
                    Arguments = request.Arguments,
                    WorkingDirectory = request.WorkingDirectory,
                }
            );
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
