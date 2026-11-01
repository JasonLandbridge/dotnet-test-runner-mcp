using DotnetTestRunnerMcp.Terminal;

namespace DotnetTestRunnerMcp.Tools;

internal static class TestProjectDiscovery
{
    internal static async Task<string[]> ListAsync(
        ICommandRunner commandRunner,
        McpOptions options,
        string? workingDirectory,
        CancellationToken cancellationToken
    )
    {
        var result = await commandRunner.RunAsync(
            new CommandRequest("dotnet", "sln", "list") { WorkingDirectory = workingDirectory },
            cancellationToken
        );

        if (result.StandardOutputLines.FirstOrDefault() is not "Project(s)")
            throw new InvalidOperationException(
                $"dotnet sln list failed: Unexpected output: {result.StandardOutput.Trim()}"
            );

        var testsDirectoryName = options.TestsDirectoryName.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(testsDirectoryName))
            return [];

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var testsDirectoryPrefix = string.Concat(testsDirectoryName, "/");

        var candidates = result
            .StandardOutputLines.Skip(2)
            .Where(project =>
                project.Replace('\\', '/').StartsWith(testsDirectoryPrefix, comparison)
            )
            .ToArray();

        var testProjects = new List<string>(candidates.Length);
        foreach (var project in candidates)
        {
            var metadataResult = await commandRunner.RunAsync(
                new CommandRequest(
                    "dotnet",
                    "msbuild",
                    project,
                    "-getProperty:IsTestProject",
                    "-nologo"
                )
                {
                    WorkingDirectory = workingDirectory,
                    ThrowOnNonZeroExitCode = false,
                },
                cancellationToken
            );

            if (metadataResult.ExitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(metadataResult.StandardError)
                    ? metadataResult.StandardOutput.Trim()
                    : metadataResult.StandardError.Trim();
                if (string.IsNullOrWhiteSpace(details))
                    details = "No diagnostic output.";
                throw new InvalidOperationException(
                    $"dotnet msbuild metadata evaluation failed for project '{project}' "
                        + $"with exit code {metadataResult.ExitCode}: {details}"
                );
            }

            if (!IsExplicitlyFalse(metadataResult))
                testProjects.Add(project);
        }

        return testProjects.ToArray();
    }

    private static bool IsExplicitlyFalse(CommandResult result) =>
        string.Equals(result.StandardOutput.Trim(), "false", StringComparison.OrdinalIgnoreCase);
}
