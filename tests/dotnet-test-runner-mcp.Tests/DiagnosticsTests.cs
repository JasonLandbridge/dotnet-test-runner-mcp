using System.Text.Json;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void RunAccumulator_BoundsFailuresAndOutputIncrementally()
    {
        var store = new TestRunStore(
            TimeProvider.System,
            maximumCharacters: 10_000
        );
        var accumulator = store.BeginAccumulator("accumulator");

        for (var index = 1; index <= 250; index++)
        {
            Assert.Equal(
                index <= 200,
                accumulator.AddFailure(
                    new TestRunStore.FailureRecord(
                        $"opaque-{index}",
                        $"Test{index}",
                        "/repo/tests.csproj",
                        new string('m', 10_000),
                        "trace",
                        "stdout",
                        "stderr"
                    )
                )
            );
        }

        accumulator.AddOutput(
            new TestRunStore.CapturedOutput(
                new string('o', 10_000),
                new string('e', 10_000),
                true,
                true,
                10_000,
                10_000
            )
        );
        var record = accumulator.Complete(TestOutcome.Failed);

        Assert.Equal(250, record.OriginalFailureCount);
        Assert.True(record.FailuresTruncated);
        Assert.Equal(200, record.Failures.Length);
        Assert.Equal("opaque-1", record.Failures[0].FailureId);
        Assert.Equal("opaque-200", record.Failures[^1].FailureId);
        Assert.True(record.Output.StandardOutputTruncated);
        Assert.True(record.Output.StandardErrorTruncated);
        Assert.True(CountCharacters(record) <= 10_000);
    }

    [Fact]
    public void Store_RetainsShortTraceAndOutputWhenMessageIsHuge()
    {
        var store = new TestRunStore(
            TimeProvider.System,
            maximumCharacters: 100
        );
        store.Store(
            new TestRunStore.RunRecord(
                "fair",
                default,
                [
                    new TestRunStore.FailureRecord(
                        "opaque-id",
                        "HugeMessageTest",
                        null,
                        new string('m', 5_000),
                        "short-trace",
                        "short-output",
                        "short-error"
                    ),
                ],
                new TestRunStore.CapturedOutput("run-output", "run-error")
            )
        );

        Assert.True(store.TryGet("fair", out var retained));
        var failure = Assert.Single(retained.Failures);
        Assert.Equal("opaque-id", failure.FailureId);
        Assert.Equal("short-trace", failure.Trace);
        Assert.Equal("short-output", failure.StandardOutput);
        Assert.Equal("short-error", failure.StandardError);
        Assert.True(failure.MessageTruncated);
        Assert.NotNull(retained.Output.StandardOutput);
        Assert.NotNull(retained.Output.StandardError);
    }

    [Fact]
    public void GetFailureDetails_HasMoreOnlyForRetrievablePages()
    {
        var store = new TestRunStore();
        store.Store(
            new TestRunStore.RunRecord(
                "pages",
                default,
                Enumerable
                    .Range(1, 250)
                    .Select(index =>
                        new TestRunStore.FailureRecord(
                            $"opaque-{index}",
                            $"Test{index}",
                            null,
                            "message",
                            null,
                            null,
                            null
                        )
                    )
                    .ToArray(),
                new()
            )
            {
                OriginalFailureCount = 250,
            }
        );

        var tool = new GetFailureDetailsTool(store);
        var finalPage = tool.GetFailureDetails("pages", page: 19, pageSize: 10);
        var outOfRange = tool.GetFailureDetails("pages", page: 20, pageSize: 10);
        var direct = tool.GetFailureDetails("pages", failureId: "opaque-1");

        Assert.True(finalPage.FailuresTruncated);
        Assert.False(finalPage.HasMore);
        Assert.Empty(outOfRange.Failures);
        Assert.False(outOfRange.HasMore);
        Assert.Single(direct.Failures);
        Assert.False(direct.HasMore);
        Assert.Empty(direct.AvailableFailureIds);
        Assert.Equal(
            200,
            tool.GetFailureDetails("pages", includeAvailableFailureIds: true)
                .AvailableFailureIds.Length
        );
    }

    [Fact]
    public void GetFailureDetails_FieldSelectorMakesLargeDiagnosticsIndependentlyReachable()
    {
        var store = new TestRunStore();
        store.Store(
            new TestRunStore.RunRecord(
                "fields",
                default,
                [
                    new TestRunStore.FailureRecord(
                        "opaque-id",
                        "HugeMessageTest",
                        null,
                        new string('m', 100_000),
                        "trace-is-reachable",
                        "stdout-is-reachable",
                        "stderr-is-reachable"
                    ),
                ],
                new TestRunStore.CapturedOutput(new string('r', 1_000), "run-stderr")
            )
        );

        var tool = new GetFailureDetailsTool(store);

        var all = tool.GetFailureDetails(
            "fields",
            failureId: "opaque-id",
            includeStackTrace: true,
            includeOutput: true,
            maxCharacters: 100
        );
        var allFailure = Assert.Single(all.Failures);
        Assert.Equal("trace-is-reachable", allFailure.StackTrace?.Text);
        Assert.Equal("stdout-is-reachable", allFailure.StandardOutput?.Text);
        Assert.Equal("stderr-is-reachable", allFailure.StandardError?.Text);

        var trace = tool.GetFailureDetails(
            "fields",
            failureId: "opaque-id",
            maxCharacters: 100,
            diagnosticField: "trace"
        );
        var output = tool.GetFailureDetails(
            "fields",
            failureId: "opaque-id",
            maxCharacters: 100,
            diagnosticField: "output"
        );

        var traceFailure = Assert.Single(trace.Failures);
        Assert.Null(traceFailure.Message);
        Assert.Equal("trace-is-reachable", traceFailure.StackTrace?.Text);
        var outputFailure = Assert.Single(output.Failures);
        Assert.Null(outputFailure.Message);
        Assert.Equal("stdout-is-reachable", outputFailure.StandardOutput?.Text);
        Assert.Equal("stderr-is-reachable", outputFailure.StandardError?.Text);
        var runStdout = Assert.IsType<string>(output.RunOutput?.StandardOutput?.Text);
        Assert.StartsWith("r", runStdout);
        Assert.Equal("run-stderr", output.RunOutput?.StandardError?.Text);
    }

    [Fact]
    public void GetFailureDetails_BoundsIdentityPreviewsWithoutChangingOpaqueIds()
    {
        var store = new TestRunStore();
        store.Store(
            new TestRunStore.RunRecord(
                "identity",
                default,
                [
                    new TestRunStore.FailureRecord(
                        "opaque-id",
                        new string('t', 1_000),
                        new string('p', 1_000),
                        "message",
                        null,
                        null,
                        null
                    ),
                ],
                new()
            )
        );

        var result = new GetFailureDetailsTool(store).GetFailureDetails(
            "identity",
            failureId: "opaque-id",
            maxCharacters: 1
        );
        var failure = Assert.Single(result.Failures);

        Assert.Equal("opaque-id", failure.FailureId);
        Assert.Contains("[truncated]", failure.TestName, StringComparison.Ordinal);
        var projectPath = Assert.IsType<string>(failure.ProjectPath);
        Assert.Contains("[truncated]", projectPath, StringComparison.Ordinal);
        Assert.True(failure.TestName.Length <= 256);
        Assert.True(projectPath.Length <= 256);
        Assert.Equal("m", failure.Message?.Text);
    }


    [Fact]
    public void CtrfErrorInfo_PropagatesCaptureTruncationMetadata()
    {
        var command = new CommandResult(
            1,
            "stdout-tail",
            "stderr-tail",
            ["stdout-tail"],
            ["stderr-tail"],
            TimeSpan.Zero
        )
        {
            StandardOutputTruncated = true,
            StandardErrorTruncated = true,
            StandardOutputOriginalCharacterCount = 10_000,
            StandardErrorOriginalCharacterCount = 20_000,
        };

        var error = CtrfTestRun.BuildErrorInfo(
            command,
            null,
            ErrorKind.BuildFailed
        );

        Assert.True(error.Stdout is { Truncated: true });
        Assert.Equal(10_000, error.Stdout?.OriginalCharCount);
        Assert.True(error.Stderr is { Truncated: true });
        Assert.Equal(20_000, error.Stderr?.OriginalCharCount);
    }

    [Fact]
    public async Task CtrfCleanup_DeletesArtifactWhenExecutionIsCanceled()
    {
        var runner = new CancelingCommandRunner();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CtrfTestRun.ExecuteOnceAsync(
                runner,
                new JsonSerializerOptions(),
                ["test"],
                disableCtrf: false,
                supportsCtrf: true,
                Options.Create(new McpOptions()).Value,
                workingDirectory: null,
                CancellationToken.None
            )
        );

        Assert.NotNull(runner.ArtifactPath);
        Assert.False(File.Exists(runner.ArtifactPath));
    }

    [Fact]
    public void Store_ExpiresRunsAfterConfiguredAge()
    {
        var clock = new MutableTimeProvider(
            DateTimeOffset.Parse("2026-01-01T00:00:00Z")
        );
        var store = new TestRunStore(
            clock,
            maximumAge: TimeSpan.FromMinutes(1)
        );
        store.Store(new TestRunStore.RunRecord("expiring"));

        Assert.True(store.TryGet("expiring", out _));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(store.TryGet("expiring", out _));
    }

    private static int CountCharacters(TestRunStore.RunRecord run)
    {
        var count = run.Output.StandardOutput?.Length ?? 0;
        count += run.Output.StandardError?.Length ?? 0;
        foreach (var failure in run.Failures)
        {
            count += failure.FailureId.Length;
            count += failure.TestName.Length;
            count += failure.ProjectPath?.Length ?? 0;
            count += failure.Message?.Length ?? 0;
            count += failure.Trace?.Length ?? 0;
            count += failure.StandardOutput?.Length ?? 0;
            count += failure.StandardError?.Length ?? 0;
        }

        return count;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now += amount;
    }

    private sealed class CancelingCommandRunner : ICommandRunner
    {
        public string? ArtifactPath { get; private set; }

        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default
        )
        {
            var index = Array.IndexOf(request.Arguments, "--report-ctrf-filename");
            Assert.True(index >= 0);
            ArtifactPath = Path.Combine(
                Path.GetTempPath(),
                request.Arguments[index + 1]
            );
            File.WriteAllText(ArtifactPath, "{}");
            throw new OperationCanceledException(cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
