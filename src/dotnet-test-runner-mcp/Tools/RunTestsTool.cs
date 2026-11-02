using System.ComponentModel;
using System.Text.Json;
using DotnetTestRunnerMcp.Extensions;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DotnetTestRunnerMcp.Tools;

[McpServerToolType]
public sealed class RunTestsTool
{
    private const int MaxFailureIdsInResponse = 100;
    private const int MaxCompactDiagnosticCharacters = 4_000;

    private readonly ICommandRunner _commandRunner;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly McpOptions _options;
    private readonly TestRunStore _store;
    private readonly ProjectMetadataReader _metadataReader;
    private readonly TestCatalog _catalog;

    public RunTestsTool(
        ICommandRunner commandRunner,
        JsonSerializerOptions jsonOptions,
        IOptions<McpOptions> options,
        TestRunStore store,
        ProjectMetadataReader? metadataReader = null
    )
    {
        _commandRunner = commandRunner.ValidateNotNull();
        _jsonOptions = jsonOptions.ValidateNotNull();
        _options = options.ValidateNotNull().Value;
        _store = store.ValidateNotNull();
        _metadataReader = metadataReader ?? new ProjectMetadataReader(commandRunner, options);
        _catalog = new TestCatalog(options, commandRunner, _metadataReader);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Runs validated test selections sequentially and retains bounded failure details for get_failure_details."
    )]
    public async Task<Result> RunTests(
        [Description(
            "Test selections. Set TestName to null to run a whole project; method selections must match discovered names exactly."
        )]
            TestSelection[] selections,
        [Description("Allow a method selector to run multiple discovered test cases. Default is false.")]
            bool allowMultipleMatches = false,
        [Description("Optional working directory to run dotnet commands from.")] string? workingDirectory = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(selections);

        var runId = Guid.NewGuid().ToString("N");
        var trimmedWorkingDirectory = TrimOrNull(workingDirectory);
        var effectiveWorkingDirectory = ResolveWorkingDirectory(trimmedWorkingDirectory);

        if (selections.Length == 0)
        {
            var issue = new SelectionIssue(
                new TestSelection(string.Empty),
                TestOutcome.Error,
                "At least one test selection is required.",
                [],
                null
            );
            StoreEmptyRun(runId);
            return CreateValidationResult(runId, [issue]);
        }

        var validation = await ValidateSelectionsAsync(
            selections,
            allowMultipleMatches,
            trimmedWorkingDirectory,
            effectiveWorkingDirectory,
            cancellationToken
        );

        if (validation.Issues.Count > 0)
        {
            StoreEmptyRun(runId);
            var allIssues = validation.Issues.ToList();
            foreach (var prepared in validation.Prepared)
            {
                if (validation.InvalidIndices.Contains(prepared.InputIndex))
                    continue;

                allIssues.Add(
                    new SelectionIssue(
                        prepared.Selection,
                        TestOutcome.Error,
                        "Selection was not executed because another selection failed validation.",
                        [],
                        null
                    )
                );
            }

            return CreateValidationResult(runId, allIssues);
        }

        var executionResults = new List<PlanResult>(validation.Plans.Count);
        var accumulator = _store.BeginAccumulator(runId);

        foreach (var plan in validation.Plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var execution = await ExecutePlanAsync(
                plan,
                trimmedWorkingDirectory,
                runId,
                accumulator,
                cancellationToken
            );
            executionResults.Add(new PlanResult(plan, execution));
        }

        var aggregateOutcome = GetAggregateOutcome(
            executionResults.Select(value => value.Execution).ToArray()
        );
        var retainedRun = accumulator.Complete(aggregateOutcome);
        _store.Store(retainedRun);
        return CreateExecutionResult(runId, executionResults, retainedRun);
    }

    private async Task<ValidationState> ValidateSelectionsAsync(
        TestSelection[] selections,
        bool allowMultipleMatches,
        string? workingDirectory,
        string effectiveWorkingDirectory,
        CancellationToken cancellationToken
    )
    {
        var prepared = new List<PreparedSelection>(selections.Length);
        var issues = new List<SelectionIssue>();
        var invalidIndices = new HashSet<int>();
        var projectDiscovery = new Dictionary<string, TestCatalog.CatalogDiscovery>(PathComparer);
        var projectDialects = new Dictionary<string, TestRunnerDialect>(PathComparer);

        for (var index = 0; index < selections.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selection = selections[index];
            if (selection is null)
            {
                AddIssue(
                    index,
                    new TestSelection(string.Empty),
                    TestOutcome.Error,
                    "A test selection cannot be null.",
                    [],
                    null
                );
                continue;
            }

            var projectPath = TrimOrNull(selection.ProjectPath);
            if (projectPath is null)
            {
                AddIssue(
                    index,
                    selection,
                    TestOutcome.Error,
                    "ProjectPath is required.",
                    [],
                    null
                );
                continue;
            }

            string canonicalProjectPath;
            try
            {
                canonicalProjectPath = Path.GetFullPath(projectPath, effectiveWorkingDirectory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                AddIssue(
                    index,
                    selection,
                    TestOutcome.Error,
                    $"Invalid project path '{projectPath}': {exception.Message}",
                    [],
                    null
                );
                continue;
            }

            if (!File.Exists(canonicalProjectPath))
            {
                AddIssue(
                    index,
                    selection,
                    TestOutcome.NotFound,
                    $"Project file '{projectPath}' does not exist.",
                    [],
                    null
                );
                continue;
            }

            if (selection.TestName is null)
            {
                if (!projectDialects.TryGetValue(canonicalProjectPath, out var projectDialect))
                {
                    try
                    {
                        projectDialect = await _metadataReader.GetDialectAsync(
                            projectPath,
                            workingDirectory,
                            cancellationToken
                        );
                        projectDialects.Add(canonicalProjectPath, projectDialect);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        var error = BuildValidationError(
                            projectPath,
                            $"Project metadata evaluation failed for '{projectPath}': {exception.Message}",
                            exception
                        );
                        AddIssue(index, selection, TestOutcome.Error, error.Reason, [], error);
                        continue;
                    }
                }

                if (projectDialect == TestRunnerDialect.Unknown)
                {
                    var error = BuildValidationError(
                        projectPath,
                        $"The test runner dialect for '{projectPath}' could not be determined from evaluated project metadata.",
                        null
                    );
                    AddIssue(index, selection, TestOutcome.Error, error.Reason, [], error);
                    continue;
                }

                prepared.Add(
                    new PreparedSelection(
                        index,
                        selection with { ProjectPath = projectPath, TestName = null },
                        projectPath,
                        canonicalProjectPath,
                        true,
                        [],
                        projectDialect,
                        null
                    )
                );
                continue;
            }

            var testName = TrimOrNull(selection.TestName);
            if (testName is null)
            {
                AddIssue(
                    index,
                    selection,
                    TestOutcome.Error,
                    "TestName must be null for a whole-project selection or contain a method name.",
                    [],
                    null
                );
                continue;
            }

            TestCatalog.CatalogDiscovery discovery;
            if (!projectDiscovery.TryGetValue(canonicalProjectPath, out discovery!))
            {
                try
                {
                    discovery = await _catalog.DiscoverAsync(
                        projectPath,
                        workingDirectory,
                        cancellationToken
                    );
                    projectDiscovery.Add(canonicalProjectPath, discovery);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var error = BuildValidationError(
                        projectPath,
                        $"Test discovery failed for project '{projectPath}': {exception.Message}",
                        exception
                    );
                    AddIssue(index, selection, TestOutcome.Error, error.Reason, [], error);
                    continue;
                }
            }

            var projectResult = discovery.ProjectResults.FirstOrDefault(result =>
                PathsEqual(result.ProjectPath, projectPath, effectiveWorkingDirectory)
            );
            if (projectResult?.Outcome == TestOutcome.Error)
            {
                var error = projectResult.Error
                    ?? BuildValidationError(projectPath, projectResult.Message, null);
                AddIssue(index, selection, TestOutcome.Error, error.Reason, [], error);
                continue;
            }

            var candidates = discovery
                .Tests.Where(test =>
                    PathsEqual(test.ProjectPath, projectPath, effectiveWorkingDirectory)
                    && (
                        string.Equals(test.Selector, testName, StringComparison.Ordinal)
                        || string.Equals(test.DisplayName, testName, StringComparison.Ordinal)
                    )
                )
                .Distinct()
                .ToArray();

            if (candidates.Length == 0)
            {
                AddIssue(
                    index,
                    selection,
                    TestOutcome.NotFound,
                    $"No discovered test exactly matches '{testName}'.",
                    [],
                    null
                );
                continue;
            }

            var matchingNames = candidates
                .Select(candidate => CompactText(candidate.DisplayName) ?? string.Empty)
                .Distinct(StringComparer.Ordinal)
                .Take(20)
                .ToArray();
            var ambiguous = candidates.Length > 1 || candidates.Any(candidate => candidate.IsAmbiguous);
            if (ambiguous && !allowMultipleMatches)
            {
                AddIssue(
                    index,
                    selection,
                    TestOutcome.Ambiguous,
                    $"Selector '{testName}' may match multiple cases or have non-exact identity; set allowMultipleMatches=true to run all known matches.",
                    matchingNames,
                    null
                );
                continue;
            }

            var dialect = candidates[0].Dialect;
            var selectors = candidates
                .Select(value => value.Selector)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (dialect == TestRunnerDialect.Unknown || selectors.Length == 0)
            {
                AddIssue(
                    index,
                    selection,
                    TestOutcome.Error,
                    $"Exact method selection is unsupported for '{testName}' because the runner dialect or selector is unknown.",
                    matchingNames,
                    null
                );
                continue;
            }

            var invalidSelector = false;
            foreach (var selector in selectors)
            {
                if (
                    !TestCommandBuilder.TryValidateMethodSelector(
                        dialect,
                        selector,
                        out var selectorError
                    )
                )
                {
                    AddIssue(index, selection, TestOutcome.Error, selectorError, matchingNames, null);
                    invalidSelector = true;
                    break;
                }
            }

            if (invalidSelector)
                continue;

            if (
                !TestBatchCommandBuilder.TryBuild(
                    dialect,
                    projectPath,
                    selectors,
                    out _,
                    out var unionError
                )
            )
            {
                AddIssue(index, selection, TestOutcome.Error, unionError, matchingNames, null);
                continue;
            }

            prepared.Add(
                new PreparedSelection(
                    index,
                    selection with { ProjectPath = projectPath, TestName = testName },
                    projectPath,
                    canonicalProjectPath,
                    false,
                    selectors,
                    dialect,
                    candidates[0]
                )
            );
        }

        foreach (var group in prepared.GroupBy(value => value.CanonicalProjectPath, PathComparer))
        {
            var wholeProject = group.Any(value => value.IsWholeProject);
            if (!wholeProject || !group.Any(value => !value.IsWholeProject))
                continue;

            foreach (var value in group)
            {
                AddIssue(
                    value.InputIndex,
                    value.Selection,
                    TestOutcome.Ambiguous,
                    "A whole-project selection overlaps a method selection for the same project; submit one scope only.",
                    [],
                    null
                );
            }
        }

        if (issues.Count > 0)
            return new ValidationState(prepared, [], issues, invalidIndices);

        var plans = new List<ExecutionPlan>();
        foreach (
            var projectGroup in prepared.GroupBy(
                value => value.CanonicalProjectPath,
                PathComparer
            )
        )
        {
            var requests = projectGroup.ToList();
            var primary = requests[0];
            var selectors = primary.IsWholeProject
                ? []
                : requests
                    .SelectMany(value => value.Selectors)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            plans.Add(new ExecutionPlan(primary, requests, selectors));
        }

        return new ValidationState(prepared, plans, issues, invalidIndices);

        void AddIssue(
            int index,
            TestSelection selection,
            TestOutcome outcome,
            string reason,
            string[] matchingTests,
            ErrorInfo? error
        )
        {
            issues.Add(
                new SelectionIssue(
                    selection,
                    outcome,
                    CompactText(reason) ?? string.Empty,
                    matchingTests,
                    CompactError(error)
                )
            );
            invalidIndices.Add(index);
        }
    }

    private async Task<Execution> ExecutePlanAsync(
        ExecutionPlan plan,
        string? workingDirectory,
        string runId,
        TestRunStore.RunAccumulator accumulator,
        CancellationToken cancellationToken
    )
    {
        var dialect = plan.Primary.Dialect;
        string[] arguments;
        if (plan.Primary.IsWholeProject)
        {
            arguments = TestCommandBuilder.BuildProjectRun(dialect, plan.Primary.ProjectPath);
        }
        else if (
            !TestBatchCommandBuilder.TryBuild(
                dialect,
                plan.Primary.ProjectPath,
                plan.Selectors,
                out arguments,
                out var buildError
            )
        )
        {
            return new Execution(
                TestOutcome.Error,
                CompactText(buildError),
                -1,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                [],
                false,
                CompactError(BuildValidationError(plan.Primary.ProjectPath, buildError, null))
            );
        }

        var supportsCtrf = dialect == TestRunnerDialect.MTP;
        var runResult = await CtrfTestRun.ExecuteOnceAsync(
            _commandRunner,
            _jsonOptions,
            arguments,
            _options.DisableCtrf,
            supportsCtrf,
            _options,
            workingDirectory,
            cancellationToken
        );
        accumulator.AddOutput(ToCapturedOutput(runResult.CommandResult));

        if (runResult.Report is not null)
            return CreateExecutionFromReport(
                runResult.Report,
                runResult.CommandResult,
                runId,
                plan.Primary.ProjectPath,
                accumulator
            );

        if (
            !runResult.ReportFileFound
            && (runResult.CommandResult.ExitCode is 0 or 1)
            && ConsoleTestSummaryParser.HasSummary(runResult.CommandResult)
        )
        {
            var summary = ConsoleTestSummaryParser.Parse(runResult.CommandResult);
            var outcome = ConsoleTestSummaryParser.GetOutcome(runResult.CommandResult, summary);
            var error = outcome == TestOutcome.Error
                ? CtrfTestRun.BuildErrorInfo(
                    runResult.CommandResult,
                    null,
                    CtrfTestRun.ClassifyErrorKind(runResult.CommandResult, false, null),
                    "run_tests",
                    plan.Primary.ProjectPath
                )
                : null;
            return new Execution(
                outcome,
                ConsoleTestSummaryParser.GetMessage(outcome),
                runResult.CommandResult.ExitCode,
                summary.TestCount,
                summary.Passed,
                summary.Failed,
                summary.Skipped,
                summary.Pending,
                summary.Other,
                summary.DurationMilliseconds,
                [],
                true,
                CompactError(error)
            );
        }

        if (!runResult.ReportFileFound && runResult.CommandResult.ExitCode == 8)
        {
            return new Execution(
                TestOutcome.NotFound,
                plan.Primary.IsWholeProject
                    ? "No tests found."
                    : "No tests matched the method filter.",
                runResult.CommandResult.ExitCode,
                0,
                0,
                0,
                0,
                0,
                0,
                null,
                [],
                true,
                null
            );
        }

        var errorKind = CtrfTestRun.ClassifyErrorKind(
            runResult.CommandResult,
            runResult.ReportFileFound,
            runResult.ReadErrorMessage
        );
        var runError = CtrfTestRun.BuildErrorInfo(
            runResult.CommandResult,
            runResult.ReadErrorMessage,
            errorKind,
            "run_tests",
            plan.Primary.ProjectPath
        );
        return new Execution(
            TestOutcome.Error,
            CompactText(runError.Reason),
            runResult.CommandResult.ExitCode,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            [],
            false,
            CompactError(runError)
        );
    }

    private static Execution CreateExecutionFromReport(
        CtrfReport report,
        CommandResult commandResult,
        string runId,
        string projectPath,
        TestRunStore.RunAccumulator accumulator
    )
    {
        var summary = report.Results.Summary;
        var tests = report.Results.Tests;
        var testCount = summary.Tests > 0 ? summary.Tests : tests.Count;
        var passed = summary.Passed > 0 ? summary.Passed : CountByStatus(tests, "passed");
        var failed = summary.Failed > 0 ? summary.Failed : CountByStatus(tests, "failed");
        var skipped = summary.Skipped > 0 ? summary.Skipped : CountByStatus(tests, "skipped");
        var pending = summary.Pending > 0 ? summary.Pending : CountByStatus(tests, "pending");
        var other = summary.Other > 0 ? summary.Other : CountByStatus(tests, "other");
        var failureIds = new List<string>();

        foreach (
            var test in tests.Where(test =>
                string.Equals(test.Status, "failed", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            var failureId = string.Concat(runId, "-f-", Guid.NewGuid().ToString("N"));
            var failure = new TestRunStore.FailureRecord(
                failureId,
                string.IsNullOrWhiteSpace(test.Name) ? test.Id ?? "<unnamed test>" : test.Name,
                projectPath,
                test.Message,
                test.Trace,
                JoinLines(test.Stdout),
                JoinLines(test.Stderr)
            );
            if (accumulator.AddFailure(failure))
                failureIds.Add(failureId);
        }

        if (commandResult.ExitCode != 0 && commandResult.ExitCode != 8 && failed == 0)
        {
            var errorKind = CtrfTestRun.ClassifyErrorKind(commandResult, true, null);
            var error = CtrfTestRun.BuildErrorInfo(
                commandResult,
                null,
                errorKind,
                "run_tests",
                projectPath
            );
            return new Execution(
                TestOutcome.Error,
                CompactText(error.Reason),
                commandResult.ExitCode,
                testCount,
                passed,
                failed,
                skipped,
                pending,
                other,
                CtrfTestRun.TryParseDurationMilliseconds(summary.Duration),
                failureIds.ToArray(),
                true,
                CompactError(error)
            );
        }

        if (testCount == 0 && tests.Count == 0)
        {
            return new Execution(
                TestOutcome.NotFound,
                "No tests found.",
                commandResult.ExitCode,
                0,
                passed,
                failed,
                skipped,
                pending,
                other,
                CtrfTestRun.TryParseDurationMilliseconds(summary.Duration),
                failureIds.ToArray(),
                true,
                null
            );
        }

        var outcome = failed > 0
            ? TestOutcome.Failed
            : passed > 0
                ? TestOutcome.Passed
                : skipped > 0 || pending > 0
                    ? TestOutcome.Skipped
                    : TestOutcome.Partial;
        var message = outcome switch
        {
            TestOutcome.Failed => "Test run completed with failures.",
            TestOutcome.Passed => "All tests passed.",
            TestOutcome.Skipped => "Tests completed without failures.",
            _ => "Test run completed.",
        };
        return new Execution(
            outcome,
            message,
            commandResult.ExitCode,
            testCount,
            passed,
            failed,
            skipped,
            pending,
            other,
            CtrfTestRun.TryParseDurationMilliseconds(summary.Duration),
            failureIds.ToArray(),
            true,
            null
        );
    }

    private Result CreateExecutionResult(
        string runId,
        IReadOnlyList<PlanResult> executionResults,
        TestRunStore.RunRecord retainedRun
    )
    {
        var retainedFailureIds = retainedRun
            .Failures.Select(value => value.FailureId)
            .ToHashSet(StringComparer.Ordinal);
        var selections = new List<SelectionResult>();
        foreach (var planResult in executionResults)
        {
            var countsCanBeAttributed =
                planResult.Execution.CountsKnown && planResult.Plan.Selectors.Length <= 1;
            var seenSelectors = new HashSet<string>(StringComparer.Ordinal);
            var wholeProjectSeen = false;
            foreach (var request in planResult.Plan.Requests)
            {
                var isPrimary = request.InputIndex == planResult.Plan.Primary.InputIndex;
                var contributesToNativeRun = request.IsWholeProject
                    ? !wholeProjectSeen
                    : request.Selectors.Any(seenSelectors.Add);
                wholeProjectSeen |= request.IsWholeProject;
                var countsKnown = countsCanBeAttributed && isPrimary;
                var selectionFailureIds = countsKnown
                    ? planResult.Execution.FailureIds
                        .Where(retainedFailureIds.Contains)
                        .Take(MaxFailureIdsInResponse)
                        .ToArray()
                    : [];
                selections.Add(
                    new SelectionResult(
                        request.Selection,
                        planResult.Execution.Outcome,
                        CompactText(planResult.Execution.Message) ?? string.Empty,
                        planResult.Execution.ExitCode,
                        countsKnown ? planResult.Execution.TestCount : null,
                        countsKnown ? planResult.Execution.Passed : null,
                        countsKnown ? planResult.Execution.Failed : null,
                        countsKnown ? planResult.Execution.Skipped : null,
                        countsKnown ? planResult.Execution.Pending : null,
                        countsKnown ? planResult.Execution.Other : null,
                        countsKnown ? planResult.Execution.DurationMilliseconds : null,
                        selectionFailureIds,
                        selectionFailureIds.Length > 0,
                        !contributesToNativeRun,
                        countsKnown,
                        CompactError(planResult.Execution.Error)
                    )
                );
            }
        }

        var testCount = executionResults.Sum(value => value.Execution.TestCount);
        var passed = executionResults.Sum(value => value.Execution.Passed);
        var failed = executionResults.Sum(value => value.Execution.Failed);
        var skipped = executionResults.Sum(value => value.Execution.Skipped);
        var pending = executionResults.Sum(value => value.Execution.Pending);
        var other = executionResults.Sum(value => value.Execution.Other);
        int? duration = executionResults.All(value => value.Execution.DurationMilliseconds.HasValue)
            ? executionResults.Sum(value => value.Execution.DurationMilliseconds!.Value)
            : null;
        var outcome = GetAggregateOutcome(executionResults.Select(value => value.Execution).ToArray());
        var failureIds = retainedRun
            .Failures.Select(value => value.FailureId)
            .Take(MaxFailureIdsInResponse)
            .ToArray();
        var error = executionResults
            .Select(value => value.Execution.Error)
            .FirstOrDefault(value => value is not null);
        var compactError = CompactError(error);
        var message = outcome switch
        {
            TestOutcome.Failed => "Test run completed with failures.",
            TestOutcome.Passed => "All selected tests passed.",
            TestOutcome.Skipped => "Selected tests completed without failures.",
            TestOutcome.NotFound => "No selected tests were found.",
            TestOutcome.Partial => "Test execution partially succeeded; see per-selection outcomes.",
            _ => compactError?.Reason ?? "Test execution failed.",
        };
        var exitCode = executionResults
            .Select(value => value.Execution.ExitCode)
            .FirstOrDefault(value => value != 0);
        if (exitCode == 0 && executionResults.Count > 0)
            exitCode = executionResults[0].Execution.ExitCode;

        var projectExecutions = executionResults
            .Select(planResult =>
                new ProjectExecutionResult(
                    planResult.Plan.Primary.ProjectPath,
                    planResult.Execution.Outcome,
                    CompactText(planResult.Execution.Message) ?? string.Empty,
                    planResult.Execution.ExitCode,
                    planResult.Execution.TestCount,
                    planResult.Execution.Passed,
                    planResult.Execution.Failed,
                    planResult.Execution.Skipped,
                    planResult.Execution.Pending,
                    planResult.Execution.Other,
                    planResult.Execution.DurationMilliseconds,
                    planResult.Plan.Selectors,
                    planResult.Execution.FailureIds
                        .Where(retainedFailureIds.Contains)
                        .Take(MaxFailureIdsInResponse)
                        .ToArray(),
                    planResult.Execution.FailureIds.Any(retainedFailureIds.Contains),
                    planResult.Execution.CountsKnown,
                    CompactError(planResult.Execution.Error)
                )
            )
            .ToArray();

        return new Result(
            runId,
            outcome,
            message,
            exitCode,
            testCount,
            passed,
            failed,
            skipped,
            pending,
            other,
            duration,
            selections.ToArray(),
            [],
            projectExecutions,
            failureIds,
            retainedRun.Failures.Length > failureIds.Length,
            retainedRun.Failures.Length > 0,
            retainedRun.OriginalFailureCount,
            retainedRun.Failures.Length,
            retainedRun.FailuresTruncated,
            compactError,
            retainedRun.Output.StandardOutput is not null
                || retainedRun.Output.StandardError is not null
        );
    }

    private static TestRunStore.CapturedOutput ToCapturedOutput(CommandResult commandResult) =>
        new(
            string.IsNullOrEmpty(commandResult.StandardOutput)
                ? null
                : commandResult.StandardOutput,
            string.IsNullOrEmpty(commandResult.StandardError)
                ? null
                : commandResult.StandardError,
            commandResult.StandardOutputTruncated,
            commandResult.StandardErrorTruncated,
            commandResult.StandardOutputOriginalCharacterCount > 0
                ? commandResult.StandardOutputOriginalCharacterCount
                : null,
            commandResult.StandardErrorOriginalCharacterCount > 0
                ? commandResult.StandardErrorOriginalCharacterCount
                : null
        );

    private Result CreateValidationResult(string runId, IReadOnlyList<SelectionIssue> issues) =>
        new(
            runId,
            TestOutcome.Error,
            "Selection validation failed; no tests were executed.",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            [],
            issues.ToArray(),
            [],
            [],
            false,
            false,
            0,
            0,
            false,
            null,
            false
        );

    private void StoreEmptyRun(string runId) =>
        _store.Store(new TestRunStore.RunRecord(runId, default, [], new()));

    private static TestOutcome GetAggregateOutcome(IReadOnlyCollection<Execution> executions)
    {
        if (executions.Count == 0)
            return TestOutcome.NotFound;

        var hasError = executions.Any(value => value.Outcome == TestOutcome.Error);
        var hasFailed = executions.Any(value => value.Outcome == TestOutcome.Failed);
        var hasPassed = executions.Any(value => value.Outcome == TestOutcome.Passed);
        var hasSkipped = executions.Any(value => value.Outcome == TestOutcome.Skipped);
        var hasNotFound = executions.Any(value => value.Outcome == TestOutcome.NotFound);
        var hasPartial = executions.Any(value => value.Outcome == TestOutcome.Partial);

        if (hasError && (hasFailed || hasPassed || hasSkipped || hasNotFound || hasPartial))
            return TestOutcome.Partial;
        if (hasError)
            return TestOutcome.Error;
        if (hasNotFound && (hasFailed || hasPassed || hasSkipped || hasPartial))
            return TestOutcome.Partial;
        if (hasFailed && (hasPassed || hasSkipped || hasNotFound || hasPartial))
            return TestOutcome.Partial;
        if (hasFailed)
            return TestOutcome.Failed;
        if (hasPartial || (hasPassed && hasSkipped))
            return TestOutcome.Partial;
        if (hasPassed)
            return TestOutcome.Passed;
        if (hasSkipped)
            return TestOutcome.Skipped;
        return TestOutcome.NotFound;
    }

    private static int CountByStatus(IReadOnlyCollection<CtrfTest> tests, string status) =>
        tests.Count(test => string.Equals(test.Status, status, StringComparison.OrdinalIgnoreCase));


    private static string? JoinLines(IReadOnlyCollection<string>? lines) =>
        lines is null || lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);

    private static ErrorInfo? CompactError(ErrorInfo? error)
    {
        if (error is null)
            return null;

        return error with
        {
            Reason = CompactText(error.Reason),
            Stdout = CompactText(error.Stdout),
            Stderr = CompactText(error.Stderr),
            Arguments = error.Arguments.Take(32).Select(CompactText).ToArray(),
            ExceptionMessage = CompactNullableText(error.ExceptionMessage),
            ExceptionStackTrace = CompactNullableText(error.ExceptionStackTrace),
        };
    }

    private static TruncatedText? CompactText(TruncatedText? text)
    {
        if (text is null)
            return null;

        var value = text.Text ?? string.Empty;
        var originalCount = Math.Max(text.OriginalCharCount ?? value.Length, value.Length);
        var count = Math.Min(value.Length, MaxCompactDiagnosticCharacters);
        return new TruncatedText(
            value[..count],
            text.Truncated || count < value.Length || originalCount > count,
            originalCount,
            text.OriginalLineCount
        );
    }

    private static string CompactText(string text) =>
        text.Length <= MaxCompactDiagnosticCharacters
            ? text
            : text[..MaxCompactDiagnosticCharacters];

    private static string? CompactNullableText(string? text) =>
        text is null ? null : CompactText(text);

    private static ErrorInfo BuildValidationError(
        string projectPath,
        string reason,
        Exception? exception
    ) => new(ErrorKind.DiscoveryFailed, reason, null, null)
    {
        ProjectPath = projectPath,
        ExceptionType = exception?.GetType().FullName,
        ExceptionMessage = exception?.Message,
        ExceptionStackTrace = exception?.StackTrace,
    };

    private string ResolveWorkingDirectory(string? workingDirectory)
    {
        var candidate = workingDirectory ?? _options.WorkingDirectory;
        return Path.GetFullPath(candidate);
    }


    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool PathsEqual(
        string left,
        string right,
        string effectiveWorkingDirectory
    )
    {
        try
        {
            var leftPath = Path.GetFullPath(left, effectiveWorkingDirectory);
            var rightPath = Path.GetFullPath(right, effectiveWorkingDirectory);
            return PathComparer.Equals(leftPath, rightPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    [Description("Result of a validated, sequential batch test run.")]
    public sealed record Result(
        [property: Description("Identifier used by get_failure_details.")] string RunId,
        [property: Description("Aggregate outcome of all executed selections.")] TestOutcome Outcome,
        [property: Description("Short summary of the batch run.")] string Message,
        [property: Description("First non-zero process exit code, or zero when all succeeded.")]
            int ExitCode,
        [property: Description("Total test cases reported by executed native project runs.")] int TestCount,
        [property: Description("Total passed test cases reported by executed native project runs.")] int Passed,
        [property: Description("Total failed test cases reported by executed native project runs.")] int Failed,
        [property: Description("Total skipped test cases reported by executed native project runs.")] int Skipped,
        [property: Description("Total pending test cases reported by executed native project runs.")]
            int Pending,
        [property: Description("Total other-status test cases reported by executed native project runs.")]
            int Other,
        [property: Description("Aggregate duration when every native project run reported one.")]
            int? DurationMilliseconds,
        [property: Description("Outcome and counts for each requested selection.")] SelectionResult[] Selections,
        [property: Description("Selections that were unmatched, ambiguous, unsupported, or blocked.")]
            SelectionIssue[] UnmatchedSelections,
        [property: Description("One result for each native project execution.")] ProjectExecutionResult[] ProjectExecutions,
        [property: Description("Compact identifiers for retained failure details.")] string[] FailureIds,
        [property: Description("True when more failure identifiers are retained than returned.")]
            bool HasMoreFailures,
        [property: Description("True when at least one retained per-test failure detail exists.")]
            bool FailureDetailsAvailable,
        [property: Description("Number of failed test artifacts before retention bounds.")]
            int OriginalFailureCount,
        [property: Description("Number of failed test artifacts retained for retrieval.")]
            int RetainedFailureCount,
        [property: Description("True when retention bounds dropped failure artifacts.")]
            bool FailuresTruncated,
        [property: Description("First structured execution error, when one occurred.")]
            ErrorInfo? Error,
        [property: Description("True when stdout or stderr was captured for retrieval.")]
            bool CapturedOutputAvailable
    );

    [Description("Outcome and attribution for one native project execution.")]
    public sealed record ProjectExecutionResult(
        string ProjectPath,
        TestOutcome Outcome,
        string Message,
        int ExitCode,
        int TestCount,
        int Passed,
        int Failed,
        int Skipped,
        int Pending,
        int Other,
        int? DurationMilliseconds,
        string[] NativeSelectors,
        string[] FailureIds,
        bool FailureDetailsAvailable,
        bool CountsKnown,
        ErrorInfo? Error
    );

    [Description("Outcome and aggregate counts for one requested selection.")]
    public sealed record SelectionResult(
        TestSelection Selection,
        TestOutcome Outcome,
        string Message,
        int ExitCode,
        int? TestCount,
        int? Passed,
        int? Failed,
        int? Skipped,
        int? Pending,
        int? Other,
        int? DurationMilliseconds,
        string[] FailureIds,
        bool FailureDetailsAvailable,
        bool Deduplicated,
        bool CountsKnown,
        ErrorInfo? Error
    );

    [Description("A selection that could not be executed.")]
    public sealed record SelectionIssue(
        TestSelection Selection,
        TestOutcome Outcome,
        string Reason,
        string[] MatchingTests,
        ErrorInfo? Error
    );

    private sealed record PreparedSelection(
        int InputIndex,
        TestSelection Selection,
        string ProjectPath,
        string CanonicalProjectPath,
        bool IsWholeProject,
        string[] Selectors,
        TestRunnerDialect Dialect,
        TestCatalog.DiscoveredTest? Candidate
    );

    private sealed class ExecutionPlan(
        PreparedSelection primary,
        List<PreparedSelection> requests,
        string[] selectors
    )
    {
        internal PreparedSelection Primary { get; } = primary;
        internal List<PreparedSelection> Requests { get; } = requests;
        internal string[] Selectors { get; } = selectors;
    }

    private sealed record ValidationState(
        IReadOnlyList<PreparedSelection> Prepared,
        IReadOnlyList<ExecutionPlan> Plans,
        IReadOnlyList<SelectionIssue> Issues,
        IReadOnlySet<int> InvalidIndices
    );

    private sealed record PlanResult(ExecutionPlan Plan, Execution Execution);

    private sealed record Execution(
        TestOutcome Outcome,
        string Message,
        int ExitCode,
        int TestCount,
        int Passed,
        int Failed,
        int Skipped,
        int Pending,
        int Other,
        int? DurationMilliseconds,
        string[] FailureIds,
        bool CountsKnown,
        ErrorInfo? Error
    );
}
