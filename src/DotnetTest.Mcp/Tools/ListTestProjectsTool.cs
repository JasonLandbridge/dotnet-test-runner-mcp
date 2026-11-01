using System.ComponentModel;
using DotnetTest.Mcp.Terminal;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DotnetTest.Mcp.Tools;

[McpServerToolType]
public sealed class ListTestProjectsTool(IOptions<McpOptions> options, ICommandRunner commandRunner)
{
    private readonly McpOptions _options = options.ValidateNotNull().Value;
    private readonly ICommandRunner _commandRunner = commandRunner.ValidateNotNull();

    [McpServerTool(UseStructuredContent = true)]
    [Description("Lists test projects in the solution.")]
    public async Task<Result> ListTestProjects(
        [Description("Optional working directory to run dotnet commands from (useful for git worktrees).")]
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var trimmedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim();

        var testProjects = await TestProjectDiscovery.ListAsync(
            _commandRunner,
            _options,
            trimmedWorkingDirectory,
            cancellationToken);
        return new Result(testProjects);
    }

    [Description("Result containing discovered test project paths.")]
    public record Result(
        [property: Description("Test project paths (relative to the solution root).")]
        string[] TestProjects);
}
