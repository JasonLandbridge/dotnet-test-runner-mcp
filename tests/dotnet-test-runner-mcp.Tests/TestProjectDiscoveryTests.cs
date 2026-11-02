using System.Text.Json;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class TestProjectDiscoveryTests
{
    [Fact]
    public async Task ListAsync_FiltersConfiguredDirectoryByNormalizedBoundary()
    {
        var runner = new FakeCommandRunner(
            "Project(s)\n"
                + "----------\n"
                + "tests/Good/Good.csproj\n"
                + "tests-tools/NotTests/NotTests.csproj\n"
                + "tests\\Windows/Windows.csproj\n"
                + "Tests/Case/Case.csproj",
            new Dictionary<string, string>
            {
                ["tests/Good/Good.csproj"] = Metadata(true),
                ["tests\\Windows/Windows.csproj"] = Metadata(true),
                ["Tests/Case/Case.csproj"] = Metadata(true),
            }
        );
        var projects = await TestProjectDiscovery.ListAsync(
            runner,
            new McpOptions { TestsDirectoryName = "tests" },
            null,
            CancellationToken.None
        );

        var expected = OperatingSystem.IsWindows()
            ? new[]
            {
                "tests/Good/Good.csproj",
                "tests\\Windows/Windows.csproj",
                "Tests/Case/Case.csproj",
            }
            : new[] { "tests/Good/Good.csproj", "tests\\Windows/Windows.csproj" };
        Assert.Equal(expected, projects);
    }

    [Fact]
    public async Task ListAsync_ExcludesProjectsWithExplicitFalseMetadata()
    {
        const string helperProject = "tests/BaseTests/BaseTests.csproj";
        const string testProject = "tests/Real.Tests/Real.Tests.csproj";
        var runner = new FakeCommandRunner(
            $"Project(s)\n----------\n{helperProject}\n{testProject}",
            new Dictionary<string, string>
            {
                [helperProject] = Metadata(false),
                [testProject] = Metadata(true),
            }
        );

        var projects = await TestProjectDiscovery.ListAsync(
            runner,
            new McpOptions(),
            null,
            CancellationToken.None
        );

        Assert.Equal(new[] { testProject }, projects);
    }

    [Fact]
    public async Task ListAsync_RetainsVSTestLibraryWhenMetadataIsTrue()
    {
        const string project = "tests/Library.Tests/Library.Tests.csproj";
        var runner = new FakeCommandRunner(
            $"Project(s)\n----------\n{project}",
            new Dictionary<string, string> { [project] = Metadata(true) }
        );

        var projects = await TestProjectDiscovery.ListAsync(
            runner,
            new McpOptions(),
            null,
            CancellationToken.None
        );

        Assert.Equal(new[] { project }, projects);
    }

    [Fact]
    public async Task ListAsync_RetainsProjectWhenMetadataIsUnknown()
    {
        const string project = "tests/Fresh/Fresh.csproj";
        var runner = new FakeCommandRunner(
            $"Project(s)\n----------\n{project}",
            new Dictionary<string, string> { [project] = Metadata(null) }
        );

        var projects = await TestProjectDiscovery.ListAsync(
            runner,
            new McpOptions(),
            null,
            CancellationToken.None
        );

        Assert.Equal(new[] { project }, projects);
    }

    [Fact]
    public async Task ListAsync_WhenMetadataEvaluationFails_ThrowsUsefulError()
    {
        const string project = "tests/Broken/Broken.csproj";
        var runner = new FakeCommandRunner(
            $"Project(s)\n----------\n{project}",
            new Dictionary<string, string> { [project] = Metadata(null) },
            metadataExitCode: 1,
            metadataError: "error MSB1009: Project file does not exist."
        );

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestProjectDiscovery.ListAsync(
                runner,
                new McpOptions(),
                "/repo",
                CancellationToken.None
            )
        );

        Assert.Contains(project, exception.Message, StringComparison.Ordinal);
        Assert.Contains("exit code 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MSB1009", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAsync_ExcludesProjectWhenImportedMetadataIsFalse()
    {
        var rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"test-project-discovery-{Guid.NewGuid():N}"
        );
        var projectPath = "tests/ImportedHelper/ImportedHelper.csproj";
        var projectDirectory = Path.Combine(rootDirectory, "tests", "ImportedHelper");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(
            Path.Combine(projectDirectory, "Directory.Build.props"),
            """
            <Project>
              <PropertyGroup>
                <IsTestProject>false</IsTestProject>
              </PropertyGroup>
            </Project>
            """
        );
        File.WriteAllText(
            Path.Combine(projectDirectory, "ImportedHelper.csproj"),
            """
            <Project>
              <Import Project="$(MSBuildThisFileDirectory)Directory.Build.props" />
            </Project>
            """
        );

        try
        {
            await using var processRunner = new ProcessCommandRunner(
                Options.Create(new McpOptions { WorkingDirectory = rootDirectory })
            );
            var runner = new SolutionListingCommandRunner(
                processRunner,
                $"Project(s)\n----------\n{projectPath}"
            );

            var projects = await TestProjectDiscovery.ListAsync(
                runner,
                new McpOptions { TestsDirectoryName = "tests" },
                rootDirectory,
                CancellationToken.None
            );

            Assert.Empty(projects);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private static string Metadata(bool? isTest) =>
        JsonSerializer.Serialize(
            new
            {
                Properties = new Dictionary<string, string>
                {
                    ["IsTestProject"] = isTest?.ToString().ToLowerInvariant() ?? "",
                },
                Items = new
                {
                    PackageReference = Array.Empty<object>(),
                    ProjectReference = Array.Empty<object>(),
                    Compile = Array.Empty<object>(),
                },
            }
        );

    private sealed class SolutionListingCommandRunner(
        ICommandRunner metadataRunner,
        string solutionOutput
    ) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (
                !request.Arguments.SequenceEqual(
                    ["sln", "list"],
                    StringComparer.Ordinal
                )
            )
                return metadataRunner.RunAsync(request, cancellationToken);

            var lines = solutionOutput.Split('\n', StringSplitOptions.None);
            return Task.FromResult(
                new CommandResult(0, solutionOutput, string.Empty, lines, [], TimeSpan.Zero)
                {
                    FileName = request.FileName,
                    Arguments = request.Arguments,
                    WorkingDirectory = request.WorkingDirectory,
                }
            );
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCommandRunner(
        string solutionOutput,
        IReadOnlyDictionary<string, string> metadataOutput,
        int metadataExitCode = 0,
        string metadataError = ""
    ) : ICommandRunner
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
                request.Arguments.Length > 1
                && request.Arguments[0] == "msbuild";
            var output = isSolutionListing
                ? solutionOutput
                : isMetadataEvaluation
                    && metadataOutput.TryGetValue(request.Arguments[1], out var metadata)
                    ? metadata
                    : string.Empty;
            var standardError = isMetadataEvaluation ? metadataError : string.Empty;
            var exitCode = isMetadataEvaluation ? metadataExitCode : 0;
            var lines = output.Split('\n', StringSplitOptions.None);
            var errorLines = standardError.Split('\n', StringSplitOptions.None);

            return Task.FromResult(
                new CommandResult(exitCode, output, standardError, lines, errorLines, TimeSpan.Zero)
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
