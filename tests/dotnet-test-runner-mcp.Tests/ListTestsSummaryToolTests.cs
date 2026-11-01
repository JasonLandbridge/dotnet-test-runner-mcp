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

    [Fact]
    public async Task ListTestsSummary_WhenSolutionContainsMoreThanEightProjects_DiscoversEveryProject()
    {
        var projectOutputs = Enumerable.Range(1, 9)
            .ToDictionary(
                index => $"tests/Project{index}/Project{index}.csproj",
                index => CreateTestOutput([$"ShouldVerifyProject{index}"])
            );
        var runner = new FakeCommandRunner(string.Empty, projectOutputs);

        var result = await CreateTool(runner)
            .ListTestsSummary(includeTests: true, requireTests: true);

        Assert.Equal(TestOutcome.Passed, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(9, result.TestCount);
        Assert.Equal(
            projectOutputs.Keys,
            result.ProjectResults.Select(project => project.ProjectPath)
        );
        Assert.All(
            result.ProjectResults,
            project => Assert.Equal(TestOutcome.Passed, project.Outcome)
        );
        Assert.Equal(
            Enumerable.Range(1, 9).Select(index => $"ShouldVerifyProject{index}"),
            result.Tests
        );
    }

    [Fact]
    public async Task ListTestsSummary_WhenProjectsShareDisplayNames_PreservesBothTestCases()
    {
        var projectOutputs = new Dictionary<string, string>
        {
            ["tests/First/First.csproj"] = CreateTestOutput(["ShouldWork"]),
            ["tests/Second/Second.csproj"] = CreateTestOutput(["ShouldWork"]),
        };
        var runner = new FakeCommandRunner(string.Empty, projectOutputs);

        var result = await CreateTool(runner)
            .ListTestsSummary(includeTests: true, requireTests: true);

        Assert.Equal(TestOutcome.Passed, result.Outcome);
        Assert.Equal(2, result.TestCount);
        Assert.Equal(result.ProjectResults.Sum(project => project.TestCount), result.TestCount);
        Assert.Equal(new[] { "ShouldWork", "ShouldWork" }, result.Tests);
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(true, 200, true)]
    public async Task ListTestsSummary_WhenNamesAreCapped_PreservesCompleteCounts(
        bool includeTests,
        int expectedReturnedCount,
        bool expectedHasMore
    )
    {
        var testNames = Enumerable.Range(1, 211)
            .Select(index => $"ShouldVerifyCase{index:D3}")
            .ToArray();
        var runner = new FakeCommandRunner(CreateTestOutput(testNames));

        var result = await CreateTool(runner)
            .ListTestsSummary(
                projectPath: ProjectPath,
                includeTests: includeTests,
                requireTests: true
            );

        Assert.Equal(TestOutcome.Passed, result.Outcome);
        Assert.Equal(211, result.TestCount);
        Assert.Equal(211, Assert.Single(result.ProjectResults).TestCount);
        Assert.Equal(expectedReturnedCount, result.ReturnedCount);
        Assert.Equal(expectedHasMore, result.HasMore);
        Assert.Equal(testNames.Take(expectedReturnedCount), result.Tests);
    }

    [Fact]
    public async Task ListTestsSummary_WhenExplicitProjectHasNoSolution_DiscoversItsTests()
    {
        var runner = new FakeCommandRunner(
            CreateTestOutput(["ShouldRunWithoutSolution"]),
            hasSolution: false
        );

        var result = await CreateTool(runner)
            .ListTestsSummary(projectPath: ProjectPath, includeTests: true, requireTests: true);

        Assert.Equal(TestOutcome.Passed, result.Outcome);
        Assert.Equal(new[] { "ShouldRunWithoutSolution" }, result.Tests);
        Assert.Equal(1, result.TestCount);
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

    private sealed class FakeCommandRunner(
        string testOutput,
        IReadOnlyDictionary<string, string>? projectOutputs = null,
        bool hasSolution = true
    ) : ICommandRunner
    {
        public List<CommandRequest> Requests { get; } = [];

        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            string output;
            if (request.Arguments.SequenceEqual(["sln", "list"], StringComparer.Ordinal))
            {
                if (!hasSolution)
                    throw new InvalidOperationException("No solution exists for this project.");

                IEnumerable<string> projects = projectOutputs is null
                    ? [ProjectPath]
                    : projectOutputs.Keys;
                output = $"Project(s)\n----------\n{string.Join("\n", projects)}";
            }
            else if (request.Arguments[0] == "msbuild")
            {
                output = "true";
            }
            else
            {
                output = projectOutputs is null
                    ? testOutput
                    : projectOutputs[
                        request.Arguments.Single(argument => projectOutputs.ContainsKey(argument))
                    ];
            }
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
