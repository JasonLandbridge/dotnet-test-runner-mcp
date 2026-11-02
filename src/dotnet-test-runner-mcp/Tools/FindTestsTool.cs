using System.ComponentModel;
using System.Text;
using System.Text.Json;
using DotnetTestRunnerMcp.Extensions;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DotnetTestRunnerMcp.Tools;

[McpServerToolType]
public sealed class FindTestsTool
{
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 200;
    private const int CursorVersion = 1;

    private readonly McpOptions _options;
    private readonly TestCatalog _catalog;
    private readonly TestDiscoverySnapshotStore _snapshotStore;

    public FindTestsTool(
        IOptions<McpOptions> options,
        ICommandRunner commandRunner,
        ProjectMetadataReader? metadataReader = null,
        TestDiscoverySnapshotStore? snapshotStore = null
    )
    {
        _options = options.ValidateNotNull().Value;
        _catalog = new TestCatalog(options, commandRunner, metadataReader);
        _snapshotStore = snapshotStore ?? new TestDiscoverySnapshotStore();
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Finds discovered tests by case-insensitive substring, preserving complete display names and executable method selectors. "
            + "Cursor pages use one immutable discovery snapshot retained for 10 minutes; source changes are not reflected until a new search."
    )]
    public async Task<Result> FindTests(
        [Description("Optional case-insensitive substring matched against display names and selectors.")] string? query = null,
        [Description("Optional project path to scope discovery to one project.")] string? projectPath = null,
        [Description("Maximum matches to return per page (1-200, default 50).")]
            int pageSize = DefaultPageSize,
        [Description("Opaque cursor returned by a previous page; cursors expire after 10 minutes.")] string? cursor = null,
        [Description("Optional working directory to run dotnet commands from.")] string? workingDirectory = null,
        CancellationToken cancellationToken = default
    )
    {
        var trimmedQuery = TrimOrNull(query);
        var trimmedProjectPath = TrimOrNull(projectPath);
        var effectiveWorkingDirectory = ResolveWorkingDirectory(workingDirectory);
        var boundedPageSize = Math.Clamp(pageSize, 1, MaximumPageSize);
        var trimmedCursor = TrimOrNull(cursor);

        TestCatalog.CatalogDiscovery discovery;
        string snapshotId;
        var offset = 0;
        if (trimmedCursor is null)
        {
            discovery = await _catalog.DiscoverAsync(
                trimmedProjectPath,
                effectiveWorkingDirectory,
                cancellationToken
            );
            snapshotId = _snapshotStore.Create(discovery) ?? string.Empty;
            if (snapshotId.Length == 0)
            {
                const string reason =
                    "The discovered catalog is too large for the bounded snapshot store; narrow the query or project scope.";
                return CreateErrorResult(
                    reason,
                    trimmedQuery,
                    trimmedProjectPath,
                    effectiveWorkingDirectory,
                    boundedPageSize,
                    discovery.ProjectResults,
                    new ErrorInfo(ErrorKind.InvocationError, reason, null, null)
                );
            }

            var stored = _snapshotStore.Get(snapshotId);
            if (
                stored.Status != TestDiscoverySnapshotStore.SnapshotLookupStatus.Found
                || stored.Discovery is null
            )
            {
                const string reason = "The fresh discovery snapshot could not be retained.";
                return CreateErrorResult(
                    reason,
                    trimmedQuery,
                    trimmedProjectPath,
                    effectiveWorkingDirectory,
                    boundedPageSize,
                    discovery.ProjectResults,
                    new ErrorInfo(ErrorKind.InvocationError, reason, null, null)
                );
            }

            discovery = stored.Discovery;
        }
        else
        {
            if (
                !TryReadCursor(
                    trimmedCursor,
                    trimmedQuery,
                    trimmedProjectPath,
                    effectiveWorkingDirectory,
                    out var state,
                    out var cursorError
                )
            )
            {
                return CreateErrorResult(
                    cursorError,
                    trimmedQuery,
                    trimmedProjectPath,
                    effectiveWorkingDirectory,
                    boundedPageSize,
                    [],
                    new ErrorInfo(ErrorKind.InvocationError, cursorError, null, null)
                );
            }

            var stored = _snapshotStore.Get(state.SnapshotId);
            if (stored.Status != TestDiscoverySnapshotStore.SnapshotLookupStatus.Found)
            {
                var reason = stored.Status == TestDiscoverySnapshotStore.SnapshotLookupStatus.Expired
                    ? "The discovery cursor has expired; start a new search."
                    : "The discovery cursor is no longer available; start a new search.";
                return CreateErrorResult(
                    reason,
                    trimmedQuery,
                    trimmedProjectPath,
                    effectiveWorkingDirectory,
                    boundedPageSize,
                    [],
                    new ErrorInfo(ErrorKind.InvocationError, reason, null, null)
                );
            }

            discovery = stored.Discovery!;
            snapshotId = state.SnapshotId;
            offset = state.Offset;
        }

        var matches = discovery
            .Tests.Where(test =>
                (trimmedProjectPath is null || PathsEqual(test.ProjectPath, trimmedProjectPath))
                && (
                    trimmedQuery is null
                    || test.DisplayName.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)
                    || test.Selector.Contains(trimmedQuery, StringComparison.OrdinalIgnoreCase)
                )
            )
            .ToArray();

        if (offset > matches.Length)
        {
            const string reason = "The cursor offset is outside the fixed snapshot result set.";
            return CreateErrorResult(
                reason,
                trimmedQuery,
                trimmedProjectPath,
                effectiveWorkingDirectory,
                boundedPageSize,
                discovery.ProjectResults,
                new ErrorInfo(ErrorKind.InvocationError, reason, null, null)
            );
        }

        var page = matches
            .Skip(offset)
            .Take(boundedPageSize)
            .Select(ToPublicTest)
            .ToArray();
        var hasMore = offset + page.Length < matches.Length;
        var nextCursor = hasMore
            ? CreateCursor(
                new CursorState(
                    CursorVersion,
                    snapshotId,
                    trimmedQuery,
                    trimmedProjectPath,
                    effectiveWorkingDirectory,
                    offset + page.Length
                )
            )
            : null;

        var projectErrors = discovery.ProjectResults
            .Where(result => result.Outcome == TestOutcome.Error)
            .ToArray();
        var hasSuccessfulProject = discovery.ProjectResults.Any(
            result => result.Outcome != TestOutcome.Error
        );
        var partialDiscovery = projectErrors.Length > 0 && hasSuccessfulProject;
        var outcome = projectErrors.Length > 0
            ? partialDiscovery
                ? TestOutcome.Partial
                : TestOutcome.Error
            : matches.Length == 0
                ? TestOutcome.NotFound
                : TestOutcome.Passed;
        var message = projectErrors.Length > 0
            ? partialDiscovery
                ? "Test discovery partially succeeded. See ProjectResults for project errors."
                : "Test discovery failed for all requested projects."
            : matches.Length == 0
                ? "No discovered tests matched the query."
                : $"Found {matches.Length} matching tests.";
        var overallError = outcome == TestOutcome.Error
            ? projectErrors.Select(result => result.Error).FirstOrDefault(error => error is not null)
                ?? new ErrorInfo(ErrorKind.DiscoveryFailed, message, null, null)
            : null;

        return new Result(
            outcome,
            message,
            trimmedQuery,
            trimmedProjectPath,
            effectiveWorkingDirectory,
            boundedPageSize,
            matches.Length,
            page.Length,
            hasMore,
            nextCursor,
            page,
            CloneProjectResults(discovery.ProjectResults),
            overallError
        );
    }

    private static Result CreateErrorResult(
        string reason,
        string? query,
        string? projectPath,
        string workingDirectory,
        int pageSize,
        ProjectDiscoveryResult[] projectResults,
        ErrorInfo error
    ) => new(
        TestOutcome.Error,
        reason,
        query,
        projectPath,
        workingDirectory,
        pageSize,
        0,
        0,
        false,
        null,
        [],
        CloneProjectResults(projectResults),
        error
    );

    private static ProjectDiscoveryResult[] CloneProjectResults(
        IReadOnlyCollection<ProjectDiscoveryResult> projectResults
    ) =>
        projectResults
            .Select(result =>
                result with
                {
                    Error = result.Error is null
                        ? null
                        : result.Error with
                        {
                            Arguments = result.Error.Arguments.ToArray(),
                            Stdout = result.Error.Stdout is null ? null : result.Error.Stdout with { },
                            Stderr = result.Error.Stderr is null ? null : result.Error.Stderr with { },
                        },
                }
            )
            .ToArray();

    private static FoundTest ToPublicTest(TestCatalog.DiscoveredTest test)
    {
        var dialect = test.Dialect.ToString();
        var selectorSupported = TestCommandBuilder.TryValidateMethodSelector(
            test.Dialect,
            test.Selector,
            out var selectorReason
        );
        var limitation = selectorSupported
            ? test.IsAmbiguous
                ? "The selector is runner-supported but does not identify exactly one executable case."
                : null
            : string.IsNullOrWhiteSpace(selectorReason)
                ? "The runner cannot verify this method selector."
                : selectorReason;
        return new FoundTest(
            test.ProjectPath,
            test.DisplayName,
            test.Selector,
            dialect,
            test.IsAmbiguous,
            selectorSupported,
            limitation
        );
    }

    private static string CreateCursor(CursorState state)
    {
        var json = JsonSerializer.Serialize(state);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryReadCursor(
        string cursor,
        string? query,
        string? projectPath,
        string workingDirectory,
        out CursorState state,
        out string error
    )
    {
        state = default!;
        error = string.Empty;
        try
        {
            var encoded = cursor.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            state = JsonSerializer.Deserialize<CursorState>(
                Encoding.UTF8.GetString(Convert.FromBase64String(encoded))
            )!;
            if (state is null)
            {
                error = "The cursor is invalid.";
                return false;
            }

            if (state.Version != CursorVersion)
            {
                error = "The cursor version is unsupported.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(state.SnapshotId))
            {
                error = "The cursor snapshot identifier is invalid.";
                return false;
            }

            if (
                !string.Equals(state.Query, query, StringComparison.Ordinal)
                || !string.Equals(state.ProjectPath, projectPath, StringComparison.Ordinal)
                || !string.Equals(state.WorkingDirectory, workingDirectory, StringComparison.Ordinal)
            )
            {
                error = "The cursor does not match the requested query, project path, or working directory.";
                return false;
            }

            if (state.Offset < 0)
            {
                error = "The cursor offset is invalid.";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            error = "The cursor is invalid.";
            return false;
        }
        catch (FormatException)
        {
            error = "The cursor is invalid.";
            return false;
        }
        catch (ArgumentException)
        {
            error = "The cursor is invalid.";
            return false;
        }
        catch (NotSupportedException)
        {
            error = "The cursor is invalid.";
            return false;
        }
    }

    private string ResolveWorkingDirectory(string? workingDirectory)
    {
        var requested = TrimOrNull(workingDirectory) ?? TrimOrNull(_options.WorkingDirectory);
        return Path.GetFullPath(requested ?? Directory.GetCurrentDirectory());
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );

    internal sealed record CursorState(
        int Version,
        string SnapshotId,
        string? Query,
        string? ProjectPath,
        string WorkingDirectory,
        int Offset
    );

    [Description("A discovered test with its complete display name and executable selector metadata.")]
    public sealed record FoundTest(
        [property: Description("Project containing the discovered test.")] string ProjectPath,
        [property: Description("Complete runner display name, including parameters when present.")]
            string DisplayName,
        [property: Description("Executable method selector with parameter suffix removed when applicable.")]
            string Selector,
        [property: Description("Detected runner dialect: TUnit, VSTest, MTP, or Unknown.")] string Dialect,
        [property: Description("True when this selector cannot be guaranteed to identify one case.")]
            bool IsAmbiguous,
        [property: Description("True when the detected runner supports method selection syntax.")]
            bool SelectorSupported,
        [property: Description("Limitation explaining ambiguity or unknown runner support.")]
            string? Limitation
    );

    [Description(
        "Paged test-discovery results. Cursors page one immutable snapshot for up to 10 minutes; expired cursors must start a new search."
    )]
    public sealed record Result(
        [property: Description("Overall discovery outcome.")] TestOutcome Outcome,
        [property: Description("Human-readable discovery and paging summary.")] string Message,
        [property: Description("Normalized case-insensitive substring query, or null.")] string? Query,
        [property: Description("Project path filter, or null for all discovered projects.")]
            string? ProjectPath,
        [property: Description("Canonical effective working directory used for discovery.")]
            string? WorkingDirectory,
        [property: Description("Bounded page size used for this response.")] int PageSize,
        [property: Description("Total matches across the complete fixed discovery snapshot.")] int TotalMatches,
        [property: Description("Number of tests returned in this page.")] int ReturnedCount,
        [property: Description("True when another page is available in the fixed snapshot.")]
            bool HasMore,
        [property: Description("Opaque cursor for the next page, or null; cursors expire after 10 minutes.")]
            string? NextCursor,
        [property: Description("Tests returned in deterministic order.")] FoundTest[] Tests,
        [property: Description("Per-project discovery results, including partial errors.")]
            ProjectDiscoveryResult[] ProjectResults,
        [property: Description("Structured error when discovery or cursor validation fails.")]
            ErrorInfo? Error
    );
}
