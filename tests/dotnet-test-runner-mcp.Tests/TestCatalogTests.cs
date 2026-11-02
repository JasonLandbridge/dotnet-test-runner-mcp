using System.Text.Json;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class TestCatalogTests
{
    [Fact]
    public async Task DiscoverAsync_PreservesParameterizedDisplayNamesAndMarksSelectorAmbiguity()
    {
        var project = CreateProject("Microsoft.NET.Test.Sdk");
        try
        {
            var displays = new[]
            {
                "Catalog.Tests.ParameterizedTest(\"first value\")",
                "Catalog.Tests.ParameterizedTest(\"second value\")",
            };
            var catalog = CreateCatalog(
                new FakeCommandRunner(
                    new Dictionary<string, string> { [project] = CreateTestOutput(displays) },
                    [project]
                )
            );

            var result = await catalog.DiscoverAsync(project, null, CancellationToken.None);

            Assert.Equal(displays, result.Tests.Select(test => test.DisplayName));
            Assert.All(result.Tests, test =>
            {
                Assert.Equal("Catalog.Tests.ParameterizedTest", test.Selector);
                Assert.True(test.IsAmbiguous);
                Assert.Equal(TestRunnerDialect.VSTest, test.Dialect);
            });
            Assert.Equal(TestOutcome.Passed, Assert.Single(result.ProjectResults).Outcome);
        }
        finally
        {
            File.Delete(project);
        }
    }

    [Fact]
    public async Task DiscoverAsync_ReportsOneProjectErrorWithoutDroppingSuccessfulMatches()
    {
        var root = Path.Combine(Path.GetTempPath(), $"catalog-solution-{Guid.NewGuid():N}");
        var goodProject = "tests/Good/Good.csproj";
        var brokenProject = "tests/Broken/Broken.csproj";
        CreateProjectAt(root, goodProject, "Microsoft.NET.Test.Sdk");
        CreateProjectAt(root, brokenProject, "Microsoft.NET.Test.Sdk");
        try
        {
            var runner = new FakeCommandRunner(
                new Dictionary<string, string>
                {
                    [goodProject] = CreateTestOutput(["Good.Tests.Runs"]),
                    [brokenProject] = CreateTestOutput(["Broken.Tests.Runs"]),
                },
                [goodProject, brokenProject],
                new HashSet<string>(new[] { brokenProject }, StringComparer.Ordinal)
            );
            var catalog = CreateCatalog(
                runner,
                new McpOptions { WorkingDirectory = root }
            );

            var result = await catalog.DiscoverAsync(null, root, CancellationToken.None);

            Assert.Equal(new[] { "Good.Tests.Runs" }, result.Tests.Select(test => test.DisplayName));
            Assert.Equal(2, result.ProjectResults.Length);
            Assert.Equal(TestOutcome.Passed, result.ProjectResults.Single(value => value.ProjectPath == goodProject).Outcome);
            var broken = result.ProjectResults.Single(value => value.ProjectPath == brokenProject);
            Assert.Equal(TestOutcome.Error, broken.Outcome);
            Assert.Contains("simulated", broken.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoverAsync_UnknownDialectExposesAmbiguityInsteadOfClaimingExactSupport()
    {
        var project = CreateProject(null);
        try
        {
            var catalog = CreateCatalog(
                new FakeCommandRunner(
                    new Dictionary<string, string> { [project] = CreateTestOutput(["Unknown.Tests.Run"]) },
                    [project],
                    unknownProjects: new HashSet<string>([project], StringComparer.Ordinal)
                )
            );

            var result = await catalog.DiscoverAsync(project, null, CancellationToken.None);
            var test = Assert.Single(result.Tests);

            Assert.Equal(TestRunnerDialect.Unknown, test.Dialect);
            Assert.True(test.IsAmbiguous);
            Assert.Equal("Unknown.Tests.Run", test.Selector);
        }
        finally
        {
            File.Delete(project);
        }
    }

    private static TestCatalog CreateCatalog(ICommandRunner runner, McpOptions? options = null) =>
        new(Options.Create(options ?? new McpOptions()), runner);

    private static string CreateProject(string? package)
    {
        var path = Path.Combine(Path.GetTempPath(), $"catalog-{Guid.NewGuid():N}.csproj");
        var packageReference = package is null
            ? string.Empty
            : $"<PackageReference Include=\"{package}\" />";
        File.WriteAllText(
            path,
            $"<Project><ItemGroup>{packageReference}</ItemGroup></Project>"
        );
        return path;
    }

    private static void CreateProjectAt(string root, string relativePath, string package)
    {
        var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            $"<Project><ItemGroup><PackageReference Include=\"{package}\" /></ItemGroup></Project>"
        );
    }

    private static string CreateTestOutput(string[] tests)
    {
        var lines = new List<string>
        {
            "Discovering tests from /repo/bin/Debug/net10.0/Catalog.Tests.dll",
            $"Discovered {tests.Length} tests in assembly - /repo/bin/Debug/net10.0/Catalog.Tests.dll",
        };
        lines.AddRange(tests.Select(test => $"  {test}"));
        return string.Join('\n', lines);
    }

    private sealed class FakeCommandRunner(
        IReadOnlyDictionary<string, string> projectOutputs,
        IReadOnlyList<string> projects,
        IReadOnlySet<string>? failingProjects = null,
        IReadOnlySet<string>? unknownProjects = null
    ) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default
        )
        {
            if (request.Arguments.SequenceEqual(["sln", "list"], StringComparer.Ordinal))
            {
                var output = $"Project(s)\n----------\n{string.Join('\n', projects)}";
                return Task.FromResult(CreateResult(request, output));
            }

            if (request.Arguments.Length > 0 && request.Arguments[0] == "msbuild")
            {
                var metadataProject = request.Arguments.ElementAtOrDefault(1);
                var package = metadataProject is not null && unknownProjects?.Contains(metadataProject) != true
                    ? "Microsoft.NET.Test.Sdk"
                    : null;
                return Task.FromResult(CreateResult(request, Metadata(package)));
            }

            var project = request.Arguments.FirstOrDefault(argument => projectOutputs.ContainsKey(argument));
            if (project is null)
                throw new InvalidOperationException("No project was supplied to the fake runner.");
            if (failingProjects?.Contains(project) == true)
                throw new InvalidOperationException($"simulated discovery failure for {project}");

            return Task.FromResult(CreateResult(request, projectOutputs[project]));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static string Metadata(string? package) =>
            JsonSerializer.Serialize(
                new
                {
                    Properties = new Dictionary<string, string>
                    {
                        ["IsTestProject"] = "true",
                    },
                    Items = new
                    {
                        PackageReference = package is null
                            ? Array.Empty<object>()
                            : new[] { new { Identity = package } },
                        ProjectReference = Array.Empty<object>(),
                        Compile = Array.Empty<object>(),
                    },
                }
            );

        private static CommandResult CreateResult(CommandRequest request, string output)
        {
            var lines = output.Split('\n', StringSplitOptions.None);
            return new CommandResult(0, output, string.Empty, lines, [], TimeSpan.Zero)
            {
                FileName = request.FileName,
                Arguments = request.Arguments,
                WorkingDirectory = request.WorkingDirectory,
            };
        }
    }
}
