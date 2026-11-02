namespace DotnetTestRunnerMcp.Tools;

internal enum TestRunnerDialect
{
    Unknown,
    TUnit,
    VSTest,
    MTP,
}

internal static class TestCommandBuilder
{
    internal static string[] BuildClassRun(
        TestRunnerDialect dialect,
        string className,
        string? project
    )
    {
        if (dialect == TestRunnerDialect.TUnit)
        {
            var shortClassName = ExtractTypeName(className);
            var arguments = BuildTestCommandPrefix(dialect, project);
            arguments.AddRange([
                "--treenode-filter",
                $"/*/*/{ValidateTreeNodeSegment(shortClassName, nameof(className))}/*",
            ]);
            return arguments.ToArray();
        }

        if (dialect == TestRunnerDialect.VSTest)
        {
            var arguments = BuildTestCommandPrefix(dialect, project);
            arguments.AddRange(["--filter", $"FullyQualifiedName~{className}"]);
            return arguments.ToArray();
        }

        if (!string.IsNullOrWhiteSpace(project))
            return ["test", project, "--filter-class", className];

        return ["test", "--filter-class", className];
    }

    internal static string[] BuildSingleTestRun(
        TestRunnerDialect dialect,
        string testName,
        string? project,
        bool exactMatch = false
    )
    {
        if (dialect == TestRunnerDialect.TUnit)
        {
            var methodName = ExtractMethodName(testName);
            var arguments = BuildTestCommandPrefix(dialect, project);
            arguments.AddRange([
                "--treenode-filter",
                $"/*/*/*/{ValidateTreeNodeSegment(methodName, nameof(testName))}",
            ]);
            return arguments.ToArray();
        }

        if (dialect == TestRunnerDialect.VSTest)
        {
            var arguments = BuildTestCommandPrefix(dialect, project);
            arguments.AddRange([
                "--filter",
                exactMatch
                    ? $"FullyQualifiedName={testName}"
                    : $"FullyQualifiedName~{testName}",
            ]);
            return arguments.ToArray();
        }

        if (!string.IsNullOrWhiteSpace(project))
            return ["test", project, "--filter-method", testName];

        return ["test", "--filter-method", testName];
    }

    /// <summary>
    /// Builds one native method filter for a set of already-discovered selectors.
    /// </summary>
    internal static bool TryBuildMethodUnion(
        TestRunnerDialect dialect,
        IReadOnlyCollection<string> selectors,
        out string filter,
        out string reason
    )
    {
        filter = string.Empty;
        reason = string.Empty;
        if (selectors.Count == 0)
        {
            reason = "At least one method selector is required.";
            return false;
        }

        var distinctSelectors = selectors
            .Where(selector => !string.IsNullOrWhiteSpace(selector))
            .Select(selector => selector.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinctSelectors.Length == 0)
        {
            reason = "At least one non-empty method selector is required.";
            return false;
        }

        foreach (var selector in distinctSelectors)
        {
            if (!TryValidateMethodSelector(dialect, selector, out reason))
                return false;
        }

        if (dialect == TestRunnerDialect.VSTest)
        {
            filter = string.Join(
                "|",
                distinctSelectors.Select(selector => $"FullyQualifiedName={selector}")
            );
            return true;
        }

        if (dialect == TestRunnerDialect.TUnit)
        {
            var methods = distinctSelectors
                .Select(ExtractMethodName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            // TUnit 1.45.29 treats parenthesized methods as literal pre-filter hints.
            // AND with match-all bypasses that hint without broadening the exact union:
            // https://github.com/thomhurst/TUnit/issues/6026
            filter = methods.Length == 1
                ? $"/*/*/*/{methods[0]}"
                : $"/*/*/*/({string.Join("|", methods.Select(method => $"({method})"))})&(*)";
            return true;
        }

        reason = "Method unions require a known runner dialect.";
        return false;
    }

    internal static bool TryValidateMethodSelector(
        TestRunnerDialect dialect,
        string selector,
        out string reason
    )
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(selector))
        {
            reason = "The method selector cannot be empty.";
            return false;
        }

        if (dialect == TestRunnerDialect.VSTest)
        {
            if (!selector.Contains('.', StringComparison.Ordinal))
            {
                reason =
                    $"Exact VSTest selection '{selector}' is unsupported because discovery returned a short name.";
                return false;
            }

            if (ContainsUnsafeFilterSyntax(selector))
            {
                reason =
                    $"Exact VSTest selection '{selector}' contains filter syntax and cannot be used safely.";
                return false;
            }

            return true;
        }

        if (dialect == TestRunnerDialect.TUnit)
        {
            var methodName = ExtractMethodName(selector);
            if (ContainsUnsafeFilterSyntax(methodName))
            {
                reason =
                    $"Exact TUnit selection '{selector}' contains tree-filter syntax and is unsupported.";
                return false;
            }

            try
            {
                _ = ValidateTreeNodeSegment(methodName, nameof(selector));
                return true;
            }
            catch (ArgumentException exception)
            {
                reason = $"Exact TUnit selection '{selector}' is unsupported: {exception.Message}";
                return false;
            }
        }

        reason =
            $"Exact method selection '{selector}' is unsupported because the runner dialect is unknown.";
        return false;
    }

    internal static string[] BuildProjectRun(TestRunnerDialect dialect, string project) =>
        BuildTestCommandPrefix(dialect, project).ToArray();

    private static List<string> BuildTestCommandPrefix(TestRunnerDialect dialect, string? project)
    {
        if (dialect == TestRunnerDialect.MTP)
        {
            if (!string.IsNullOrWhiteSpace(project))
                return ["run", "--project", project, "--"];

            return ["run", "--"];
        }
        if (dialect == TestRunnerDialect.TUnit)
        {
            if (!string.IsNullOrWhiteSpace(project))
            {
                return ["run", "--project", project, "--", "--no-ansi", "--disable-logo"];
            }

            return ["test", "--", "--no-ansi", "--disable-logo"];
        }

        if (!string.IsNullOrWhiteSpace(project))
            return ["test", project];

        return ["test"];
    }

    private static string ExtractMethodName(string testName)
    {
        var methodSeparator = testName.LastIndexOf('.');
        return methodSeparator < 0 || methodSeparator == testName.Length - 1
            ? testName
            : testName[(methodSeparator + 1)..];
    }

    private static string ExtractTypeName(string typeName)
    {
        var classSeparator = typeName.LastIndexOf('.');
        return classSeparator < 0 || classSeparator == typeName.Length - 1
            ? typeName
            : typeName[(classSeparator + 1)..];
    }

    private static string ValidateTreeNodeSegment(string segment, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(segment))
            throw new ArgumentException(
                "TUnit tree-node filter segment cannot be empty.",
                parameterName
            );

        if (
            segment.Contains('/', StringComparison.Ordinal)
            || segment.Contains("**", StringComparison.Ordinal)
        )
            throw new ArgumentException(
                "TUnit tree-node filter segment cannot contain '/' or '**'.",
                parameterName
            );

        return segment;
    }

    private static bool ContainsUnsafeFilterSyntax(string value) =>
        value.Any(
            character =>
                character
                    is '|'
                        or '&'
                        or '!'
                        or '='
                        or '~'
                        or '(' 
                        or ')'
                        or '\\'
                        or ';'
                        or ','
                        or '['
                        or ']'
                        or '*'
                        or '?'
                        or '/'
        );
}
