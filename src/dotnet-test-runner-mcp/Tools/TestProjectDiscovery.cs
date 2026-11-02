using DotnetTestRunnerMcp.Terminal;
using Microsoft.Extensions.Options;

namespace DotnetTestRunnerMcp.Tools;

internal static class TestProjectDiscovery
{
    internal static async Task<string[]> ListAsync(
        ICommandRunner commandRunner,
        McpOptions options,
        string? workingDirectory,
        CancellationToken cancellationToken,
        ProjectMetadataReader? metadataReader = null
    )
    {
        var candidates = await ListCandidatesAsync(
            commandRunner,
            options,
            workingDirectory,
            cancellationToken
        );

        var reader = metadataReader ?? new ProjectMetadataReader(
            commandRunner,
            Options.Create(options)
        );
        var testProjects = new List<string>(candidates.Length);
        foreach (var project in candidates)
        {
            var metadata = await reader.ReadProjectAsync(
                project,
                workingDirectory,
                cancellationToken
            );
            if (!metadata.EvaluationSucceeded)
            {
                throw new InvalidOperationException(
                    $"dotnet msbuild metadata evaluation failed for project '{project}': "
                        + $"{metadata.EvaluationError ?? "No diagnostic output."}"
                );
            }

            if (!IsExplicitlyFalse(metadata))
                testProjects.Add(project);
        }

        return testProjects.ToArray();
    }

    internal static async Task<string[]> ListCandidatesAsync(
        ICommandRunner commandRunner,
        McpOptions options,
        string? workingDirectory,
        CancellationToken cancellationToken
    )
    {
        var result = await commandRunner.RunAsync(
            new CommandRequest("dotnet", "sln", "list")
            {
                WorkingDirectory = workingDirectory,
                MaxOutputChars = 8 * 1024 * 1024,
            },
            cancellationToken
        );
        if (result.StandardOutputTruncated || result.StandardErrorTruncated)
            throw new InvalidOperationException(
                "dotnet sln list output was truncated; test-project discovery is inconclusive."
            );
        if (result.StandardOutputLines.FirstOrDefault() is not "Project(s)")
            throw new InvalidOperationException(
                $"dotnet sln list failed: Unexpected output: {result.StandardOutput.Trim()}"
            );

        var directoryName = options.TestsDirectoryName.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(directoryName))
            return [];

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = string.Concat(directoryName, "/");
        return result.StandardOutputLines
            .Skip(2)
            .Where(project => project.Replace('\\', '/').StartsWith(prefix, comparison))
            .ToArray();
    }

    private static bool IsExplicitlyFalse(ProjectMetadata metadata) =>
        metadata.Properties.TryGetValue("IsTestProject", out var value)
        && string.Equals(value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
}
