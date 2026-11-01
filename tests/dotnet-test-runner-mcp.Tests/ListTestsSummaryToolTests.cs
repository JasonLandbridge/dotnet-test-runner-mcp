using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class ListTestsSummaryToolTests
{
    private const string ProjectPath = "tests/Fake.Tests/Fake.Tests.csproj";

    [Fact]
    public async Task ListTestsSummary_WhenQualifiedPrefixTargetsUnqualifiedNames_ReturnsActionableDiscoveryFailure()
    {
        var shortTests = new[]
        {
            "ShouldReturnFalse_WhenOptionsIsNull",
            "ShouldReturnTrue_WhenFilterContainsCondition",
        };
        var runner = new FakeCommandRunner(CreateTestOutput(shortTests));

        var result = await CreateTool(runner)
            .ListTestsSummary(
                prefix: "Reaparr.Application.UnitTests.Class",
                projectPath: ProjectPath,
                includeTests: true,
                requireTests: true
            );

        Assert.Equal(TestOutcome.Error, result.Outcome);
        var error = Assert.IsType<ErrorInfo>(result.Error);
        Assert.Equal(ErrorKind.DiscoveryFailed, error.Kind);
        Assert.Contains("unqualified", error.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("qualified prefix", error.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(error.Stdout);
        Assert.Contains(shortTests[0], error.Stdout!.Text ?? string.Empty);
        Assert.Empty(result.Tests);
        Assert.Equal(0, result.TestCount);
        Assert.Equal(ErrorKind.DiscoveryFailed, Assert.Single(result.ProjectResults).Error?.Kind);
    }

    [Fact]
    public async Task ListTestsSummary_WhenShortPrefixTargetsUnqualifiedNames_FiltersMethodNames()
    {
        var runner = new FakeCommandRunner(
            CreateTestOutput([
                "ShouldReturnFalse_WhenOptionsIsNull",
                "ShouldReturnTrue_WhenFilterContainsCondition",
                "OtherTest_WhenCalled",
            ])
        );

        var result = await CreateTool(runner)
            .ListTestsSummary(
                prefix: "ShouldReturn",
                projectPath: ProjectPath,
                includeTests: true,
                requireTests: true
            );

        Assert.Equal(TestOutcome.Passed, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(
            new[]
            {
                "ShouldReturnFalse_WhenOptionsIsNull",
                "ShouldReturnTrue_WhenFilterContainsCondition",
            },
            result.Tests
        );
        Assert.Equal(2, result.TestCount);
    }

    [Fact]
    public async Task ListTestsSummary_WhenFullyQualifiedPrefixTargetsQualifiedNames_FiltersQualifiedNames()
    {
        var runner = new FakeCommandRunner(
            CreateTestOutput([
                "Reaparr.Application.UnitTests.Class.ShouldReturnFalse_WhenOptionsIsNull",
                "Reaparr.Application.UnitTests.Class.ShouldReturnTrue_WhenFilterContainsCondition",
                "Reaparr.Application.OtherClass.ShouldNotMatch",
            ])
        );

        var result = await CreateTool(runner)
            .ListTestsSummary(
                prefix: "Reaparr.Application.UnitTests.Class",
                projectPath: ProjectPath,
                includeTests: true,
                requireTests: true
            );

        Assert.Equal(TestOutcome.Passed, result.Outcome);
        Assert.Equal(
            new[]
            {
                "Reaparr.Application.UnitTests.Class.ShouldReturnFalse_WhenOptionsIsNull",
                "Reaparr.Application.UnitTests.Class.ShouldReturnTrue_WhenFilterContainsCondition",
            },
            result.Tests
        );
        Assert.Equal(2, result.TestCount);
    }

    [Fact]
    public async Task ListTestsSummary_ForVSTestProject_UsesPositionalProjectForDiscovery()
    {
        var projectPath = CreateVSTestProject();
        try
        {
            var runner = new FakeCommandRunner(
                CreateTestOutput([
                    "DotnetTestRunnerMcp.Tests.ProcessCommandRunnerTests.RunAsync_WhenProcessTimesOut_ReturnsCapturedStdoutAndStderr",
                ])
            );

            var result = await CreateTool(runner)
                .ListTestsSummary(projectPath: projectPath, includeTests: true, requireTests: true);

            Assert.Equal(TestOutcome.Passed, result.Outcome);
            var request = Assert.Single(
                runner.Requests.Where(request =>
                    request.Arguments.Contains("--list-tests", StringComparer.Ordinal)
                )
            );
            Assert.Equal(new[] { "test", projectPath, "--list-tests" }, request.Arguments);
        }
        finally
        {
            File.Delete(projectPath);
        }
    }

    [Fact]
    public async Task ListTestsSummary_WhenPrefixMatchesNoDiscoveredTests_ReportsNoTestsDiscovered()
    {
        var runner = new FakeCommandRunner(
            CreateTestOutput(["ShouldReturnFalse_WhenOptionsIsNull"])
        );

        var result = await CreateTool(runner)
            .ListTestsSummary(prefix: "Missing", projectPath: ProjectPath, requireTests: true);

        Assert.Equal(TestOutcome.Error, result.Outcome);
        Assert.Equal(ErrorKind.NoTestsDiscovered, Assert.IsType<ErrorInfo>(result.Error).Kind);
        Assert.Equal("No tests discovered.", result.Message);
        Assert.Equal(ErrorKind.NoTestsDiscovered, Assert.Single(result.ProjectResults).Error?.Kind);
    }

    private static ListTestsSummaryTool CreateTool(ICommandRunner commandRunner) =>
        new(Options.Create(new McpOptions()), commandRunner);

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

    private static string CreateTestOutput(string[] tests)
    {
        var lines = new List<string>
        {
            "Discovering tests from /repo/tests/bin/Debug/net10.0/Fake.Tests.dll (net10.0|x64)",
            $"Discovered {tests.Length} tests in assembly - /repo/tests/bin/Debug/net10.0/Fake.Tests.dll (net10.0|x64)",
        };
        lines.AddRange(tests.Select(test => $"  {test}"));
        return string.Join("\n", lines);
    }

    private sealed class FakeCommandRunner(string testOutput) : ICommandRunner
    {
        public List<CommandRequest> Requests { get; } = [];

        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            var output = request.Arguments.SequenceEqual(["sln", "list"], StringComparer.Ordinal)
                ? $"Project(s)\n----------\n{ProjectPath}"
                : testOutput;
            var lines = output.Split('\n', StringSplitOptions.None);

            return Task.FromResult(
                new CommandResult(0, output, string.Empty, lines, [], TimeSpan.Zero)
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
