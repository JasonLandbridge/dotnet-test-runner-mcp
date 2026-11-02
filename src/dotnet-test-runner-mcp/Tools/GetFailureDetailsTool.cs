using System.ComponentModel;
using DotnetTestRunnerMcp.Models;
using ModelContextProtocol.Server;

namespace DotnetTestRunnerMcp.Tools;

[McpServerToolType]
public sealed class GetFailureDetailsTool(TestRunStore store)
{
    private const int DefaultPageSize = 20;
    private const int MaximumPageSize = 100;
    private const int DefaultMaximumCharacters = 8_000;
    private const int MaximumCharacters = 100_000;
    private const int MaximumIdentityPreviewCharacters = 256;
    private const string IdentityTruncationMarker = "[truncated]";

    private readonly TestRunStore _store = store ?? throw new ArgumentNullException(nameof(store));

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Retrieves bounded failure messages, optional stack traces, and optional captured output retained by run_tests. Use diagnosticField to budget one artifact field independently; failure identifiers are opt-in metadata."
    )]
    public Result GetFailureDetails(
        [Description("Run identifier returned by run_tests or run_changed_tests.")] string runId,
        [Description("Optional failure identifier. Omit to retrieve a page of failures.")] string? failureId = null,
        [Description("Include retained stack traces. Default is false.")] bool includeStackTrace = false,
        [Description("Include retained per-failure and run-level stdout/stderr. Default is false.")] bool includeOutput = false,
        [Description("Zero-based failure page. Default is zero.")] int page = 0,
        [Description("Failures per page, capped at 100. Default is 20.")] int pageSize = DefaultPageSize,
        [Description("Total character budget across returned message, trace, stdout, and stderr fields. Identity previews use a separate fixed cap. Default is 8000.")] int maxCharacters = DefaultMaximumCharacters,
        [Description("Optional independent diagnostic field: message, trace, or output.")] string? diagnosticField = null,
        [Description("Include retained identifiers in the response metadata. Default is false.")] bool includeAvailableFailureIds = false
    )
    {
        var normalizedRunId = string.IsNullOrWhiteSpace(runId) ? string.Empty : runId.Trim();
        var field = NormalizeDiagnosticField(diagnosticField);
        var includeMessage = field is null or "message";
        var includeTrace = field is null ? includeStackTrace : field == "trace";
        var includeFailureOutput = field is null ? includeOutput : field == "output";
        var includeRunOutput = includeFailureOutput;
        page = Math.Max(page, 0);
        pageSize = Math.Clamp(pageSize, 1, MaximumPageSize);
        maxCharacters = Math.Clamp(maxCharacters, 1, MaximumCharacters);

        var invalidField = field is not (null or "message" or "trace" or "output");
        if (invalidField || !_store.TryGet(normalizedRunId, out var run))
        {
            return new Result(
                normalizedRunId,
                TestOutcome.Error,
                invalidField
                    ? "diagnosticField must be one of: message, trace, output, or all (or omitted)."
                    : string.IsNullOrEmpty(normalizedRunId)
                        ? "A run identifier is required."
                        : $"Run '{normalizedRunId}' was not found or has expired.",
                false,
                0,
                0,
                0,
                false,
                page,
                pageSize,
                false,
                [],
                null,
                [],
                includeTrace,
                includeFailureOutput
            );
        }

        var availableFailureIds = includeAvailableFailureIds
            ? run.Failures.Select(value => value.FailureId).ToArray()
            : [];
        if (!string.IsNullOrWhiteSpace(failureId))
        {
            var normalizedFailureId = failureId.Trim();
            var failure = run.Failures.FirstOrDefault(value =>
                string.Equals(value.FailureId, normalizedFailureId, StringComparison.Ordinal)
            );
            if (failure is null)
            {
                var missingOutputBudget = maxCharacters;
                return new Result(
                    normalizedRunId,
                    TestOutcome.Error,
                    $"Failure '{normalizedFailureId}' was not found in run '{normalizedRunId}'.",
                    true,
                    run.Failures.Length,
                    run.OriginalFailureCount,
                    run.Failures.Length,
                    run.FailuresTruncated,
                    0,
                    1,
                    false,
                    [],
                    includeRunOutput
                        ? BuildRunOutput(run.Output, ref missingOutputBudget)
                        : null,
                    availableFailureIds,
                    includeTrace,
                    includeFailureOutput
                );
            }

            var failureBudget = maxCharacters;
            var details = BuildFailure(
                failure,
                includeMessage,
                includeTrace,
                includeFailureOutput,
                ref failureBudget
            );
            var output = includeRunOutput ? BuildRunOutput(run.Output, ref failureBudget) : null;
            return new Result(
                normalizedRunId,
                run.Outcome,
                "Failure details retrieved.",
                true,
                run.Failures.Length,
                run.OriginalFailureCount,
                run.Failures.Length,
                run.FailuresTruncated,
                0,
                1,
                false,
                [details],
                output,
                availableFailureIds,
                includeTrace,
                includeFailureOutput
            );
        }

        var skip = (long)page * pageSize;
        var failureBudgetForPage = maxCharacters;
        var failures = new List<FailureDetail>();
        if (skip < run.Failures.Length)
        {
            var selected = run.Failures.Skip((int)skip).Take(pageSize);
            foreach (var failure in selected)
            {
                failures.Add(
                    BuildFailure(
                        failure,
                        includeMessage,
                        includeTrace,
                        includeFailureOutput,
                        ref failureBudgetForPage
                    )
                );
            }
        }

        var hasMore = skip + failures.Count < run.Failures.Length;
        var runOutput = includeRunOutput
            ? BuildRunOutput(run.Output, ref failureBudgetForPage)
            : null;
        var message = run.Failures.Length == 0
            ? run.Output.StandardOutput is not null || run.Output.StandardError is not null
                ? "Run found; no per-test failure details were retained. Captured output is available."
                : "Run found; no per-test failure details were retained."
            : run.FailuresTruncated
                ? "Failure details retrieved; retention bounds dropped some failure artifacts."
                : "Failure details retrieved.";

        return new Result(
            normalizedRunId,
            run.Outcome,
            message,
            true,
            run.Failures.Length,
            run.OriginalFailureCount,
            run.Failures.Length,
            run.FailuresTruncated,
            page,
            pageSize,
            hasMore,
            failures.ToArray(),
            runOutput,
            availableFailureIds,
            includeTrace,
            includeFailureOutput
        );
    }

    private static string? NormalizeDiagnosticField(string? diagnosticField)
    {
        var normalized = diagnosticField?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized) || normalized == "all")
            return null;

        return normalized;
    }

    private static string? PreviewIdentity(string? value)
    {
        if (value is null || value.Length <= MaximumIdentityPreviewCharacters)
            return value;

        var contentLength =
            MaximumIdentityPreviewCharacters - IdentityTruncationMarker.Length;
        return string.Concat(value[..contentLength], IdentityTruncationMarker);
    }

    private static FailureDetail BuildFailure(
        TestRunStore.FailureRecord failure,
        bool includeMessage,
        bool includeStackTrace,
        bool includeOutput,
        ref int remainingCharacters
    )
    {
        if (
            includeMessage
            && (includeStackTrace || includeOutput)
            && failure.Message?.Length > remainingCharacters
        )
            return BuildFairFailure(
                failure,
                includeMessage,
                includeStackTrace,
                includeOutput,
                ref remainingCharacters
            );

        return new FailureDetail(
            failure.FailureId,
            PreviewIdentity(failure.TestName)!,
            PreviewIdentity(failure.ProjectPath),
            includeMessage
                ? ToTruncatedText(
                    failure.Message,
                    failure.MessageTruncated,
                    failure.MessageOriginalCharacterCount,
                    ref remainingCharacters
                )
                : null,
            includeStackTrace
                ? ToTruncatedText(
                    failure.Trace,
                    failure.TraceTruncated,
                    failure.TraceOriginalCharacterCount,
                    ref remainingCharacters
                )
                : null,
            includeOutput
                ? ToTruncatedText(
                    failure.StandardOutput,
                    failure.StandardOutputTruncated,
                    failure.StandardOutputOriginalCharacterCount,
                    ref remainingCharacters
                )
                : null,
            includeOutput
                ? ToTruncatedText(
                    failure.StandardError,
                    failure.StandardErrorTruncated,
                    failure.StandardErrorOriginalCharacterCount,
                    ref remainingCharacters
                )
                : null,
            failure.Trace is not null,
            failure.StandardOutput is not null || failure.StandardError is not null
        );
    }

    private static FailureDetail BuildFairFailure(
        TestRunStore.FailureRecord failure,
        bool includeMessage,
        bool includeStackTrace,
        bool includeOutput,
        ref int remainingCharacters
    )
    {
        var values = new[]
        {
            includeMessage ? failure.Message : null,
            includeStackTrace ? failure.Trace : null,
            includeOutput ? failure.StandardOutput : null,
            includeOutput ? failure.StandardError : null,
        };
        var alreadyTruncated = new[]
        {
            failure.MessageTruncated,
            failure.TraceTruncated,
            failure.StandardOutputTruncated,
            failure.StandardErrorTruncated,
        };
        var originalCounts = new[]
        {
            failure.MessageOriginalCharacterCount,
            failure.TraceOriginalCharacterCount,
            failure.StandardOutputOriginalCharacterCount,
            failure.StandardErrorOriginalCharacterCount,
        };
        var bounded = BoundDiagnosticFields(
            values,
            alreadyTruncated,
            originalCounts,
            ref remainingCharacters
        );

        return new FailureDetail(
            failure.FailureId,
            PreviewIdentity(failure.TestName)!,
            PreviewIdentity(failure.ProjectPath),
            bounded[0],
            bounded[1],
            bounded[2],
            bounded[3],
            failure.Trace is not null,
            failure.StandardOutput is not null || failure.StandardError is not null
        );
    }

    private static TruncatedText?[] BoundDiagnosticFields(
        string?[] values,
        bool[] alreadyTruncated,
        int?[] originalCounts,
        ref int remainingCharacters
    )
    {
        var lengths = values.Select(value => value?.Length ?? 0).ToArray();
        var present = lengths.Count(length => length > 0);
        var totalLength = lengths.Sum(value => (long)value);
        var target = (int)Math.Min(totalLength, Math.Max(remainingCharacters, 0));
        var allocations = new int[values.Length];
        if (present > 0 && target > 0)
        {
            var minimum = target / present;
            var remainder = target;
            for (var index = 0; index < values.Length; index++)
            {
                if (lengths[index] == 0)
                    continue;

                allocations[index] = Math.Min(lengths[index], minimum);
                remainder -= allocations[index];
            }

            for (var index = 0; index < values.Length && remainder > 0; index++)
            {
                var extra = Math.Min(lengths[index] - allocations[index], remainder);
                allocations[index] += extra;
                remainder -= extra;
            }
        }

        remainingCharacters -= allocations.Sum();
        var bounded = new TruncatedText?[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (value is null)
                continue;

            var originalCount = Math.Max(value.Length, originalCounts[index] ?? value.Length);
            var count = allocations[index];
            bounded[index] = new TruncatedText(
                value[..count],
                alreadyTruncated[index] || count < value.Length || originalCount > count,
                originalCount,
                alreadyTruncated[index] ? null : CountLines(value)
            );
        }

        return bounded;
    }

    private static CapturedOutput BuildRunOutput(
        TestRunStore.CapturedOutput output,
        ref int remainingCharacters
    )
    {
        var bounded = BoundDiagnosticFields(
            [output.StandardOutput, output.StandardError],
            [output.StandardOutputTruncated, output.StandardErrorTruncated],
            [
                output.StandardOutputOriginalCharacterCount,
                output.StandardErrorOriginalCharacterCount,
            ],
            ref remainingCharacters
        );
        return new CapturedOutput(bounded[0], bounded[1]);
    }

    private static TruncatedText? ToTruncatedText(
        string? value,
        bool alreadyTruncated,
        int? originalCharacterCount,
        ref int remainingCharacters
    )
    {
        if (value is null)
            return null;

        var originalCount = Math.Max(value.Length, originalCharacterCount ?? value.Length);
        var count = Math.Min(value.Length, Math.Max(remainingCharacters, 0));
        remainingCharacters -= count;
        var text = value[..count];
        return new TruncatedText(
            text,
            alreadyTruncated || count < value.Length || originalCount > count,
            originalCount,
            alreadyTruncated ? null : CountLines(value)
        );
    }

    private static int CountLines(string value) => value.Count(character => character == '\n') + 1;

    [Description("Result of retrieving retained failure details.")]
    public sealed record Result(
        [property: Description("Run identifier queried.")] string RunId,
        [property: Description("Retained run outcome, or Error for invalid input or a missing identifier.")]
            TestOutcome Outcome,
        [property: Description("Short retrieval status message.")] string Message,
        [property: Description("True when a valid request found the run in the in-memory store.")] bool Found,
        [property: Description("Number of retained per-test failures returned by the store.")]
            int TotalFailures,
        [property: Description("Number of failures reported before retention bounds.")]
            int OriginalFailureCount,
        [property: Description("Number of retained failures available for this run.")]
            int RetainedFailureCount,
        [property: Description("True when retention bounds dropped failure artifacts.")]
            bool FailuresTruncated,
        [property: Description("Zero-based page returned, or zero for a failureId query.")] int Page,
        [property: Description("Effective page size.")] int PageSize,
        [property: Description("True when another retrievable failure page exists; retention truncation alone does not set this.")]
            bool HasMore,
        [property: Description("Returned bounded failure details.")] FailureDetail[] Failures,
        [property: Description("Run-level captured output when includeOutput or diagnosticField=output is requested.")]
            CapturedOutput? RunOutput,
        [property: Description("Retained failure identifiers when includeAvailableFailureIds is true.")]
            string[] AvailableFailureIds,
        [property: Description("Whether stack traces were requested.")] bool StackTraceIncluded,
        [property: Description("Whether stdout/stderr were requested.")] bool OutputIncluded
    );

    [Description("One bounded retained failure artifact.")]
    public sealed record FailureDetail(
        [property: Description("Opaque retained failure identifier; never preview-truncated.")] string FailureId,
        [property: Description("Bounded test-name preview; a [truncated] suffix indicates clipping.")] string TestName,
        [property: Description("Bounded project-path preview; a [truncated] suffix indicates clipping.")] string? ProjectPath,
        TruncatedText? Message,
        TruncatedText? StackTrace,
        TruncatedText? StandardOutput,
        TruncatedText? StandardError,
        bool StackTraceAvailable,
        bool OutputAvailable
    );

    [Description("Run-level captured process output.")]
    public sealed record CapturedOutput(TruncatedText? StandardOutput, TruncatedText? StandardError);
}
