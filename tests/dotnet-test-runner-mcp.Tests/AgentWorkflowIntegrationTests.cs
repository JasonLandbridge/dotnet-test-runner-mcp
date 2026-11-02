using System.Text.Json.Nodes;
using System.Xml.Linq;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Microsoft.Extensions.Options;
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

        var gitRoot = Path.Combine(Path.GetTempPath(), $"agent-workflow-git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(gitRoot);
        try
        {
            await using var git = new ProcessCommandRunner(
                Options.Create(new McpOptions { WorkingDirectory = gitRoot })
            );
            await git.RunAsync(new CommandRequest("git", "init"), timeout.Token);
            await git.RunAsync(
                new CommandRequest(
                    "git", "-c", "user.name=Test Fixture",
                    "-c", "user.email=fixture@example.invalid",
                    "-c", "commit.gpgsign=false",
                    "commit", "--allow-empty", "-m", "test fixture"
                ),
                timeout.Token
            );
            var changed = await Call(client, "run_changed_tests", new()
            {
                ["baseRef"] = "HEAD",
                ["dryRun"] = true,
                ["workingDirectory"] = gitRoot,
            }, timeout.Token);
            Assert.Equal("Skipped", changed["Outcome"]!.GetValue<string>());
            Assert.True(changed["DryRun"]!.GetValue<bool>());
            Assert.Null(changed["Execution"]);
            var retainedAfterGit = await Call(client, "get_failure_details", new()
            {
                ["runId"] = runId,
            }, timeout.Token);
            Assert.True(retainedAfterGit["Found"]!.GetValue<bool>());
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(gitRoot, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(gitRoot, recursive: true);
        }
    }

    [Fact]
    public async Task AgentWorkflow_RetainsActualFailingTestDiagnosticsAndValidatesFields()
    {
        var root = FindRepositoryRoot();
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"native-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            var project = Path.Combine(fixtureRoot, "NativeFailureFixture.csproj");
            var testProject = XDocument.Load(Path.Combine(
                root, "tests", "dotnet-test-runner-mcp.Tests", "dotnet-test-runner-mcp.Tests.csproj"
            ));
            new XDocument(new XElement("Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup",
                    new XElement("TargetFramework", "net10.0"),
                    new XElement("IsTestProject", "true"),
                    new XElement("IsPackable", "false")),
                new XElement("ItemGroup", testProject.Descendants("PackageReference")
                    .Select(reference => new XElement(reference)))
            )).Save(project);
            File.WriteAllText(Path.Combine(fixtureRoot, "NativeFailureTests.cs"), """
                namespace NativeFailureFixture;
                public sealed class NativeFailureTests
                {
                    [Xunit.Fact]
                    public void FailsWithDiagnostic()
                    {
                        throw new System.InvalidOperationException("native-failure-fixture");
                    }
                }
                """);

            var testAssembly = typeof(AgentWorkflowIntegrationTests).Assembly.Location;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet",
                Arguments = [
                    "exec",
                    "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
                    "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"),
                    typeof(RunTestsTool).Assembly.Location,
                ],
                WorkingDirectory = fixtureRoot,
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["WORKING_DIRECTORY"] = fixtureRoot,
                    ["DOTNET_CLI_UI_LANGUAGE"] = "en",
                },
            });
            await using var client = await McpClient.CreateAsync(
                transport, cancellationToken: timeout.Token
            );
            var run = await Call(client, "run_tests", new()
            {
                ["selections"] = new[] { new TestSelection(project) },
            }, timeout.Token);
            Assert.Equal("Failed", run["Outcome"]!.GetValue<string>());
            Assert.Equal(1, run["TestCount"]!.GetValue<int>());
            Assert.Equal(1, run["Failed"]!.GetValue<int>());
            var runId = run["RunId"]!.GetValue<string>();

            var invalid = await Call(client, "get_failure_details", new()
            {
                ["runId"] = runId,
                ["diagnosticField"] = "invalid-field",
            }, timeout.Token);
            Assert.Equal("Error", invalid["Outcome"]!.GetValue<string>());
            Assert.Contains("message, trace, output", invalid["Message"]!.GetValue<string>());

            var details = await Call(client, "get_failure_details", new()
            {
                ["runId"] = runId,
                ["diagnosticField"] = "output",
                ["maxCharacters"] = 8_000,
            }, timeout.Token);
            Assert.True(details["Found"]!.GetValue<bool>());
            Assert.Equal("Failed", details["Outcome"]!.GetValue<string>());
            var output = details["RunOutput"]!;
            var stdout = output["StandardOutput"]?["Text"]?.GetValue<string>() ?? "";
            var stderr = output["StandardError"]?["Text"]?.GetValue<string>() ?? "";
            var diagnosticText = string.Concat(stdout, stderr);
            Assert.Contains("native-failure-fixture", diagnosticText, StringComparison.Ordinal);
            Assert.Contains(
                "at NativeFailureFixture.NativeFailureTests.FailsWithDiagnostic()",
                diagnosticText,
                StringComparison.Ordinal
            );
            Assert.True(diagnosticText.Length <= 8_000);

            var bounded = await Call(client, "get_failure_details", new()
            {
                ["runId"] = runId,
                ["diagnosticField"] = "output",
                ["maxCharacters"] = 64,
            }, timeout.Token);
            var boundedOutput = bounded["RunOutput"]!;
            var boundedStdout = boundedOutput["StandardOutput"]?["Text"]?.GetValue<string>() ?? "";
            var boundedStderr = boundedOutput["StandardError"]?["Text"]?.GetValue<string>() ?? "";
            Assert.True(boundedStdout.Length + boundedStderr.Length <= 64);
            Assert.True(boundedOutput["StandardOutput"]?["Truncated"]?.GetValue<bool>() == true
                || boundedOutput["StandardError"]?["Truncated"]?.GetValue<bool>() == true);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(fixtureRoot, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(fixtureRoot, recursive: true);
        }
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
