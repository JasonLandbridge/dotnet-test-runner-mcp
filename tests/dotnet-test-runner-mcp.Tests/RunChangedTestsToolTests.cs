using System.Text.Json;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class RunChangedTestsToolTests
{
    [Fact]
    public async Task AnalyzeAsync_SelectsMultipleTransitiveDependentsButNotUnrelatedProjects()
    {
        using var fixture = Fixture.Create(
            new Dictionary<string, ProjectFixture>
            {
                ["src/Product/Product.csproj"] = ProjectFixture.Source("src/Product/Product.cs"),
                ["src/Library/Library.csproj"] = ProjectFixture.Project(
                    ["src/Product/Product.csproj"],
                    "src/Library/Library.cs"
                ),
                ["tests/Product.Tests/Product.Tests.csproj"] = ProjectFixture.Project(
                    ["src/Library/Library.csproj"],
                    "tests/Product.Tests/ProductTests.cs"
                ),
                ["tests/Product.More.Tests/Product.More.Tests.csproj"] = ProjectFixture.Project(
                    ["src/Library/Library.csproj"],
                    "tests/Product.More.Tests/ProductMoreTests.cs"
                ),
                ["src/Unrelated/Unrelated.csproj"] = ProjectFixture.Source(
                    "src/Unrelated/Unrelated.cs"
                ),
                ["tests/Unrelated.Tests/Unrelated.Tests.csproj"] = ProjectFixture.Project(
                    ["src/Unrelated/Unrelated.csproj"],
                    "tests/Unrelated.Tests/UnrelatedTests.cs"
                ),
            },
            diff: "M\0src/Product/Product.cs\0"
        );

        var result = await fixture.AnalyzeAsync();

        Assert.True(result.Succeeded);
        Assert.False(result.ImpactUnknown);
        Assert.Equal(
            new[]
            {
                "tests/Product.Tests/Product.Tests.csproj",
                "tests/Product.More.Tests/Product.More.Tests.csproj",
            },
            result.SelectedTestProjects
        );
        Assert.DoesNotContain(
            "tests/Unrelated.Tests/Unrelated.Tests.csproj",
            result.SelectedTestProjects
        );
        var productExplanation = Assert.Single(
            result.ImpactExplanations.Where(value =>
                value.SelectedTestProject == "tests/Product.Tests/Product.Tests.csproj"
            )
        );
        Assert.Contains(
            productExplanation.Chains,
            chain =>
                chain.ChangedPath == "src/Product/Product.cs"
                && chain.OwnerProject == "src/Product/Product.csproj"
                && chain.ReverseDependencyPath.SequenceEqual(
                    new[]
                    {
                        "src/Product/Product.csproj",
                        "src/Library/Library.csproj",
                        "tests/Product.Tests/Product.Tests.csproj",
                    }
                )
        );
    }

    [Fact]
    public async Task AnalyzeAsync_BoundsLongProvenanceAndReportsTruncation()
    {
        var projects = new Dictionary<string, ProjectFixture>
        {
            ["src/Root/Root.csproj"] = ProjectFixture.Source("src/Root/Root.cs"),
        };
        var previous = "src/Root/Root.csproj";
        for (var index = 1; index <= 70; index++)
        {
            var project = $"src/Chain{index:00}/Chain{index:00}.csproj";
            projects[project] = ProjectFixture.Project(
                [previous],
                $"src/Chain{index:00}/Chain{index:00}.cs"
            );
            previous = project;
        }

        projects["tests/Chain.Tests/Chain.Tests.csproj"] = ProjectFixture.Project(
            [previous],
            "tests/Chain.Tests/ChainTests.cs"
        );
        using var fixture = Fixture.Create(
            projects,
            diff: "M\0src/Root/Root.cs\0"
        );

        var result = await fixture.AnalyzeAsync();

        Assert.Equal(
            new[] { "tests/Chain.Tests/Chain.Tests.csproj" },
            result.SelectedTestProjects
        );
        var explanation = Assert.Single(result.ImpactExplanations);
        Assert.True(explanation.IsUnknown);
        Assert.True(explanation.IsTruncated);
        Assert.Contains(
            explanation.Reasons,
            reason => reason.Contains("truncated", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public async Task AnalyzeAsync_HandlesCyclesAndProjectDirectoryBoundaries()
    {
        using var fixture = Fixture.Create(
            new Dictionary<string, ProjectFixture>
            {
                ["src/One/One.csproj"] = ProjectFixture.Project(
                    ["src/Two/Two.csproj"],
                    "src/One/One.cs"
                ),
                ["src/Two/Two.csproj"] = ProjectFixture.Project(
                    ["src/One/One.csproj"],
                    "src/Two/Two.cs"
                ),
                ["tests/One.Tests/One.Tests.csproj"] = ProjectFixture.Project(
                    ["src/One/One.csproj"],
                    "tests/One.Tests/OneTests.cs"
                ),
                ["src/App/App.csproj"] = ProjectFixture.Source("src/App/App.cs"),
                ["src/App2/App2.csproj"] = ProjectFixture.Source("src/App2/App2.cs"),
                ["tests/App.Tests/App.Tests.csproj"] = ProjectFixture.Project(
                    ["src/App/App.csproj"],
                    "tests/App.Tests/AppTests.cs"
                ),
                ["tests/App2.Tests/App2.Tests.csproj"] = ProjectFixture.Project(
                    ["src/App2/App2.csproj"],
                    "tests/App2.Tests/App2Tests.cs"
                ),
            },
            diff: "M\0src/App2/App2.cs\0"
        );

        var boundaryResult = await fixture.AnalyzeAsync();
        Assert.Equal(new[] { "tests/App2.Tests/App2.Tests.csproj" }, boundaryResult.SelectedTestProjects);

        fixture.Runner.SetDiff("M\0src/One/One.cs\0");
        var cycleResult = await fixture.AnalyzeAsync();
        Assert.Equal(new[] { "tests/One.Tests/One.Tests.csproj" }, cycleResult.SelectedTestProjects);
        Assert.Contains(
            cycleResult.ImpactExplanations.SelectMany(value => value.Reasons),
            reason => reason.Contains("Cycle-safe traversal", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task AnalyzeAsync_SelectsAllForSharedUnknownDeletedAndRenamedChanges()
    {
        using var fixture = Fixture.Create(
            new Dictionary<string, ProjectFixture>
            {
                ["src/Product/Product.csproj"] = ProjectFixture.Source("src/Product/Product.cs"),
                ["tests/Product.Tests/Product.Tests.csproj"] = ProjectFixture.Project(
                    ["src/Product/Product.csproj"],
                    "tests/Product.Tests/ProductTests.cs"
                ),
                ["tests/Other.Tests/Other.Tests.csproj"] = ProjectFixture.Source(
                    "tests/Other.Tests/OtherTests.cs"
                ),
            },
            diff: "M\0Directory.Build.props\0",
            untracked: ""
        );

        var sharedResult = await fixture.AnalyzeAsync();
        Assert.True(sharedResult.ImpactUnknown);
        Assert.Equal(fixture.TestProjects, sharedResult.SelectedTestProjects);
        Assert.Contains(sharedResult.Reasons, reason => reason.Contains("Shared configuration", StringComparison.Ordinal));

        fixture.Runner.SetDiff("D\0src/Product/Deleted.cs\0");
        var deletedResult = await fixture.AnalyzeAsync();
        Assert.True(deletedResult.ImpactUnknown);
        Assert.Equal(fixture.TestProjects, deletedResult.SelectedTestProjects);
        Assert.Contains(
            deletedResult.Reasons,
            reason => reason.Contains("No evaluated Compile item", StringComparison.Ordinal)
        );

        fixture.Runner.SetDiff("R100\0src/Product/Old.cs\0src/Product/Renamed.cs\0");
        var renamedResult = await fixture.AnalyzeAsync();
        Assert.True(renamedResult.ImpactUnknown);
        Assert.Equal(fixture.TestProjects, renamedResult.SelectedTestProjects);
        Assert.Equal(
            new[] { "src/Product/Old.cs", "src/Product/Renamed.cs" },
            renamedResult.ChangedFiles
        );
        Assert.Contains(
            renamedResult.Reasons,
            reason => reason.Contains("No evaluated Compile item", StringComparison.Ordinal)
        );

        fixture.Runner.SetDiff("");
        fixture.Runner.SetUntracked("docs/new-file.txt\0");
        var unknownResult = await fixture.AnalyzeAsync();
        Assert.True(unknownResult.ImpactUnknown);
        Assert.Equal(fixture.TestProjects, unknownResult.SelectedTestProjects);
    }

    [Fact]
    public async Task AnalyzeAsync_DeletedLinkedWildcardSelectsIndependentConsumersConservatively()
    {
        using var fixture = Fixture.Create(
            new Dictionary<string, ProjectFixture>
            {
                ["src/ConsumerA/ConsumerA.csproj"] = new ProjectFixture(
                    [],
                    ["src/Shared/Linked/StillPresent.cs"],
                    CompileLinks: new Dictionary<string, string?>
                    {
                        ["src/Shared/Linked/StillPresent.cs"] = "Linked/StillPresent.cs",
                    }
                ),
                ["src/ConsumerB/ConsumerB.csproj"] = new ProjectFixture(
                    [],
                    ["src/Shared/Linked/StillPresent.cs"],
                    CompileLinks: new Dictionary<string, string?>
                    {
                        ["src/Shared/Linked/StillPresent.cs"] = "Linked/StillPresent.cs",
                    }
                ),
                ["tests/ConsumerA.Tests/ConsumerA.Tests.csproj"] = ProjectFixture.Project(
                    ["src/ConsumerA/ConsumerA.csproj"],
                    "tests/ConsumerA.Tests/ConsumerATests.cs"
                ),
                ["tests/ConsumerB.Tests/ConsumerB.Tests.csproj"] = ProjectFixture.Project(
                    ["src/ConsumerB/ConsumerB.csproj"],
                    "tests/ConsumerB.Tests/ConsumerBTests.cs"
                ),
            },
            diff: "D\0src/Shared/Linked/Deleted.cs\0"
        );

        var result = await fixture.AnalyzeAsync();

        Assert.True(result.ImpactUnknown);
        Assert.Equal(fixture.TestProjects, result.SelectedTestProjects);
        Assert.All(
            result.ImpactExplanations.Where(value =>
                value.SelectedTestProject != "<additional selected projects omitted>"
            ),
            explanation =>
            {
                Assert.True(explanation.IsUnknown);
                Assert.Contains(
                    explanation.Chains,
                    chain =>
                        chain.ChangedPath == "src/Shared/Linked/Deleted.cs"
                        && chain.OwnerProject is null
                        && chain.Reason!.Contains(
                            "No evaluated Compile item",
                            StringComparison.Ordinal
                        )
                );
            }
        );
    }

    [Fact]
    public async Task AnalyzeAsync_RenameUsesEvaluatedCompileEvidenceWhenOldAndNewPathsArePresent()
    {
        using var fixture = Fixture.Create(
            new Dictionary<string, ProjectFixture>
            {
                ["src/Product/Product.csproj"] = new ProjectFixture(
                    [],
                    ["src/Product/Old.cs", "src/Product/Renamed.cs"]
                ),
                ["tests/Product.Tests/Product.Tests.csproj"] = ProjectFixture.Project(
                    ["src/Product/Product.csproj"],
                    "tests/Product.Tests/ProductTests.cs"
                ),
                ["tests/Other.Tests/Other.Tests.csproj"] = ProjectFixture.Source(
                    "tests/Other.Tests/OtherTests.cs"
                ),
            },
            diff: "R100\0src/Product/Old.cs\0src/Product/Renamed.cs\0"
        );

        var result = await fixture.AnalyzeAsync();

        Assert.False(result.ImpactUnknown);
        Assert.Equal(
            new[] { "tests/Product.Tests/Product.Tests.csproj" },
            result.SelectedTestProjects
        );
        Assert.All(
            result.ImpactExplanations,
            explanation => Assert.All(explanation.Chains, chain => Assert.NotNull(chain.OwnerProject))
        );
    }

    [Fact]
    public async Task AnalyzeAsync_GraphEvaluationErrorSelectsAllWithReason()
    {
        using var fixture = Fixture.Create(
            new Dictionary<string, ProjectFixture>
            {
                ["src/Product/Product.csproj"] = ProjectFixture.Source("src/Product/Product.cs"),
                ["tests/Product.Tests/Product.Tests.csproj"] = ProjectFixture.Project(
                    ["src/Product/Product.csproj"],
                    "tests/Product.Tests/ProductTests.cs"
                ),
                ["src/Other/Other.csproj"] = ProjectFixture.Source(
                    "src/Other/Other.cs"
                ),
            },
            diff: "M\0src/Product/Product.cs\0"
        );
        fixture.Runner.FailGraphFor("src/Other/Other.csproj");

        var result = await fixture.AnalyzeAsync();

        Assert.True(result.ImpactUnknown);
        Assert.Equal(fixture.TestProjects, result.SelectedTestProjects);
        Assert.Contains(result.Reasons, reason => reason.Contains("MSBuild evaluation failed", StringComparison.Ordinal));
        Assert.All(result.ImpactExplanations, explanation => Assert.True(explanation.IsUnknown));
    }

    [Fact]
    public async Task AnalyzeAsync_MultiTargetProjectSelectsAllWithExplicitUncertainty()
    {
        using var fixture = Fixture.Create(
            new Dictionary<string, ProjectFixture>
            {
                ["src/Product/Product.csproj"] = ProjectFixture.Source(
                    "src/Product/Product.cs",
                    targetFrameworks: "net8.0;net10.0"
                ),
                ["tests/Product.Tests/Product.Tests.csproj"] = ProjectFixture.Project(
                    ["src/Product/Product.csproj"],
                    "tests/Product.Tests/ProductTests.cs"
                ),
                ["tests/Other.Tests/Other.Tests.csproj"] = ProjectFixture.Source(
                    "tests/Other.Tests/OtherTests.cs"
                ),
            },
            diff: "M\0src/Product/Product.cs\0"
        );

        var result = await fixture.AnalyzeAsync();

        Assert.True(result.ImpactUnknown);
        Assert.Equal(fixture.TestProjects, result.SelectedTestProjects);
        Assert.Contains(result.Reasons, reason => reason.Contains("multiple frameworks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunChangedTests_InvalidBaseRefDoesNotDiscoverOrExecuteTests()
    {
        using var fixture = Fixture.Create(
            new Dictionary<string, ProjectFixture>
            {
                ["tests/Product.Tests/Product.Tests.csproj"] = ProjectFixture.Source(
                    "tests/Product.Tests/ProductTests.cs"
                ),
            },
            diff: "M\0tests/Product.Tests/ProductTests.cs\0"
        );
        fixture.Runner.InvalidBase = true;

        var result = await fixture.CreateTool().RunChangedTests(workingDirectory: fixture.Root);

        Assert.Equal(TestOutcome.Error, result.Outcome);
        Assert.Null(result.Execution);
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.Arguments.Contains("test"));
        Assert.DoesNotContain(
            fixture.Runner.Requests,
            request => request.FileName == "dotnet" && request.Arguments.Contains("sln")
        );
    }

    [Fact]
    public async Task RunChangedTests_DryRunDoesNotExecuteTestsAndUsesNestedSolutionDirectory()
    {
        using var fixture = Fixture.Create(
            new Dictionary<string, ProjectFixture>
            {
                ["tests/Product.Tests/Product.Tests.csproj"] = ProjectFixture.Source(
                    "tests/Product.Tests/ProductTests.cs"
                ),
            },
            diff: "M\0src/Solution/tests/Product.Tests/ProductTests.cs\0",
            solutionDirectory: "src/Solution"
        );

        var result = await fixture.CreateTool().RunChangedTests(
            dryRun: true,
            workingDirectory: Path.Combine(fixture.SolutionDirectory, "tests")
        );

        Assert.True(result.DryRun);
        Assert.Contains("no tests were executed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(result.ImpactExplanations);
        Assert.DoesNotContain(fixture.Runner.Requests, request => request.Arguments.Contains("test"));
        Assert.Contains(
            fixture.Runner.Requests,
            request =>
                request.FileName == "dotnet"
                && string.Equals(
                    request.WorkingDirectory,
                    fixture.SolutionDirectory,
                    StringComparison.Ordinal
                )
        );

    }

    [Fact]
    public async Task RunChangedTests_ExecutesWholeProjectSelectionsAndExposesRunId()
    {
        using var fixture = Fixture.Create(
            new Dictionary<string, ProjectFixture>
            {
                ["tests/Product.Tests/Product.Tests.csproj"] = ProjectFixture.Source(
                    "tests/Product.Tests/ProductTests.cs"
                ),
            },
            diff: "M\0tests/Product.Tests/ProductTests.cs\0"
        );

        var result = await fixture.CreateTool().RunChangedTests(workingDirectory: fixture.Root);
        Assert.Equal(TestOutcome.Passed, result.Outcome);
        Assert.Equal(1, result.Execution?.TestCount);
        Assert.Equal(1, result.Execution?.Passed);
        Assert.Single(result.ImpactExplanations);
        Assert.Contains(
            result.Reasons,
            reason => reason.Contains("does not claim method-level coverage", StringComparison.Ordinal)
        );

        Assert.NotNull(result.Execution);
        Assert.Equal(result.Execution!.RunId, result.RunId);
        var projectExecution = Assert.Single(result.Execution!.ProjectExecutions);
        Assert.Equal("tests/Product.Tests/Product.Tests.csproj", projectExecution.ProjectPath);
        Assert.Equal(1, projectExecution.TestCount);
        Assert.Contains(
            fixture.Runner.Requests,
            request =>
                request.FileName == "dotnet"
                && request.Arguments.Length > 0
                && request.Arguments[0] == "test"
        );

    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _solutionDirectory;

        private Fixture(
            string root,
            string solutionDirectory,
            FakeCommandRunner runner,
            string[] testProjects
        )
        {
            Root = root;
            _solutionDirectory = solutionDirectory;
            Runner = runner;
            TestProjects = testProjects;
        }

        public string Root { get; }
        public string SolutionDirectory => _solutionDirectory;
        public FakeCommandRunner Runner { get; }
        public string[] TestProjects { get; }

        public static Fixture Create(
            IReadOnlyDictionary<string, ProjectFixture> projects,
            string diff,
            string untracked = "",
            string solutionDirectory = ""
        )
        {
            var root = Path.Combine(Path.GetTempPath(), $"changed-tests-{Guid.NewGuid():N}");
            var solutionPath = string.IsNullOrWhiteSpace(solutionDirectory)
                ? root
                : Path.Combine(root, solutionDirectory);
            Directory.CreateDirectory(solutionPath);
            File.WriteAllText(Path.Combine(solutionPath, "tests.slnx"), "<Solution />");
            foreach (var project in projects.Keys)
            {
                var projectPath = Path.Combine(solutionPath, project);
                Directory.CreateDirectory(Path.GetDirectoryName(projectPath)!);
                File.WriteAllText(projectPath, "<Project />");
            }

            var testProjects = projects.Keys
                .Where(project => project.Replace('\\', '/').StartsWith("tests/", StringComparison.Ordinal))
                .ToArray();
            var runner = new FakeCommandRunner(
                root,
                solutionPath,
                projects,
                diff,
                untracked
            );
            return new Fixture(root, solutionPath, runner, testProjects);
        }

        public async Task<ChangedTestsImpactAnalyzer.ImpactAnalysis> AnalyzeAsync()
        {
            var analyzer = new ChangedTestsImpactAnalyzer(
                Runner,
                new McpOptions { WorkingDirectory = Root }
            );
            return await analyzer.AnalyzeAsync("HEAD", Root, CancellationToken.None);
        }

        public RunChangedTestsTool CreateTool()
        {
            var options = Options.Create(
                new McpOptions { WorkingDirectory = Root, DisableCtrf = true }
            );
            var runTestsTool = new RunTestsTool(
                Runner,
                new JsonSerializerOptions(),
                options,
                new TestRunStore()
            );
            return new RunChangedTestsTool(Runner, options, runTestsTool);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }

    private sealed record ProjectFixture(
        string[] ProjectReferences,
        string[] CompileFiles,
        string TargetFrameworks = "net10.0",
        IReadOnlyDictionary<string, string?>? CompileLinks = null
    )
    {
        public static ProjectFixture Source(string compileFile, string targetFrameworks = "net10.0") =>
            new([], [compileFile], targetFrameworks);

        public static ProjectFixture Project(string[] references, string compileFile) =>
            new(references, [compileFile]);
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        private readonly string _root;
        private readonly string _solutionDirectory;
        private readonly IReadOnlyDictionary<string, ProjectFixture> _projects;
        private readonly HashSet<string> _graphFailures = new(StringComparer.Ordinal);
        private string _diff;
        private string _untracked;

        public FakeCommandRunner(
            string root,
            string solutionDirectory,
            IReadOnlyDictionary<string, ProjectFixture> projects,
            string diff,
            string untracked
        )
        {
            _root = root;
            _solutionDirectory = solutionDirectory;
            _projects = projects;
            _diff = diff;
            _untracked = untracked;
        }

        public List<CommandRequest> Requests { get; } = [];
        public bool InvalidBase { get; set; }

        public void SetDiff(string diff) => _diff = diff;

        public void SetUntracked(string untracked) => _untracked = untracked;

        public void FailGraphFor(string project) => _graphFailures.Add(project);

        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            if (request.FileName == "git")
                return Task.FromResult(RunGit(request));
            if (request.FileName == "dotnet")
                return Task.FromResult(RunDotnet(request));
            return Task.FromResult(Result(1, "", "unexpected command"));
        }

        private CommandResult RunGit(CommandRequest request)
        {
            if (request.Arguments.SequenceEqual(["rev-parse", "--show-toplevel"]))
                return Result(0, _root + "\n", "");
            if (request.Arguments.Contains("--end-of-options", StringComparer.Ordinal))
                return InvalidBase ? Result(128, "", "bad revision") : Result(0, "0123456789abcdef\n", "");
            if (request.Arguments.Contains("diff", StringComparer.Ordinal))
                return Result(0, _diff, "");
            if (request.Arguments.Contains("ls-files", StringComparer.Ordinal))
                return Result(0, _untracked, "");
            return Result(1, "", "unexpected git command");
        }

        private CommandResult RunDotnet(CommandRequest request)
        {
            if (request.Arguments.SequenceEqual(["sln", "list"]))
            {
                var solutionOutput = "Project(s)\n----------\n" + string.Join("\n", _projects.Keys);
                return Result(0, solutionOutput, "");
            }

            if (request.Arguments.Contains("test", StringComparer.Ordinal))
                return Result(0, "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 1 s", "");
            if (request.Arguments.Length < 2 || request.Arguments[0] != "msbuild")
                return Result(1, "", "unexpected dotnet command");

            var project = request.Arguments[1];
            if (!_projects.TryGetValue(project, out var metadata) || _graphFailures.Contains(project))
                return Result(1, "", "MSBuild evaluation failed");

            var isTest = project.Replace('\\', '/').StartsWith("tests/", StringComparison.Ordinal);
            var references = metadata.ProjectReferences.Select(reference =>
            {
                var fullPath = Path.GetFullPath(Path.Combine(_solutionDirectory, reference));
                return new { Identity = reference, EvaluatedInclude = reference, FullPath = fullPath };
            });
            var compile = metadata.CompileFiles.Select(file =>
            {
                var fullPath = Path.GetFullPath(Path.Combine(_solutionDirectory, file));
                var link =
                    metadata.CompileLinks is not null
                    && metadata.CompileLinks.TryGetValue(file, out var configuredLink)
                        ? configuredLink
                        : null;
                return new
                {
                    Identity = file,
                    EvaluatedInclude = file,
                    FullPath = fullPath,
                    Link = link,
                };
            });
            var targetFramework = metadata.TargetFrameworks
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault()
                ?? "net10.0";
            var packageReferences = isTest
                ? new object[]
                {
                    new
                    {
                        Identity = "xunit.runner.visualstudio",
                        EvaluatedInclude = "xunit.runner.visualstudio",
                    },
                }
                : Array.Empty<object>();
            var output = JsonSerializer.Serialize(
                new
                {
                    Properties = new
                    {
                        IsTestProject = isTest ? "true" : "false",
                        IsTestingPlatformApplication = "true",
                        OutputType = "Library",
                        TargetFramework = targetFramework,
                        TargetFrameworks = metadata.TargetFrameworks,
                    },
                    Items = new
                    {
                        PackageReference = packageReferences,
                        ProjectReference = references,
                        Compile = compile,
                    },
                }
            );
            return Result(0, output, "");
        }

        private static CommandResult Result(int exitCode, string stdout, string stderr) =>
            new(
                exitCode,
                stdout,
                stderr,
                stdout.Split('\n', StringSplitOptions.None),
                stderr.Split('\n', StringSplitOptions.None),
                TimeSpan.Zero
            );

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
