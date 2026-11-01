using DotnetTest.Mcp.Terminal;

namespace DotnetTest.Mcp.Tools;

internal static class TestProjectDiscovery
{
    internal static async Task<string[]> ListAsync(
        ICommandRunner commandRunner,
        McpOptions options,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await commandRunner.RunAsync(
            new CommandRequest("dotnet", "sln", "list")
            {
                WorkingDirectory = workingDirectory,
            },
            cancellationToken);

        if (result.StandardOutputLines.FirstOrDefault() is not "Project(s)")
            throw new InvalidOperationException(
                $"dotnet sln list failed: Unexpected output: {result.StandardOutput.Trim()}");

        var testsDirectoryName = options.TestsDirectoryName
            .Replace('\\', '/')
            .Trim('/');
        if (string.IsNullOrWhiteSpace(testsDirectoryName))
            return [];

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var testsDirectoryPrefix = string.Concat(testsDirectoryName, "/");

        return result.StandardOutputLines
            .Skip(2)
            .Where(project => project
                .Replace('\\', '/')
                .StartsWith(testsDirectoryPrefix, comparison))
            .ToArray();
    }
}