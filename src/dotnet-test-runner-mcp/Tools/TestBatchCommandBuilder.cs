namespace DotnetTestRunnerMcp.Tools;

internal static class TestBatchCommandBuilder
{
    internal static bool TryBuild(
        TestRunnerDialect dialect,
        string projectPath,
        IReadOnlyCollection<string> selectors,
        out string[] arguments,
        out string reason
    )
    {
        ArgumentNullException.ThrowIfNull(projectPath);
        ArgumentNullException.ThrowIfNull(selectors);

        var distinctSelectors = selectors
            .Where(selector => !string.IsNullOrWhiteSpace(selector))
            .Select(selector => selector.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (distinctSelectors.Length == 0)
        {
            if (dialect == TestRunnerDialect.Unknown)
            {
                arguments = [];
                reason = "Project execution requires a known runner dialect.";
                return false;
            }

            arguments = TestCommandBuilder.BuildProjectRun(dialect, projectPath);
            reason = string.Empty;
            return true;
        }

        if (
            !TestCommandBuilder.TryBuildMethodUnion(
                dialect,
                distinctSelectors,
                out var filter,
                out reason
            )
        )
        {
            arguments = [];
            return false;
        }

        var built = TestCommandBuilder.BuildSingleTestRun(
            dialect,
            distinctSelectors[0],
            projectPath,
            exactMatch: true
        );
        var filterArgument = dialect == TestRunnerDialect.TUnit ? "--treenode-filter" : "--filter";
        var filterIndex = Array.IndexOf(built, filterArgument);
        if (filterIndex < 0 || filterIndex + 1 >= built.Length)
        {
            arguments = [];
            reason = $"The {dialect} runner did not produce a native method filter.";
            return false;
        }

        built[filterIndex + 1] = filter;
        arguments = built;
        reason = string.Empty;
        return true;
    }
}
