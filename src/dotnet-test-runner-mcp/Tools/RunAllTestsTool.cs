using System.ComponentModel;
using System.Text.Json;
using DotnetTestRunnerMcp.Extensions;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DotnetTestRunnerMcp.Tools;

[McpServerToolType]
public sealed class RunAllTestsTool(
    ICommandRunner commandRunner,
    JsonSerializerOptions jsonOptions,
    IOptions<McpOptions> options
)
{
    private const int MaxFailingTests = 20;
    private const int MaxFailureDetails = 3;

    private readonly ICommandRunner _commandRunner = commandRunner.ValidateNotNull();
    private readonly JsonSerializerOptions _jsonOptions = jsonOptions.ValidateNotNull();
    private readonly McpOptions _options = options.Value.ValidateNotNull();

    [McpServerTool(UseStructuredContent = true)]
    [Description("Runs all tests in the solution.")]
    public async Task<Result> RunAllTests(
        [Description("Include stack traces in failure details. Default is false.")]
            bool includeStackTrace = false,
        [Description(
            "Optional working directory to run dotnet commands from (useful for git worktrees)."
        )]
            string? workingDirectory = null,
        CancellationToken cancellationToken = default
    )
    {
        var outputOptions = FailureFormatting.CreateOptions(includeStackTrace);
        var trimmedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : workingDirectory.Trim();

        var runResult = await CtrfTestRun.ExecuteAsync(
            _commandRunner,
            _jsonOptions,
            ["test"],
            _options.DisableCtrf,
            _options,
            trimmedWorkingDirectory,
            cancellationToken
        );

        if (runResult.Report is null)
        {
            if (
                !runResult.ReportFileFound
                && (runResult.CommandResult.ExitCode is 0 or 1)
                && ConsoleTestSummaryParser.HasSummary(runResult.CommandResult)
            )
                return CreateResultFromConsole(runResult.CommandResult);
            if (runResult is { ReportFileFound: false, CommandResult.ExitCode: 8 })
                return CreateResult(
                    TestOutcome.NotFound,
                    "No tests found.",
                    runResult.CommandResult.ExitCode,
                    [],
                    false,
                    null
                );

            var errorKind = CtrfTestRun.ClassifyErrorKind(
                runResult.CommandResult,
                runResult.ReportFileFound,
                runResult.ReadErrorMessage
            );
            var error = CtrfTestRun.BuildErrorInfo(
                runResult.CommandResult,
                runResult.ReadErrorMessage,
                errorKind
            );
            return CreateResult(
                TestOutcome.Error,
                error.Reason,
                runResult.CommandResult.ExitCode,
                [],
                false,
                error
            );
        }

        return CreateResultFromReport(runResult.Report, runResult.CommandResult, outputOptions);
    }

    [Description("Result of running the entire test suite.")]
    public record Result(
        [property: Description("Scope used to run tests: solution.")] string Scope,
        [property: Description("Outcome of the test run.")] TestOutcome Outcome,
        [property: Description("Short summary (<= 1-2 lines).")] string Message,
        [property: Description("Process exit code from dotnet test.")] int ExitCode,
        [property: Description("Total number of tests when available.")] int TestCount,
        [property: Description("Count of passed tests when available.")] int Passed,
        [property: Description("Count of failed tests when available.")] int Failed,
        [property: Description("Count of skipped tests when available.")] int Skipped,
        [property: Description("Count of pending tests when available.")] int Pending,
        [property: Description("Count of other-status tests when available.")] int Other,
        [property: Description("Total duration in milliseconds when available; otherwise null.")]
            int? DurationMilliseconds,
        [property: Description("Up to the first 20 failing test names when available.")]
            string[] FailingTests,
        [property: Description("Structured details for up to 3 failed tests.")]
            TestFailure[] FailureDetails,
        [property: Description("True when more failed tests exist than returned.")]
            bool HasMoreFailures,
        [property: Description("Structured error details when Outcome is Error; otherwise null.")]
            ErrorInfo? Error
    );

    private Result CreateResult(
        TestOutcome outcome,
        string message,
        int exitCode,
        TestFailure[] failureDetails,
        bool hasMoreFailures,
        ErrorInfo? error
    ) =>
        new(
            "solution",
            outcome,
            message,
            exitCode,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            [],
            failureDetails,
            hasMoreFailures,
            error
        );

    private static Result CreateResultFromConsole(CommandResult commandResult)
    {
        var summary = ConsoleTestSummaryParser.Parse(commandResult);
        var outcome = ConsoleTestSummaryParser.GetOutcome(commandResult, summary);
        return new Result(
            "solution",
            outcome,
            ConsoleTestSummaryParser.GetMessage(outcome),
            commandResult.ExitCode,
            summary.TestCount,
            summary.Passed,
            summary.Failed,
            summary.Skipped,
            summary.Pending,
            summary.Other,
            summary.DurationMilliseconds,
            [],
            [],
            false,
            outcome == TestOutcome.Error
                ? CtrfTestRun.BuildErrorInfo(
                    commandResult,
                    null,
                    CtrfTestRun.ClassifyErrorKind(commandResult, false, null)
                )
                : null
        );
    }

    private Result CreateResultFromReport(
        CtrfReport report,
        CommandResult commandResult,
        FailureFormatting.FailureOutputOptions outputOptions
    )
    {
        var summary = report.Results.Summary;
        var tests = report.Results.Tests;

        var exitCode = commandResult.ExitCode;
        var testCount = summary.Tests > 0 ? summary.Tests : tests.Count;
        var passed = summary.Passed > 0 ? summary.Passed : CountByStatus(tests, "passed");
        var failed = summary.Failed > 0 ? summary.Failed : CountByStatus(tests, "failed");
        var skipped = summary.Skipped > 0 ? summary.Skipped : CountByStatus(tests, "skipped");
        var pending = summary.Pending > 0 ? summary.Pending : CountByStatus(tests, "pending");
        var other = summary.Other > 0 ? summary.Other : CountByStatus(tests, "other");

        var durationMilliseconds = CtrfTestRun.TryParseDurationMilliseconds(summary.Duration);
        var failingTests = ExtractFailingTests(tests);
        var failureDetails = ExtractFailureDetails(tests, outputOptions, out var hasMoreFailures);

        if (exitCode != 0 && exitCode != 8 && failed == 0)
        {
            var errorKind = CtrfTestRun.ClassifyErrorKind(commandResult, true, null);
            var error = CtrfTestRun.BuildErrorInfo(commandResult, null, errorKind);
            return new Result(
                "solution",
                TestOutcome.Error,
                error.Reason,
                exitCode,
                testCount,
                passed,
                failed,
                skipped,
                pending,
                other,
                durationMilliseconds,
                failingTests,
                failureDetails,
                hasMoreFailures,
                error
            );
        }

        if (testCount == 0 && tests.Count == 0)
            return new Result(
                "solution",
                TestOutcome.NotFound,
                "No tests found.",
                exitCode,
                testCount,
                passed,
                failed,
                skipped,
                pending,
                other,
                durationMilliseconds,
                failingTests,
                failureDetails,
                hasMoreFailures,
                null
            );

        var outcome =
            failed > 0 ? TestOutcome.Failed
            : passed > 0 ? TestOutcome.Passed
            : TestOutcome.Skipped;

        var message = outcome switch
        {
            TestOutcome.Failed => "Test run completed with failures.",
            TestOutcome.Passed => "All tests passed.",
            TestOutcome.Skipped => "Tests completed without failures.",
            _ => "Test run completed with an unknown outcome.",
        };

        return new Result(
            "solution",
            outcome,
            message,
            exitCode,
            testCount,
            passed,
            failed,
            skipped,
            pending,
            other,
            durationMilliseconds,
            failingTests,
            failureDetails,
            hasMoreFailures,
            null
        );
    }

    private static int CountByStatus(IReadOnlyCollection<CtrfTest> tests, string status) =>
        tests.Count(test => string.Equals(test.Status, status, StringComparison.OrdinalIgnoreCase));

    private static string[] ExtractFailingTests(IReadOnlyCollection<CtrfTest> tests) =>
        tests
            .Where(test => string.Equals(test.Status, "failed", StringComparison.OrdinalIgnoreCase))
            .Select(test => test.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxFailingTests)
            .ToArray();

    private static TestFailure[] ExtractFailureDetails(
        IReadOnlyCollection<CtrfTest> tests,
        FailureFormatting.FailureOutputOptions outputOptions,
        out bool hasMoreFailures
    )
    {
        var failures = tests
            .Where(test => string.Equals(test.Status, "failed", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (failures.Count == 0)
        {
            hasMoreFailures = false;
            return [];
        }

        var details = failures
            .Take(MaxFailureDetails)
            .Select(test => FailureFormatting.BuildFailure(test, outputOptions))
            .Where(detail => detail is not null)
            .Select(detail => detail!)
            .ToArray();

        hasMoreFailures = failures.Count > details.Length;
        return details;
    }
}
