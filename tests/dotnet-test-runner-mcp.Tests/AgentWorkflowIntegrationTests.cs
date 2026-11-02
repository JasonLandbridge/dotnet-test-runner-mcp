using System.Text.Json.Nodes;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Tools;
using ModelContextProtocol.Client;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class AgentWorkflowIntegrationTests
{
    [Fact]
    public async Task AgentWorkflow_DiscoversRunsAndRetrievesResultsAcrossMcpRequests()
    {
        var root = FindRepositoryRoot();
        const string project = "tests/dotnet-test-runner-mcp.Tests/dotnet-test-runner-mcp.Tests.csproj";
        const string method = "DotnetTestRunnerMcp.Tests.TestListParserTests.ExtractTests_WhenTUnitListsMethodNames_ReturnsDiscoveredTests";
        const string secondMethod = "DotnetTestRunnerMcp.Tests.TestListParserTests.ExtractTests_WhenDisplayNamesRepeat_PreservesEachCase";
        var testAssembly = typeof(AgentWorkflowIntegrationTests).Assembly.Location;
        var serverAssembly = typeof(RunTestsTool).Assembly.Location;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet",
            Arguments = [
                "exec",
                "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"),
                serverAssembly,
            ],
            WorkingDirectory = root,
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["WORKING_DIRECTORY"] = root,
            },
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);

        var environment = await Call(client, "inspect_test_environment", new()
        {
            ["projectPath"] = project,
        }, timeout.Token);
        Assert.Equal("Passed", environment["Outcome"]!.GetValue<string>());
        Assert.Equal("VSTest", environment["Projects"]![0]!["Runner"]!.GetValue<string>());

        var found = await Call(client, "find_tests", new()
        {
            ["query"] = method,
            ["projectPath"] = project,
            ["pageSize"] = 1,
        }, timeout.Token);
        Assert.Equal("Passed", found["Outcome"]!.GetValue<string>());
        var match = Assert.IsType<JsonObject>(Assert.Single(found["Tests"]!.AsArray()));
        Assert.Equal(method, match["Selector"]!.GetValue<string>());
        Assert.False(match["IsAmbiguous"]!.GetValue<bool>());

        var run = await Call(client, "run_tests", new()
        {
            ["selections"] = new[]
            {
                new TestSelection(project, method),
                new TestSelection(project, secondMethod),
            },
        }, timeout.Token);
        Assert.Equal("Passed", run["Outcome"]!.GetValue<string>());
        Assert.Equal(2, run["TestCount"]!.GetValue<int>());
        Assert.Equal(2, run["Passed"]!.GetValue<int>());
        Assert.Equal(0, run["Failed"]!.GetValue<int>());
        var projectExecution = Assert.IsType<JsonObject>(Assert.Single(run["ProjectExecutions"]!.AsArray()));
        Assert.Equal(2, projectExecution["TestCount"]!.GetValue<int>());
        Assert.Equal(2, projectExecution["NativeSelectors"]!.AsArray().Count);
        Assert.All(run["Selections"]!.AsArray(), selection =>
        {
            Assert.False(selection!["CountsKnown"]!.GetValue<bool>());
            Assert.Null(selection["TestCount"]);
        });
        var runId = run["RunId"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(runId));

        var details = await Call(client, "get_failure_details", new()
        {
            ["runId"] = runId,
            ["includeOutput"] = true,
        }, timeout.Token);
        Assert.True(details["Found"]!.GetValue<bool>());
        Assert.Equal(0, details["TotalFailures"]!.GetValue<int>());
        Assert.Contains(
            "Passed",
            details["RunOutput"]!["StandardOutput"]!["Text"]!.GetValue<string>(),
            StringComparison.Ordinal
        );

        var changed = await Call(client, "run_changed_tests", new()
        {
            ["baseRef"] = "HEAD",
            ["dryRun"] = true,
        }, timeout.Token);
        Assert.Equal("Skipped", changed["Outcome"]!.GetValue<string>());
        Assert.True(changed["DryRun"]!.GetValue<bool>());
        Assert.Null(changed["Execution"]);
    }

    private static async Task<JsonNode> Call(
        McpClient client,
        string tool,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken
    )
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);
        Assert.False(result.IsError is true);
        return result.StructuredContent
            ?? throw new Xunit.Sdk.XunitException($"Tool '{tool}' did not return structured content.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "dotnet-test-runner-mcp.slnx")))
                return directory.FullName;
        throw new InvalidOperationException("The MCP workflow integration test requires the repository checkout.");
    }
}
