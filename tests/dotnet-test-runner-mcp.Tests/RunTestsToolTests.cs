using System.Text.Json;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class RunTestsToolTests
{
    [Fact]
    public async Task RunTests_WhenLaterSelectionIsInvalid_DoesNotExecuteTests()
    {
        var projectPath = CreateProject("Microsoft.NET.Test.Sdk");
        try
        {
            var runner = new FakeCommandRunner
            {
                DiscoveryOutput = "The following Tests are available:\nNs.Tests.Good\n",
                ExecutionOutput = "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 1 s",
            };
            var tool = CreateTool(runner);

            var result = await tool.RunTests(
                [
                    new TestSelection(projectPath, "Ns.Tests.Good"),
                    new TestSelection(projectPath, "Ns.Tests.Missing"),
                ]
            );

            Assert.Equal(TestOutcome.Error, result.Outcome);
            Assert.NotEmpty(result.UnmatchedSelections);
            Assert.DoesNotContain(
                runner.Requests,
                request => request.Arguments.Contains("--filter", StringComparer.Ordinal)
            );
        }
        finally
        {
            File.Delete(projectPath);
        }
    }

    [Fact]
    public async Task RunTests_DeduplicatesWholeProjectSelectionsAndPreservesProjectOrder()
    {
        var firstProject = CreateProject("Microsoft.NET.Test.Sdk");
        var secondProject = CreateProject("Microsoft.NET.Test.Sdk");
        try
        {
            var runner = new FakeCommandRunner
            {
                ExecutionOutput = "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 1 s",
            };
            var result = await CreateTool(runner).RunTests(
                [
                    new TestSelection(firstProject),
                    new TestSelection(firstProject),
                    new TestSelection(secondProject),
                ]
            );

            Assert.Equal(4, result.TestCount);
            var executionRequests = runner.Requests
                .Where(IsNativeExecution)
                .ToArray();
            Assert.Equal(2, executionRequests.Length);
            Assert.Contains(firstProject, executionRequests[0].Arguments);
            Assert.Contains(secondProject, executionRequests[1].Arguments);
            Assert.True(result.Selections[1].Deduplicated);
            Assert.Equal(2, result.Selections[0].TestCount!.Value);
            Assert.Equal(2, result.ProjectExecutions.Length);
        }
        finally
        {
            File.Delete(firstProject);
            File.Delete(secondProject);
        }
    }
    [Fact]
    public async Task RunTests_RejectsWholeProjectAndMethodOverlap()
    {
        var projectPath = CreateProject("Microsoft.NET.Test.Sdk");
        try
        {
            var runner = new FakeCommandRunner
            {
                DiscoveryOutput = "The following Tests are available:\nNs.Tests.Good\n",
            };
            var result = await CreateTool(runner).RunTests(
                [
                    new TestSelection(projectPath),
                    new TestSelection(projectPath, "Ns.Tests.Good"),
                ]
            );

            Assert.Equal(TestOutcome.Error, result.Outcome);
            Assert.Contains(
                result.UnmatchedSelections,
                issue => issue.Reason.Contains("overlaps", StringComparison.Ordinal)
            );
            Assert.DoesNotContain(runner.Requests, request => IsNativeExecution(request));
        }
        finally
        {
            File.Delete(projectPath);
        }
    }


    [Fact]
    public async Task RunTests_UsesExactVSTestFilterAndDoesNotInventFailureDetailsFromSummary()
    {
        var projectPath = CreateProject("Microsoft.NET.Test.Sdk");
        try
        {
            var runner = new FakeCommandRunner
            {
                DiscoveryOutput =
                    "The following Tests are available:\nNs.Tests.Foo\nNs.Tests.FooOther\n",
                ExecutionOutput = "Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1, Duration: 1 s",
                ExecutionExitCode = 1,
            };
            var result = await CreateTool(runner).RunTests(
                [new TestSelection(projectPath, "Ns.Tests.Foo")]
            );

            Assert.Equal(TestOutcome.Failed, result.Outcome);
            Assert.Equal(1, result.Failed);
            Assert.Empty(result.FailureIds);
            Assert.False(result.FailureDetailsAvailable);
            var execution = Assert.Single(
                runner.Requests.Where(request => request.Arguments.Contains("--filter", StringComparer.Ordinal))
            );
            Assert.Contains("FullyQualifiedName=Ns.Tests.Foo", execution.Arguments);
            Assert.DoesNotContain("FullyQualifiedName~Ns.Tests.Foo", execution.Arguments);
        }
        finally
        {
            File.Delete(projectPath);
        }
    }

    [Fact]
    public async Task RunTests_MixedFoundAndNotFoundSelectionsArePartial()
    {
        var foundProject = CreateProject("Microsoft.NET.Test.Sdk");
        var missingTestsProject = CreateProject("Microsoft.NET.Test.Sdk");
        try
        {
            var runner = new FakeCommandRunner
            {
                ExecutionResultFactory = request =>
                    request.Arguments.Contains(missingTestsProject, StringComparer.Ordinal)
                        ? ("No tests available.", 8)
                        : ("Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 1 s", 0),
            };
            var result = await CreateTool(runner).RunTests(
                [new TestSelection(foundProject), new TestSelection(missingTestsProject)]
            );

            Assert.Equal(TestOutcome.Partial, result.Outcome);
            Assert.Equal(1, result.Passed);
            Assert.Equal(2, result.Selections.Length);
        }
        finally
        {
            File.Delete(foundProject);
            File.Delete(missingTestsProject);
        }
    }

    [Fact]
    public async Task RunTests_RequiresAmbiguityOptInForRepeatedDiscoveredCases()
    {
        var projectPath = CreateProject("Microsoft.NET.Test.Sdk");
        try
        {
            var runner = new FakeCommandRunner
            {
                DiscoveryOutput =
                    "The following Tests are available:\nNs.Tests.Param\nNs.Tests.Param\n",
                ExecutionOutput = "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 1 s",
            };
            var tool = CreateTool(runner);

            var rejected = await tool.RunTests(
                [new TestSelection(projectPath, "Ns.Tests.Param")]
            );
            Assert.Equal(TestOutcome.Error, rejected.Outcome);
            Assert.Equal(TestOutcome.Ambiguous, Assert.Single(rejected.UnmatchedSelections).Outcome);
            Assert.DoesNotContain(
                runner.Requests,
                request => IsNativeExecution(request)
            );

            var accepted = await tool.RunTests(
                [new TestSelection(projectPath, "Ns.Tests.Param")],
                allowMultipleMatches: true
            );
            Assert.Equal(TestOutcome.Passed, accepted.Outcome);
            Assert.Equal(2, accepted.TestCount);
            Assert.Contains(
                runner.Requests,
                request =>
                    request.Arguments.Contains("--filter", StringComparer.Ordinal)
                    && request.Arguments.Contains("FullyQualifiedName=Ns.Tests.Param", StringComparer.Ordinal)
            );
        }
        finally
        {
            File.Delete(projectPath);
        }
    }
    [Fact]
    public async Task RunTests_RequiresExplicitTUnitAmbiguityOptIn()
    {
        var projectPath = CreateProject("TUnit");
        try
        {
            var runner = new FakeCommandRunner
            {
                DiscoveryOutput = "The following Tests are available:\nGood\n",
            };
            var tool = CreateTool(runner);

            var rejected = await tool.RunTests([new TestSelection(projectPath, "Good")]);
            Assert.Equal(TestOutcome.Error, rejected.Outcome);
            Assert.Equal(TestOutcome.Ambiguous, Assert.Single(rejected.UnmatchedSelections).Outcome);
            Assert.DoesNotContain(runner.Requests, request => IsNativeExecution(request));

            var accepted = await tool.RunTests(
                [new TestSelection(projectPath, "Good")],
                allowMultipleMatches: true
            );
            Assert.Equal(TestOutcome.Passed, accepted.Outcome);
            Assert.Contains(
                runner.Requests,
                request =>
                    IsNativeExecution(request)
                    && request.Arguments.Contains(
                        "/*/*/*/Good",
                        StringComparer.Ordinal
                    )
            );
        }
        finally
        {
            File.Delete(projectPath);
        }
    }


    [Fact]
    public async Task RunTests_RejectsDiscoveryErrorEvenWhenNamesWereParsed()
    {
        var projectPath = CreateProject("Microsoft.NET.Test.Sdk");
        try
        {
            var runner = new FakeCommandRunner
            {
                DiscoveryOutput = "The following Tests are available:\nNs.Tests.Good\n",
                DiscoveryExitCode = 1,
            };

            var result = await CreateTool(runner).RunTests(
                [new TestSelection(projectPath, "Ns.Tests.Good")]
            );

            Assert.Equal(TestOutcome.Error, result.Outcome);
            Assert.Contains(result.UnmatchedSelections, issue => issue.Outcome == TestOutcome.Error);
            Assert.DoesNotContain(
                runner.Requests,
                request => IsNativeExecution(request)
            );
        }
        finally
        {
            File.Delete(projectPath);
        }
    }

    [Fact]
    public async Task RunTests_UsesOneNativeUnionPerProjectAndLeavesPerSelectionCountsUnknown()
    {
        var projectPath = CreateProject("Microsoft.NET.Test.Sdk");
        try
        {
            var runner = new FakeCommandRunner
            {
                DiscoveryOutput =
                    "The following Tests are available:\nNs.Tests.First\nNs.Tests.Second\nNs.Tests.Other\n",
                ExecutionOutput = "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 1 s",
            };

            var result = await CreateTool(runner).RunTests(
                [
                    new TestSelection(projectPath, "Ns.Tests.First"),
                    new TestSelection(projectPath, "Ns.Tests.Second"),
                ]
            );

            var executionRequests = runner.Requests
                .Where(IsNativeExecution)
                .ToArray();
            Assert.Single(executionRequests);
            Assert.Contains(
                "FullyQualifiedName=Ns.Tests.First|FullyQualifiedName=Ns.Tests.Second",
                executionRequests[0].Arguments
            );
            Assert.DoesNotContain("Ns.Tests.Other", executionRequests[0].Arguments);
            Assert.Equal(2, result.TestCount);
            var projectExecution = Assert.Single(result.ProjectExecutions);
            Assert.True(projectExecution.CountsKnown);
            Assert.Equal(2, projectExecution.TestCount);
            Assert.Equal(
                new[] { "Ns.Tests.First", "Ns.Tests.Second" },
                projectExecution.NativeSelectors
            );
            Assert.All(result.Selections, selection =>
            {
                Assert.Null(selection.TestCount);
                Assert.False(selection.Deduplicated);
            });
        }
        finally
        {
            File.Delete(projectPath);
        }
    }

    [Fact]
    public async Task RunTests_DeduplicatesAliasSelectionsIntoOneNativeExecution()
    {
        var projectPath = CreateProject("Microsoft.NET.Test.Sdk");
        try
        {
            var runner = new FakeCommandRunner
            {
                DiscoveryOutput = "The following Tests are available:\nNs.Tests.First(1)\n",
            };

            var result = await CreateTool(runner).RunTests(
                [
                    new TestSelection(projectPath, "Ns.Tests.First"),
                    new TestSelection(projectPath, "Ns.Tests.First(1)"),
                ],
                allowMultipleMatches: true
            );

            Assert.Single(
                runner.Requests.Where(request =>
                    IsNativeExecution(request)
                )
            );
            Assert.True(result.Selections[1].Deduplicated);
            Assert.True(result.Selections[0].CountsKnown);
            Assert.False(result.Selections[1].CountsKnown);
        }
        finally
        {
            File.Delete(projectPath);
        }
    }

    [Fact]
    public async Task RunTests_BoundsCumulativeOutputAndPropagatesTruncation()
    {
        var firstProject = CreateProject("Microsoft.NET.Test.Sdk");
        var secondProject = CreateProject("Microsoft.NET.Test.Sdk");
        var store = new TestRunStore(
            new TestTimeProvider(DateTimeOffset.UtcNow),
            maximumCharacters: 80
        );
        try
        {
            var runner = new FakeCommandRunner
            {
                ExecutionOutput = new string('x', 200),
            };

            var result = await CreateTool(runner, store).RunTests(
                [new TestSelection(firstProject), new TestSelection(secondProject)]
            );

            Assert.True(result.CapturedOutputAvailable);
            var details = new GetFailureDetailsTool(store).GetFailureDetails(
                result.RunId,
                includeOutput: true,
                maxCharacters: 1_000
            );
            Assert.True(details.RunOutput?.StandardOutput?.Truncated == true);
            Assert.True(details.RunOutput?.StandardOutput?.OriginalCharCount is > 200);
        }
        finally
        {
            File.Delete(firstProject);
            File.Delete(secondProject);
        }
    }

    [Fact]
    public async Task RunTests_CancellationStopsBeforeTheNextPlan()
    {
        var firstProject = CreateProject("Microsoft.NET.Test.Sdk");
        var secondProject = CreateProject("Microsoft.NET.Test.Sdk");
        using var cancellation = new CancellationTokenSource();
        try
        {
            var runner = new FakeCommandRunner
            {
                ExecutionOutput = "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 1 s",
                OnExecution = _ => cancellation.Cancel(),
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CreateTool(runner).RunTests(
                    [new TestSelection(firstProject), new TestSelection(secondProject)],
                    cancellationToken: cancellation.Token
                )
            );
            Assert.Single(
                runner.Requests.Where(request =>
                    IsNativeExecution(request)
                )
            );
        }
        finally
        {
            File.Delete(firstProject);
            File.Delete(secondProject);
        }
    }


    [Fact]
    public async Task RunTests_WhenCtrfContainsFailure_RetainsDetailsForGetFailureDetails()
    {
        var projectPath = CreateProject(package: null);
        var store = new TestRunStore();
        try
        {
            var runner = new FakeCommandRunner
            {
                MetadataIsMtp = true,
                ExecutionOutput = string.Empty,
                CtrfReport =
                    "{\"results\":{\"summary\":{\"tests\":1,\"failed\":1},\"tests\":["
                    + "{\"name\":\"Ns.Tests.Failing\",\"status\":\"failed\","
                    + "\"message\":\"boom\",\"trace\":\"trace\","
                    + "\"stdout\":[\"out\"],\"stderr\":[\"err\"]}]}}",
                ExecutionExitCode = 1,
            };
            var runResult = await CreateTool(runner, store).RunTests(
                [new TestSelection(projectPath)]
            );
            Assert.Single(runner.Requests.Where(IsNativeExecution));
            var failureId = Assert.Single(runResult.FailureIds);
            var compactDetails = new GetFailureDetailsTool(store).GetFailureDetails(
                runResult.RunId,
                failureId
            );
            var compactFailure = Assert.Single(compactDetails.Failures);
            Assert.Equal("boom", compactFailure.Message?.Text);
            Assert.Null(compactFailure.StackTrace);
            Assert.Null(compactFailure.StandardOutput);
            Assert.Null(compactFailure.StandardError);
            Assert.Null(compactDetails.RunOutput);
            Assert.True(compactFailure.StackTraceAvailable);
            Assert.True(compactFailure.OutputAvailable);

            var details = new GetFailureDetailsTool(store).GetFailureDetails(
                runResult.RunId,
                failureId,
                includeStackTrace: true,
                includeOutput: true
            );

            Assert.True(details.Found);
            var failure = Assert.Single(details.Failures);
            Assert.Equal("boom", failure.Message?.Text);
            Assert.Equal("trace", failure.StackTrace?.Text);
            Assert.Equal("out", failure.StandardOutput?.Text);
            Assert.Equal("err", failure.StandardError?.Text);
        }
        finally
        {
            File.Delete(projectPath);
        }
    }

    [Fact]
    public async Task RunTests_WhenRetentionDropsFailures_ReturnsOnlyBoundedRetrievableSelectionIds()
    {
        var firstProject = CreateProject(package: null);
        var secondProject = CreateProject(package: null);
        var store = new TestRunStore();
        try
        {
            var runner = new FakeCommandRunner
            {
                MetadataIsMtp = true,
                ExecutionOutput = string.Empty,
                CtrfReport = JsonSerializer.Serialize(
                    new
                    {
                        results = new
                        {
                            summary = new { tests = 250, failed = 250 },
                            tests = Enumerable
                                .Range(1, 250)
                                .Select(index => new
                                {
                                    name = $"Namespace.Tests.Failure{index}",
                                    status = "failed",
                                    message = "boom",
                                })
                                .ToArray(),
                        },
                    }
                ),
                ExecutionExitCode = 1,
            };
            var result = await CreateTool(runner, store).RunTests(
                [new TestSelection(firstProject), new TestSelection(secondProject)]
            );
            Assert.Equal(
                2,
                runner.Requests.Count(request => IsNativeExecution(request))
            );

            Assert.Equal(500, result.Failed);
            Assert.Equal(500, result.OriginalFailureCount);
            Assert.Equal(200, result.RetainedFailureCount);
            Assert.True(result.HasMoreFailures);
            Assert.True(result.FailuresTruncated);
            Assert.Equal(100, result.FailureIds.Length);
            var retainedSelection = Assert.Single(
                result.Selections,
                selection => selection.FailureDetailsAvailable
            );
            Assert.Equal(100, retainedSelection.FailureIds.Length);
            var droppedSelection = Assert.Single(
                result.Selections,
                selection => !selection.FailureDetailsAvailable
            );
            Assert.Empty(droppedSelection.FailureIds);
            var retrieval = new GetFailureDetailsTool(store);
            Assert.All(retainedSelection.FailureIds, id =>
                Assert.Single(retrieval.GetFailureDetails(result.RunId, id).Failures)
            );
        }
        finally
        {
            File.Delete(firstProject);
            File.Delete(secondProject);
        }
    }

    [Fact]
    public void TestRunStore_ExpiresEvictsAndTruncatesDeterministically()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var store = new TestRunStore(
            clock,
            maximumRuns: 2,
            maximumAge: TimeSpan.FromMinutes(1),
            maximumCharacters: 20
        );
        store.Store(
            new TestRunStore.RunRecord(
                "one",
                default,
                [new TestRunStore.FailureRecord("f1", "test", null, "0123456789", null, null, null)],
                new()
            )
        );
        clock.Advance(TimeSpan.FromSeconds(30));
        store.Store(new TestRunStore.RunRecord("two"));
        store.Store(new TestRunStore.RunRecord("three"));

        Assert.False(store.TryGet("one", out _));
        Assert.Equal(2, store.Count);
        Assert.True(store.TryGet("two", out _));
        Assert.True(store.TryGet("three", out _));

        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.False(store.TryGet("two", out _));
        Assert.False(store.TryGet("three", out _));
    }

    [Fact]
    public void TestRunStore_ReservesFailureIdsBeforeLargePayloadsAndCapsFailureCount()
    {
        var store = new TestRunStore(
            new TestTimeProvider(DateTimeOffset.UtcNow),
            maximumCharacters: 80
        );
        store.Store(
            new TestRunStore.RunRecord(
                "payload",
                default,
                [
                    new TestRunStore.FailureRecord(
                        "first",
                        "first-test",
                        null,
                        null,
                        new string('x', 1_000),
                        null,
                        null
                    ),
                    new TestRunStore.FailureRecord(
                        "second",
                        "second-test",
                        null,
                        null,
                        null,
                        null,
                        null
                    ),
                ],
                new()
            )
        );

        Assert.True(store.TryGet("payload", out var retained));
        Assert.Equal(["first", "second"], retained.Failures.Select(value => value.FailureId));
        Assert.True(retained.Failures[0].TraceTruncated);

        var manyFailures = Enumerable
            .Range(1, 500)
            .Select(index => new TestRunStore.FailureRecord(
                $"failure-{index}",
                $"test-{index}",
                null,
                null,
                null,
                null,
                null
            ))
            .ToArray();
        store.Store(
            new TestRunStore.RunRecord("many", default, manyFailures, new())
            {
                OriginalFailureCount = manyFailures.Length,
            }
        );
        Assert.True(store.TryGet("many", out var bounded));
        Assert.True(bounded.Failures.Length <= 200);
        Assert.True(bounded.FailuresTruncated);
        Assert.Equal(500, bounded.OriginalFailureCount);
    }

    [Fact]
    public void GetFailureDetails_MissingRunIsExplicit()
    {
        var result = new GetFailureDetailsTool(new TestRunStore()).GetFailureDetails("expired");

        Assert.False(result.Found);
        Assert.Equal(TestOutcome.Error, result.Outcome);
        Assert.Contains("not found or has expired", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetFailureDetails_UsesOneTotalTextBudgetAcrossFailures()
    {
        var store = new TestRunStore();
        store.Store(
            new TestRunStore.RunRecord(
                "budget",
                default,
                [
                    new TestRunStore.FailureRecord(
                        "f1",
                        "test1",
                        null,
                        new string('m', 20),
                        new string('t', 20),
                        new string('o', 20),
                        new string('e', 20)
                    ),
                    new TestRunStore.FailureRecord(
                        "f2",
                        "test2",
                        null,
                        new string('m', 20),
                        new string('t', 20),
                        new string('o', 20),
                        new string('e', 20)
                    ),
                ],
                new TestRunStore.CapturedOutput("run-output", "run-error")
            )
        );

        var result = new GetFailureDetailsTool(store).GetFailureDetails(
            "budget",
            includeStackTrace: true,
            includeOutput: true,
            maxCharacters: 25
        );
        var textLength = result.Failures
            .SelectMany(failure => new[]
            {
                failure.Message?.Text,
                failure.StackTrace?.Text,
                failure.StandardOutput?.Text,
                failure.StandardError?.Text,
            })
            .Concat([result.RunOutput?.StandardOutput?.Text, result.RunOutput?.StandardError?.Text])
            .Where(text => text is not null)
            .Sum(text => text!.Length);

        Assert.Equal(25, textLength);
        Assert.Equal(new string('m', 20), result.Failures[0].Message?.Text);
        Assert.Equal(new string('t', 5), result.Failures[0].StackTrace?.Text);
        Assert.True(result.Failures[0].StackTrace?.Truncated);
        Assert.True(result.Failures[1].Message?.Truncated);
    }


    [Theory]
    [InlineData("Microsoft.NET.Test.Sdk", "Ns.Tests.Good", "Ns.Tests.Bad|FullyQualifiedName~Ns.Tests.Other")]
    [InlineData("TUnit", "Good", "Bad*")]
    public async Task RunTests_WhenLaterSelectorHasNativeFilterSyntax_ExecutesNothing(
        string package,
        string goodName,
        string unsafeName
    )
    {
        var project = CreateProject(package);
        try
        {
            var runner = new FakeCommandRunner
            {
                DiscoveryOutput = $"The following Tests are available:\n{goodName}\n{unsafeName}\n",
            };
            var result = await CreateTool(runner).RunTests(
                [new TestSelection(project, goodName), new TestSelection(project, unsafeName)],
                allowMultipleMatches: true
            );

            Assert.Equal(TestOutcome.Error, result.Outcome);
            Assert.Equal(0, result.TestCount);
            Assert.Contains(result.UnmatchedSelections, issue => issue.Selection.TestName == unsafeName);
            Assert.DoesNotContain(runner.Requests, request =>
                IsNativeExecution(request));
        }
        finally
        {
            File.Delete(project);
        }
    }

    private static bool IsNativeExecution(CommandRequest request) =>
        request.Arguments.Length > 0
        && !string.Equals(request.Arguments[0], "msbuild", StringComparison.Ordinal)
        && !request.Arguments.Contains("--list-tests", StringComparer.Ordinal);

    private static RunTestsTool CreateTool(
        FakeCommandRunner runner,
        TestRunStore? store = null
    ) =>
        new(
            runner,
            new JsonSerializerOptions(),
            Options.Create(new McpOptions()),
            store ?? new TestRunStore()
        );

    private static string CreateProject(string? package)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csproj");
        var packageItem = package is null
            ? string.Empty
            : $"<PackageReference Include=\"{package}\" />";
        File.WriteAllText(path, $"<Project><ItemGroup>{packageItem}</ItemGroup></Project>");
        return path;
    }

    private sealed class FakeCommandRunner : ICommandRunner
    {
        public List<CommandRequest> Requests { get; } = [];

        public string DiscoveryOutput { get; init; } = string.Empty;

        public int DiscoveryExitCode { get; init; }

        public bool MetadataIsMtp { get; init; }

        public string ExecutionOutput { get; init; } = "Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 1 s";

        public int ExecutionExitCode { get; init; }
        public Func<CommandRequest, (string Output, int ExitCode)>? ExecutionResultFactory { get; init; }

        public string? CtrfReport { get; init; }
        public Action<CommandRequest>? OnExecution { get; init; }

        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            if (
                request.Arguments.Length > 0
                && string.Equals(request.Arguments[0], "msbuild", StringComparison.Ordinal)
            )
            {
                var projectPath = request.Arguments.ElementAtOrDefault(1);
                var projectXml =
                    projectPath is not null && File.Exists(projectPath)
                        ? File.ReadAllText(projectPath)
                        : string.Empty;
                var isMtp = MetadataIsMtp;
                var package = projectXml.Contains("TUnit", StringComparison.OrdinalIgnoreCase)
                    ? "TUnit"
                    : projectXml.Contains(
                        "Microsoft.NET.Test.Sdk",
                        StringComparison.OrdinalIgnoreCase
                    )
                        ? "Microsoft.NET.Test.Sdk"
                        : string.Empty;
                var metadata = JsonSerializer.Serialize(
                    new
                    {
                        Properties = new
                        {
                            IsTestProject = "true",
                            IsTestingPlatformApplication = isMtp || package == "TUnit" ? "true" : "false",
                            OutputType = isMtp || package == "TUnit" ? "Exe" : "Library",
                            TargetFramework = "net10.0",
                            TargetFrameworks = "",
                        },
                        Items = new
                        {
                            PackageReference = package.Length == 0
                                ? Array.Empty<object>()
                                : new object[] { new { Identity = package, EvaluatedInclude = package } },
                            ProjectReference = Array.Empty<object>(),
                            Compile = Array.Empty<object>(),
                            None = Array.Empty<object>(),
                        },
                    }
                );
                return Task.FromResult(CreateResult(request, metadata, 0));
            }

            if (request.Arguments.Contains("--list-tests", StringComparer.Ordinal))
                return Task.FromResult(CreateResult(request, DiscoveryOutput, DiscoveryExitCode));

            if (CtrfReport is not null)
            {
                var fileNameIndex = Array.IndexOf(request.Arguments, "--report-ctrf-filename");
                var directoryIndex = Array.IndexOf(request.Arguments, "--results-directory");
                if (fileNameIndex >= 0 && directoryIndex >= 0)
                {
                    var path = Path.Combine(
                        request.Arguments[directoryIndex + 1],
                        request.Arguments[fileNameIndex + 1]
                    );
                    File.WriteAllText(path, CtrfReport);
                }
            }

            OnExecution?.Invoke(request);
            var execution = ExecutionResultFactory?.Invoke(request)
                ?? (ExecutionOutput, ExecutionExitCode);
            return Task.FromResult(CreateResult(request, execution.Output, execution.ExitCode));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static CommandResult CreateResult(
            CommandRequest request,
            string output,
            int exitCode
        ) => new(
            exitCode,
            output,
            string.Empty,
            output.Split('\n'),
            [],
            TimeSpan.Zero
        )
        {
            FileName = request.FileName,
            Arguments = request.Arguments,
            WorkingDirectory = request.WorkingDirectory,
        };
    }

    private sealed class TestTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Advance(TimeSpan duration) => _current += duration;
    }
}
