using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DotnetTest.Mcp.Tools;

[McpServerToolType]
public sealed class GetWorkingDirectoryTool(IOptions<McpOptions> options)
{
    private readonly McpOptions _options = options.Value;

    [McpServerTool(UseStructuredContent = true)]
    [Description("Gets the working directory of the server process.")]
    public Result GetWorkingDirectory()
        => new(_options.WorkingDirectory, _options.TestsDirectoryName, _options.TestsDirectoryPath);

    public record Result(
        [property: Description("Working directory.")] string WorkingDirectory,
        [property: Description("Tests directory name.")] string TestsDirectoryName,
        [property: Description("Tests directory path.")] string TestsDirectoryPath);
}