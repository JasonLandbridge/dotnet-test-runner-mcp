using DotnetTestRunnerMcp.Extensions;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using Microsoft.Extensions.Options;

namespace DotnetTestRunnerMcp.Tools;

internal sealed class TestCatalog
{
    private readonly McpOptions _options;
    private readonly ICommandRunner _commandRunner;
    private readonly ProjectMetadataReader _metadataReader;
    private readonly ListTestsSummaryTool _summaryTool;

    internal TestCatalog(
        IOptions<McpOptions> options,
        ICommandRunner commandRunner,
        ProjectMetadataReader? metadataReader = null
    )
    {
        _options = options.ValidateNotNull().Value;
        _commandRunner = commandRunner.ValidateNotNull();
        _metadataReader =
            metadataReader ?? new ProjectMetadataReader(commandRunner, options);
        _summaryTool = new ListTestsSummaryTool(options, commandRunner, _metadataReader);
    }

    internal async Task<CatalogDiscovery> DiscoverAsync(
        string? projectPath,
        string? workingDirectory,
        CancellationToken cancellationToken
    )
    {
        var trimmedProjectPath = string.IsNullOrWhiteSpace(projectPath) ? null : projectPath.Trim();
        var trimmedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : workingDirectory.Trim();

        string[] projectPaths;
        if (trimmedProjectPath is not null)
        {
            projectPaths = [trimmedProjectPath];
        }
        else
        {
            try
            {
                projectPaths = await TestProjectDiscovery.ListAsync(
                    _commandRunner,
                    _options,
                    trimmedWorkingDirectory,
                    cancellationToken,
                    _metadataReader
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var error = BuildDiscoveryError(
                    "<solution>",
                    $"Test project discovery failed: {exception.Message}",
                    exception
                );
                return new CatalogDiscovery(
                    [],
                    [
                        new ProjectDiscoveryResult(
                            "<solution>",
                            TestOutcome.Error,
                            error.Reason,
                            -1,
                            0,
                            null,
                            error
                        ),
                    ]
                );
            }
        }

        var projectComparison = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var orderedProjects = projectPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(projectComparison)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var projectResults = new List<ProjectDiscoveryResult>(orderedProjects.Length);
        var discovered = new List<DiscoveredTest>();
        foreach (var path in orderedProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var discovery = await _summaryTool.DiscoverProjectTests(
                    path,
                    null,
                    false,
                    trimmedWorkingDirectory,
                    cancellationToken
                );
                var dialect = await _metadataReader.GetDialectAsync(
                    path,
                    trimmedWorkingDirectory,
                    cancellationToken
                );
                projectResults.Add(discovery.ProjectResult);
                discovered.AddRange(
                    discovery.Tests.Select(displayName =>
                        new DiscoveredTest(
                            path,
                            displayName,
                            BuildSelector(displayName, dialect),
                            dialect,
                            HasParameterSuffix(displayName)
                        )
                    )
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var error = BuildDiscoveryError(
                    path,
                    $"Test discovery failed for project '{path}': {exception.Message}",
                    exception
                );
                projectResults.Add(
                    new ProjectDiscoveryResult(
                        path,
                        TestOutcome.Error,
                        error.Reason,
                        -1,
                        0,
                        null,
                        error
                    )
                );
            }
        }

        var ambiguity = discovered
            .GroupBy(test => (test.ProjectPath, test.Selector))
            .Where(group => group.Count() > 1)
            .Select(group => (group.Key.ProjectPath, group.Key.Selector))
            .ToHashSet();

        var tests = discovered
            .Select(test =>
                test with
                {
                    IsAmbiguous = test.IsAmbiguous
                        || ambiguity.Contains((test.ProjectPath, test.Selector))
                        || IsShortOrUnknown(test),
                }
            )
            .OrderBy(test => test.ProjectPath, StringComparer.Ordinal)
            .ThenBy(test => test.DisplayName, StringComparer.Ordinal)
            .ThenBy(test => test.Selector, StringComparer.Ordinal)
            .ThenBy(test => test.Dialect)
            .ToArray();

        var orderedResults = projectResults
            .OrderBy(result => result.ProjectPath, StringComparer.Ordinal)
            .ToArray();
        return new CatalogDiscovery(tests, orderedResults);
    }

    private static string BuildSelector(string displayName, TestRunnerDialect dialect)
    {
        var selector = displayName.Trim();
        if (dialect == TestRunnerDialect.Unknown)
            return selector;

        var parameterStart = selector.IndexOf('(');
        if (parameterStart >= 0 && selector.EndsWith(')'))
            selector = selector[..parameterStart].TrimEnd();
        return selector;
    }

    private static bool HasParameterSuffix(string displayName)
    {
        var parameterStart = displayName.IndexOf('(');
        return parameterStart >= 0 && displayName.TrimEnd().EndsWith(')');
    }

    private static bool IsShortOrUnknown(DiscoveredTest test) =>
        test.Dialect is TestRunnerDialect.Unknown or TestRunnerDialect.MTP
        || test.Dialect == TestRunnerDialect.TUnit
        || !test.Selector.Contains('.', StringComparison.Ordinal);

    private static ErrorInfo BuildDiscoveryError(
        string projectPath,
        string reason,
        Exception exception
    ) => new(ErrorKind.DiscoveryFailed, reason, null, null)
    {
        ProjectPath = projectPath,
        ExceptionType = exception.GetType().FullName,
        ExceptionMessage = exception.Message,
        ExceptionStackTrace = exception.StackTrace,
    };

    internal sealed record CatalogDiscovery(
        DiscoveredTest[] Tests,
        ProjectDiscoveryResult[] ProjectResults
    );

    internal sealed record DiscoveredTest(
        string ProjectPath,
        string DisplayName,
        string Selector,
        TestRunnerDialect Dialect,
        bool IsAmbiguous
    );

}
