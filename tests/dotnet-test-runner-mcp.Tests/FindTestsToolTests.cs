using System.Text.Json;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class FindTestsToolTests
{
    [Fact]
    public async Task FindTests_PreservesCrossProjectNamesAndParameterizedDisplays()
    {
        using var fixture = new Fixture();
        var first = fixture.AddProject(
            "First",
            "Microsoft.NET.Test.Sdk",
            ["Shared.Tests.Run(plain value)", "Shared.Tests.Run(spaced value)"]
        );
        var second = fixture.AddProject(
            "Second",
            "Microsoft.NET.Test.Sdk",
            ["Shared.Tests.Run(plain value)"]
        );

        var result = await fixture.Tool.FindTests(query: "shared.tests.run", workingDirectory: fixture.Root);

        Assert.Equal(TestOutcome.Passed, result.Outcome);
        Assert.Equal(3, result.TotalMatches);
        Assert.Equal(3, result.ReturnedCount);
        Assert.Equal(
            new[] { first, first, second },
            result.Tests.Select(test => test.ProjectPath)
        );
        Assert.Equal(
            new[]
            {
                "Shared.Tests.Run(plain value)",
                "Shared.Tests.Run(spaced value)",
                "Shared.Tests.Run(plain value)",
            },
            result.Tests.Select(test => test.DisplayName)
        );
        Assert.All(result.Tests.Take(2), test =>
        {
            Assert.Equal("Shared.Tests.Run", test.Selector);
            Assert.True(test.IsAmbiguous);
            Assert.True(test.SelectorSupported);
        });
        Assert.True(result.Tests[2].IsAmbiguous);
    }

    [Fact]
    public async Task FindTests_DoesNotMarkSameSelectorAmbiguousAcrossProjects()
    {
        using var fixture = new Fixture();
        var first = fixture.AddProject(
            "First",
            "Microsoft.NET.Test.Sdk",
            ["Shared.Tests.Run"]
        );
        var second = fixture.AddProject(
            "Second",
            "Microsoft.NET.Test.Sdk",
            ["Shared.Tests.Run"]
        );

        var result = await fixture.Tool.FindTests(
            query: "shared.tests.run",
            workingDirectory: fixture.Root
        );

        Assert.Equal(2, result.TotalMatches);
        Assert.Equal(new[] { first, second }, result.Tests.Select(test => test.ProjectPath));
        Assert.All(result.Tests, test =>
        {
            Assert.Equal("Shared.Tests.Run", test.Selector);
            Assert.False(test.IsAmbiguous);
        });
    }

    [Fact]
    public async Task FindTests_FiltersQueryAndProjectWithoutBroadeningMatches()
    {
        using var fixture = new Fixture();
        var first = fixture.AddProject(
            "First",
            "Microsoft.NET.Test.Sdk",
            ["First.Tests.ShouldRun", "First.Tests.ShouldSkip"]
        );
        fixture.AddProject(
            "Second",
            "Microsoft.NET.Test.Sdk",
            ["Second.Tests.ShouldRun"]
        );

        var result = await fixture.Tool.FindTests(
            query: "shouldrun",
            projectPath: first,
            workingDirectory: fixture.Root
        );

        Assert.Equal(TestOutcome.Passed, result.Outcome);
        Assert.Equal(1, result.TotalMatches);
        var test = Assert.Single(result.Tests);
        Assert.Equal(first, test.ProjectPath);
        Assert.Equal("First.Tests.ShouldRun", test.DisplayName);
    }

    [Fact]
    public async Task FindTests_PagesCompleteCatalogWithoutRepeats()
    {
        using var fixture = new Fixture();
        var expected = Enumerable
            .Range(1, 205)
            .Select(index => $"Large.Tests.Case{index:D3}")
            .ToArray();
        fixture.AddProject("Large", "Microsoft.NET.Test.Sdk", expected);
        var bounded = await fixture.Tool.FindTests(
            pageSize: 500,
            workingDirectory: fixture.Root
        );
        Assert.Equal(200, bounded.PageSize);
        Assert.Equal(200, bounded.ReturnedCount);
        Assert.True(bounded.HasMore);

        var collected = new List<string>();
        string? cursor = null;
        do
        {
            var page = await fixture.Tool.FindTests(
                pageSize: 37,
                cursor: cursor,
                workingDirectory: fixture.Root
            );
            Assert.Equal(TestOutcome.Passed, page.Outcome);
            collected.AddRange(page.Tests.Select(test => test.DisplayName));
            cursor = page.NextCursor;
            if (page.HasMore)
                Assert.NotNull(cursor);
            else
                Assert.Null(cursor);
        } while (cursor is not null);

        Assert.Equal(expected.OrderBy(value => value, StringComparer.Ordinal), collected);
        Assert.Equal(collected.Count, collected.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task FindTests_RejectsInvalidMismatchedAndRetainsFixedSnapshotCursors()
    {
        using var fixture = new Fixture();
        var project = fixture.AddProject(
            "Cursor",
            "Microsoft.NET.Test.Sdk",
            ["Cursor.Tests.One", "Cursor.Tests.Two", "Cursor.Tests.Three"]
        );

        var first = await fixture.Tool.FindTests(pageSize: 1, projectPath: project);
        Assert.Equal(Path.GetFullPath(fixture.Root), first.WorkingDirectory!);
        Assert.NotNull(first.NextCursor);
        first.ProjectResults[0] = first.ProjectResults[0] with { Message = "Mutated externally" };
        var cursor = first.NextCursor!;
        var explicitWorkingDirectory = await fixture.Tool.FindTests(
            pageSize: 1,
            cursor: cursor,
            projectPath: project,
            workingDirectory: fixture.Root
        );
        Assert.NotEqual(TestOutcome.Error, explicitWorkingDirectory.Outcome);

        var requestCountBeforeInvalid = fixture.RequestCount;
        var invalid = await fixture.Tool.FindTests(
            cursor: "not-a-valid-cursor",
            workingDirectory: fixture.Root
        );
        Assert.Equal(TestOutcome.Error, invalid.Outcome);
        Assert.Contains("cursor", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(requestCountBeforeInvalid, fixture.RequestCount);

        var mismatched = await fixture.Tool.FindTests(
            query: "different",
            cursor: cursor,
            projectPath: project,
            workingDirectory: fixture.Root
        );
        Assert.Equal(TestOutcome.Error, mismatched.Outcome);
        Assert.Contains("does not match", mismatched.Message, StringComparison.OrdinalIgnoreCase);

        fixture.SetTests(project, ["Cursor.Tests.One", "Cursor.Tests.Changed"]);
        var fixedSnapshotPage = await fixture.Tool.FindTests(
            pageSize: 1,
            cursor: cursor,
            projectPath: project,
            workingDirectory: fixture.Root
        );
        Assert.NotEqual(TestOutcome.Error, fixedSnapshotPage.Outcome);
        Assert.Equal("Cursor.Tests.Three", Assert.Single(fixedSnapshotPage.Tests).DisplayName);
        Assert.Equal(requestCountBeforeInvalid, fixture.RequestCount);
        Assert.DoesNotContain(
            fixedSnapshotPage.ProjectResults,
            result => result.Message == "Mutated externally"
        );
    }

    [Fact]
    public async Task FindTests_ExpiredCursorReturnsErrorWithoutRediscovery()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        using var fixture = new Fixture(clock, TimeSpan.FromMinutes(1));
        fixture.AddProject(
            "Expiring",
            "Microsoft.NET.Test.Sdk",
            ["Expiring.Tests.One", "Expiring.Tests.Two"]
        );

        var first = await fixture.Tool.FindTests(pageSize: 1, workingDirectory: fixture.Root);
        var cursor = Assert.IsType<string>(first.NextCursor);
        var requestCount = fixture.RequestCount;

        clock.Advance(TimeSpan.FromMinutes(2));
        var expired = await fixture.Tool.FindTests(
            pageSize: 1,
            cursor: cursor,
            workingDirectory: fixture.Root
        );

        Assert.Equal(TestOutcome.Error, expired.Outcome);
        Assert.Contains("expired", expired.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(requestCount, fixture.RequestCount);
    }

    [Fact]
    public async Task FindTests_ReportsPartialDiscoveryErrorsAlongsideMatches()
    {
        using var fixture = new Fixture();
        var good = fixture.AddProject(
            "Good",
            "Microsoft.NET.Test.Sdk",
            ["Good.Tests.Run"]
        );
        var broken = fixture.AddProject(
            "Broken",
            "Microsoft.NET.Test.Sdk",
            ["Broken.Tests.Run"]
        );
        fixture.FailProject(broken);

        var result = await fixture.Tool.FindTests(workingDirectory: fixture.Root);

        Assert.Equal(TestOutcome.Partial, result.Outcome);
        Assert.Single(result.Tests);
        Assert.Equal(good, result.Tests[0].ProjectPath);
        var brokenResult = Assert.Single(
            result.ProjectResults.Where(value => value.ProjectPath == broken)
        );
        Assert.Equal(TestOutcome.Error, brokenResult.Outcome);
        Assert.Contains("simulated", brokenResult.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("partially", result.Message, StringComparison.OrdinalIgnoreCase);
        var noMatch = await fixture.Tool.FindTests(
            query: "does-not-exist",
            workingDirectory: fixture.Root
        );
        Assert.Equal(TestOutcome.Partial, noMatch.Outcome);
        Assert.Empty(noMatch.Tests);
        Assert.Contains("partially", noMatch.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindTests_UnknownDialectDoesNotClaimVerifiedSelectorSupport()
    {
        using var fixture = new Fixture();
        var project = fixture.AddProject("Unknown", null, ["Unknown.Tests.Run"]);

        var result = await fixture.Tool.FindTests(
            query: "unknown.tests.run",
            projectPath: project,
            workingDirectory: fixture.Root
        );

        var test = Assert.Single(result.Tests);
        Assert.Equal("Unknown", test.Dialect);
        Assert.False(test.SelectorSupported);
        Assert.True(test.IsAmbiguous);
        Assert.Contains("unknown", test.Limitation!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FindTests_ShortVSTestIdentityReportsUnsupportedExactSelector()
    {
        using var fixture = new Fixture();
        var project = fixture.AddProject(
            "Short",
            "Microsoft.NET.Test.Sdk",
            ["ShouldWork"]
        );

        var result = await fixture.Tool.FindTests(
            projectPath: project,
            workingDirectory: fixture.Root
        );

        var test = Assert.Single(result.Tests);
        Assert.True(test.IsAmbiguous);
        Assert.False(test.SelectorSupported);
        Assert.NotNull(test.Limitation);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Dictionary<string, string[]> _tests = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _packages = new(StringComparer.Ordinal);
        private readonly HashSet<string> _failingProjects = new(StringComparer.Ordinal);
        private readonly McpOptions _options;
        private readonly FakeCommandRunner _runner;

        internal Fixture(
            TimeProvider? timeProvider = null,
            TimeSpan? snapshotLifetime = null
        )
        {
            Root = Path.Combine(Path.GetTempPath(), $"find-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(Root, "tests"));
            _options = new McpOptions { WorkingDirectory = Root };
            _runner = new FakeCommandRunner(_tests, _packages, _failingProjects);
            var store = new TestDiscoverySnapshotStore(
                timeProvider,
                snapshotLifetime: snapshotLifetime
            );
            Tool = new FindTestsTool(Options.Create(_options), _runner, snapshotStore: store);
        }
        internal int RequestCount => _runner.Requests.Count;
        internal string Root { get; }

        internal FindTestsTool Tool { get; }

        internal string AddProject(string name, string? package, string[] tests)
        {
            var relativePath = $"tests/{name}/{name}.csproj";
            var fullPath = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var packageReference = package is null
                ? string.Empty
                : $"<PackageReference Include=\"{package}\" />";
            File.WriteAllText(fullPath, $"<Project><ItemGroup>{packageReference}</ItemGroup></Project>");
            _tests[relativePath] = tests;
            _packages[relativePath] = package;
            _runner.Projects = _tests.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            return relativePath;
        }

        internal void SetTests(string project, string[] tests) => _tests[project] = tests;

        internal void FailProject(string project) => _failingProjects.Add(project);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FakeCommandRunner(
        IReadOnlyDictionary<string, string[]> tests,
        IReadOnlyDictionary<string, string?> packages,
        IReadOnlySet<string> failingProjects
    ) : ICommandRunner
    {
        internal string[] Projects { get; set; } = [];
        internal List<CommandRequest> Requests { get; } = [];
        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            if (request.Arguments.SequenceEqual(["sln", "list"], StringComparer.Ordinal))
            {
                var output = $"Project(s)\n----------\n{string.Join('\n', Projects)}";
                return Task.FromResult(CreateResult(request, output));
            }

            if (request.Arguments.Length > 0 && request.Arguments[0] == "msbuild")
            {
                var metadataProject = request.Arguments.ElementAtOrDefault(1);
                var package = metadataProject is not null && packages.TryGetValue(metadataProject, out var value)
                    ? value
                    : null;
                return Task.FromResult(CreateResult(request, Metadata(package)));
            }

            var project = request.Arguments.FirstOrDefault(argument => tests.ContainsKey(argument));
            if (project is null)
                throw new InvalidOperationException("No project was supplied to the fake runner.");
            if (failingProjects.Contains(project))
                throw new InvalidOperationException($"simulated discovery failure for {project}");

            return Task.FromResult(
                CreateResult(request, CreateTestOutput(tests[project]))
            );
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

        private static string CreateTestOutput(string[] tests)
        {
            var lines = new List<string>
            {
                "Discovering tests from /repo/bin/Debug/net10.0/Fake.Tests.dll",
                $"Discovered {tests.Length} tests in assembly - /repo/bin/Debug/net10.0/Fake.Tests.dll",
            };
            lines.AddRange(tests.Select(test => $"  {test}"));
            return string.Join('\n', lines);
        }

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
    private sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
