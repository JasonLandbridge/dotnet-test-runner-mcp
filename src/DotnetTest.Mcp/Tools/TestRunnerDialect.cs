namespace DotnetTest.Mcp.Tools;

internal enum TestRunnerDialect
{
    Unknown,
    TUnit,
    VSTest,
}

internal static class TestRunnerDialectDetector
{
    internal static TestRunnerDialect Detect(string? projectPath, McpOptions options, string? workingDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return TestRunnerDialect.Unknown;

        var effectiveWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? options.WorkingDirectory : workingDirectory;

        var fullPath = Path.IsPathRooted(projectPath) ? projectPath : Path.Combine(effectiveWorkingDirectory, projectPath);

        if (!File.Exists(fullPath))
            return TestRunnerDialect.Unknown;

        var projectXml = File.ReadAllText(fullPath);
        if (ContainsPackage(projectXml, "TUnit")
            || ContainsPackage(projectXml, "TUnit.Core")
            || ContainsPackage(projectXml, "TUnit.Assertions")
            || ContainsPackage(projectXml, "TUnit.Engine"))
            return TestRunnerDialect.TUnit;

        if (ContainsPackage(projectXml, "Microsoft.NET.Test.Sdk")
            || ContainsPackage(projectXml, "xunit.runner.visualstudio")
            || ContainsPackage(projectXml, "MSTest.TestAdapter")
            || ContainsPackage(projectXml, "NUnit3TestAdapter"))
            return TestRunnerDialect.VSTest;

        return TestRunnerDialect.Unknown;
    }

    private static bool ContainsPackage(string projectXml, string packageName)
        => projectXml.Contains($"Include=\"{packageName}\"", StringComparison.OrdinalIgnoreCase)
            || projectXml.Contains($"Include='{packageName}'", StringComparison.OrdinalIgnoreCase);
}

internal static class TestCommandBuilder
{
    internal static string[] BuildClassRun(TestRunnerDialect dialect, string className, string? project)
    {
        if (dialect == TestRunnerDialect.TUnit)
        {
            var shortClassName = ExtractTypeName(className);
            var arguments = BuildTestCommandPrefix(dialect, project);
            arguments.AddRange(["--treenode-filter", $"/*/*/{ValidateTreeNodeSegment(shortClassName, nameof(className))}/*",]);
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

    internal static string[] BuildSingleTestRun(TestRunnerDialect dialect, string testName, string? project)
    {
        if (dialect == TestRunnerDialect.TUnit)
        {
            var methodName = ExtractMethodName(testName);
            var arguments = BuildTestCommandPrefix(dialect, project);
            arguments.AddRange(["--treenode-filter", $"/*/*/*/{ValidateTreeNodeSegment(methodName, nameof(testName))}",]);
            return arguments.ToArray();
        }

        if (dialect == TestRunnerDialect.VSTest)
        {
            var arguments = BuildTestCommandPrefix(dialect, project);
            arguments.AddRange(["--filter", $"FullyQualifiedName~{testName}"]);
            return arguments.ToArray();
        }

        if (!string.IsNullOrWhiteSpace(project))
            return ["test", project, "--filter-method", testName];

        return ["test", "--filter-method", testName];
    }

    internal static string[] BuildProjectRun(TestRunnerDialect dialect, string project) => BuildTestCommandPrefix(dialect, project).ToArray();

    private static List<string> BuildTestCommandPrefix(TestRunnerDialect dialect, string? project)
    {
        if (dialect == TestRunnerDialect.TUnit)
        {
            if (!string.IsNullOrWhiteSpace(project))
            {
                return
                [
                    "run",
                    "--project",
                    project,
                    "--",
                    "--no-ansi",
                    "--disable-logo",
                ];
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
        return methodSeparator < 0 || methodSeparator == testName.Length - 1 ? testName : testName[(methodSeparator + 1)..];
    }

    private static string ExtractTypeName(string typeName)
    {
        var classSeparator = typeName.LastIndexOf('.');
        return classSeparator < 0 || classSeparator == typeName.Length - 1 ? typeName : typeName[(classSeparator + 1)..];
    }

    private static string ValidateTreeNodeSegment(string segment, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(segment))
            throw new ArgumentException("TUnit tree-node filter segment cannot be empty.", parameterName);

        if (segment.Contains('/', StringComparison.Ordinal) || segment.Contains("**", StringComparison.Ordinal))
            throw new ArgumentException("TUnit tree-node filter segment cannot contain '/' or '**'.", parameterName);

        return segment;
    }
}