using System.ComponentModel;
using DotnetTestRunnerMcp.Extensions;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DotnetTestRunnerMcp.Tools;

[McpServerToolType]
public sealed class ListTestsSummaryTool(IOptions<McpOptions> options, ICommandRunner commandRunner)
{
    private const int MaxReturnedTests = 200;
    private const int MaxProjectsToScan = 8;
    private static readonly TimeSpan PerProjectListTestsTimeout = TimeSpan.FromSeconds(20);

    private readonly McpOptions _options = options.ValidateNotNull().Value;
    private readonly ICommandRunner _commandRunner = commandRunner.ValidateNotNull();

    [McpServerTool(UseStructuredContent = true)]
    [Description("Summarizes discovered tests with counts and an optional capped list.")]
    public async Task<Result> ListTestsSummary(
        [Description(
            "Optional prefix to filter discovered test names. Fully qualified prefixes require fully qualified names from the test runner; some runners return short method names."
        )]
            string? prefix = null,
        [Description("Optional project path to scope discovery to a single test project.")]
            string? projectPath = null,
        [Description("When true, include up to 200 test names; otherwise return counts only.")]
            bool includeTests = false,
        [Description("When true, treat zero discovered tests as an error.")]
            bool requireTests = false,
        [Description(
            "Optional working directory to run dotnet commands from (useful for git worktrees)."
        )]
            string? workingDirectory = null,
        CancellationToken cancellationToken = default
    )
    {
        var trimmedPrefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim();
        var trimmedProjectPath = string.IsNullOrWhiteSpace(projectPath) ? null : projectPath.Trim();
        var trimmedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : workingDirectory.Trim();

        var testProjects = await TestProjectDiscovery.ListAsync(
            _commandRunner,
            _options,
            trimmedWorkingDirectory,
            cancellationToken
        );

        if (trimmedProjectPath is not null)
        {
            testProjects = testProjects
                .Where(project =>
                    string.Equals(project, trimmedProjectPath, StringComparison.Ordinal)
                )
                .ToArray();

            if (testProjects.Length == 0)
                testProjects = [trimmedProjectPath];
        }
        else if (testProjects.Length > MaxProjectsToScan)
        {
            testProjects = testProjects.Take(MaxProjectsToScan).ToArray();
        }

        if (testProjects.Length == 0)
        {
            if (requireTests)
            {
                var noTestsError = new ErrorInfo(
                    ErrorKind.NoTestsDiscovered,
                    "No tests discovered.",
                    null,
                    null
                );
                return new Result(
                    "solution",
                    TestOutcome.Error,
                    noTestsError.Reason,
                    0,
                    0,
                    0,
                    false,
                    [],
                    null,
                    [],
                    noTestsError
                );
            }

            return new Result(
                "solution",
                TestOutcome.Passed,
                "No test projects discovered.",
                0,
                0,
                0,
                false,
                [],
                "No test projects discovered.",
                [],
                null
            );
        }

        var projectResults = new List<ProjectDiscoveryResult>(testProjects.Length);
        var allTests = new List<string>();

        foreach (var testProjectPath in testProjects)
        {
            var discovery = await DiscoverProjectTests(
                testProjectPath,
                trimmedPrefix,
                requireTests,
                trimmedWorkingDirectory,
                cancellationToken
            );
            projectResults.Add(discovery.ProjectResult);
            if (discovery.Tests.Length > 0)
                allTests.AddRange(discovery.Tests);
        }

        var tests = allTests
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var testCount = tests.Length;
        var returnedTests = includeTests ? tests.Take(MaxReturnedTests).ToArray() : [];
        var returnedCount = includeTests ? returnedTests.Length : 0;
        var hasMore = includeTests && testCount > returnedCount;

        var anyError = projectResults.Any(result => result.Outcome == TestOutcome.Error);
        var anySuccess = projectResults.Any(result => result.Outcome == TestOutcome.Passed);
        var hasNonNoTestsError = projectResults.Any(result =>
            result.Outcome == TestOutcome.Error && result.Error?.Kind != ErrorKind.NoTestsDiscovered
        );

        string message;
        string? warning = null;
        ErrorInfo? error = null;
        TestOutcome outcome;

        if (requireTests && testCount == 0 && !hasNonNoTestsError)
        {
            error = new ErrorInfo(ErrorKind.NoTestsDiscovered, "No tests discovered.", null, null);
            outcome = TestOutcome.Error;
            message = error.Reason;
        }
        else if (anyError && anySuccess)
        {
            outcome = TestOutcome.Partial;
            message = "Discovery partially succeeded. See ProjectResults for details.";
        }
        else if (anyError)
        {
            outcome = TestOutcome.Error;
            var projectError =
                projectResults
                    .Select(result => result.Error)
                    .FirstOrDefault(value =>
                        value is not null && value.Kind != ErrorKind.NoTestsDiscovered
                    )
                ?? projectResults
                    .Select(result => result.Error)
                    .FirstOrDefault(value => value is not null);

            if (projectError is not null)
            {
                error = projectError;
                message = projectError.Reason;
            }
            else
            {
                message = "Discovery failed for all projects.";
                error = new ErrorInfo(ErrorKind.DiscoveryFailed, message, null, null);
            }
        }
        else if (testCount == 0)
        {
            outcome = TestOutcome.Passed;
            warning = "No tests discovered.";
            message = warning;
        }
        else
        {
            outcome = TestOutcome.Passed;
            message = $"Discovered {testCount} tests.";
        }

        return new Result(
            "solution",
            outcome,
            message,
            0,
            testCount,
            returnedCount,
            hasMore,
            returnedTests,
            warning,
            projectResults.ToArray(),
            error
        );
    }

    private async Task<ProjectDiscovery> DiscoverProjectTests(
        string projectPath,
        string? prefix,
        bool requireTests,
        string? workingDirectory,
        CancellationToken cancellationToken
    )
    {
        var dialect = TestRunnerDialectDetector.Detect(projectPath, _options, workingDirectory);
        string[] arguments =
            dialect == TestRunnerDialect.VSTest
                ? ["test", projectPath, "--list-tests"]
                : ["test", "--project", projectPath, "--list-tests", "--no-ansi", "--no-progress"];

        var commandResult = await _commandRunner.RunAsync(
            new CommandRequest("dotnet", arguments.ToArray())
            {
                WorkingDirectory = workingDirectory,
                ThrowOnNonZeroExitCode = false,
                Timeout = PerProjectListTestsTimeout,
            },
            cancellationToken
        );

        var discoveredTests = TestListParser.ExtractTests(commandResult.StandardOutputLines);
        var exitCode = commandResult.ExitCode;

        if (
            exitCode == 0
            && prefix is not null
            && prefix.Contains('.', StringComparison.Ordinal)
            && discoveredTests.Any(test => !test.Contains('.', StringComparison.Ordinal))
        )
        {
            var reason =
                $"Cannot apply qualified prefix '{prefix}': dotnet test returned {discoveredTests.Length} unqualified test names (short method names), so the qualified prefix cannot be evaluated.";
            var error = CtrfTestRun.BuildErrorInfo(
                commandResult,
                reason,
                ErrorKind.DiscoveryFailed,
                projectPath: projectPath
            );

            return new ProjectDiscovery(
                new ProjectDiscoveryResult(
                    projectPath,
                    TestOutcome.Error,
                    error.Reason,
                    exitCode,
                    0,
                    null,
                    error
                ),
                []
            );
        }

        var tests = prefix is null
            ? discoveredTests
            : discoveredTests
                .Where(test => test.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();

        var testCount = tests.Length;
        if (exitCode != 0)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                var timeoutError = new ErrorInfo(
                    ErrorKind.DiscoveryFailed,
                    "Project discovery timed out.",
                    string.IsNullOrEmpty(commandResult.StandardOutput)
                        ? null
                        : new TruncatedText(
                            commandResult.StandardOutput,
                            false,
                            commandResult.StandardOutput.Length,
                            commandResult.StandardOutput.Count(c => c == '\n') + 1
                        ),
                    string.IsNullOrEmpty(commandResult.StandardError)
                        ? null
                        : new TruncatedText(
                            commandResult.StandardError,
                            false,
                            commandResult.StandardError.Length,
                            commandResult.StandardError.Count(c => c == '\n') + 1
                        )
                );

                return new ProjectDiscovery(
                    new ProjectDiscoveryResult(
                        projectPath,
                        TestOutcome.Error,
                        timeoutError.Reason,
                        exitCode,
                        testCount,
                        null,
                        timeoutError
                    ),
                    tests
                );
            }

            var errorKind = CtrfTestRun.ClassifyErrorKind(commandResult, true, null);
            if (errorKind == ErrorKind.NoTestsDiscovered && !requireTests)
            {
                var warning = "No tests discovered.";
                return new ProjectDiscovery(
                    new ProjectDiscoveryResult(
                        projectPath,
                        TestOutcome.Passed,
                        warning,
                        exitCode,
                        0,
                        warning,
                        null
                    ),
                    tests
                );
            }

            var error = CtrfTestRun.BuildErrorInfo(commandResult, null, errorKind);
            return new ProjectDiscovery(
                new ProjectDiscoveryResult(
                    projectPath,
                    TestOutcome.Error,
                    error.Reason,
                    exitCode,
                    testCount,
                    null,
                    error
                ),
                tests
            );
        }

        if (testCount == 0)
        {
            if (requireTests)
            {
                var error = CtrfTestRun.BuildErrorInfo(
                    commandResult,
                    "No tests discovered.",
                    ErrorKind.NoTestsDiscovered
                );
                return new ProjectDiscovery(
                    new ProjectDiscoveryResult(
                        projectPath,
                        TestOutcome.Error,
                        error.Reason,
                        exitCode,
                        0,
                        null,
                        error
                    ),
                    tests
                );
            }

            var warning = "No tests discovered.";
            return new ProjectDiscovery(
                new ProjectDiscoveryResult(
                    projectPath,
                    TestOutcome.Passed,
                    warning,
                    exitCode,
                    0,
                    warning,
                    null
                ),
                tests
            );
        }

        return new ProjectDiscovery(
            new ProjectDiscoveryResult(
                projectPath,
                TestOutcome.Passed,
                $"Discovered {testCount} tests.",
                exitCode,
                testCount,
                null,
                null
            ),
            tests
        );
    }

    private sealed record ProjectDiscovery(ProjectDiscoveryResult ProjectResult, string[] Tests);

    [Description("Result containing a summarized test listing.")]
    public record Result(
        [property: Description("Scope used to list tests: solution.")] string Scope,
        [property: Description("Outcome of the discovery run.")] TestOutcome Outcome,
        [property: Description("Short summary (<= 1-2 lines).")] string Message,
        [property: Description("Process exit code from dotnet test when available.")] int ExitCode,
        [property: Description("Total discovered test count after applicable filtering.")]
            int TestCount,
        [property: Description("Number of tests returned in this response.")] int ReturnedCount,
        [property: Description("True when more tests exist beyond the returned list.")]
            bool HasMore,
        [property: Description(
            "Optional capped list of discovered test names; some runners return short method names."
        )]
            string[] Tests,
        [property: Description("Optional warning message when Outcome is Passed.")] string? Warning,
        [property: Description("Per-project discovery results.")]
            ProjectDiscoveryResult[] ProjectResults,
        [property: Description("Structured error details when Outcome is Error; otherwise null.")]
            ErrorInfo? Error
    );
}
