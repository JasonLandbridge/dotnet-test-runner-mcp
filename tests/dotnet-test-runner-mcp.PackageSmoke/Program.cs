using System.Text.Json.Nodes;
using ModelContextProtocol.Client;

if (args is not [var executable])
{
    Console.Error.WriteLine("Usage: dotnet-test-runner-mcp.PackageSmoke <installed-executable>");
    return 2;
}

executable = Path.GetFullPath(executable);
if (!File.Exists(executable))
{
    Console.Error.WriteLine($"Installed tool executable does not exist: {executable}");
    return 2;
}

var root = Path.Combine(Path.GetTempPath(), $"mcp-package-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
var project = Path.Combine(root, "Inspection.csproj");
File.WriteAllText(project, """
    <Project>
      <PropertyGroup>
        <IsTestProject>true</IsTestProject>
        <IsTestingPlatformApplication>false</IsTestingPlatformApplication>
        <TargetFramework>net10.0</TargetFramework>
        <OutputType>Library</OutputType>
      </PropertyGroup>
      <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" />
      </ItemGroup>
    </Project>
    """);

try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var transport = new StdioClientTransport(new StdioClientTransportOptions
    {
        Command = executable,
        WorkingDirectory = root,
        EnvironmentVariables = new Dictionary<string, string?>
        {
            ["WORKING_DIRECTORY"] = root,
        },
    });
    await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
    var actual = tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    string[] expected =
    [
        "find_tests",
        "get_failure_details",
        "inspect_test_environment",
        "list_test_projects",
        "list_tests_summary",
        "run_all_tests",
        "run_all_tests_for_project",
        "run_all_tests_in_class",
        "run_changed_tests",
        "run_single_test",
        "run_tests",
    ];
    if (!actual.SequenceEqual(expected.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal))
        throw new InvalidOperationException($"Packaged tool catalog mismatch: {string.Join(", ", actual)}");

    var response = await client.CallToolAsync(
        "inspect_test_environment",
        new Dictionary<string, object?> { ["projectPath"] = project },
        cancellationToken: timeout.Token
    );
    if (response.IsError is true)
        throw new InvalidOperationException("Packaged inspection returned an MCP error.");
    JsonNode inspection = response.StructuredContent
        ?? throw new InvalidOperationException("Packaged inspection returned no structured content.");
    if (inspection["Outcome"]?.GetValue<string>() != "Passed")
        throw new InvalidOperationException($"Packaged inspection failed: {inspection["Issues"]?.ToJsonString()}");
    var projects = inspection["Projects"]?.AsArray()
        ?? throw new InvalidOperationException("Packaged inspection returned no project metadata.");
    if (projects.Count != 1 || projects[0]?["Runner"]?.GetValue<string>() != "VSTest")
        throw new InvalidOperationException("Packaged inspection did not identify the evaluated VSTest project.");
    if (inspection["WorkingDirectory"]?.GetValue<string>() != root)
        throw new InvalidOperationException("Packaged server ignored its launch working directory.");
    if (Directory.Exists(Path.Combine(root, "bin")) || Directory.Exists(Path.Combine(root, "obj")))
        throw new InvalidOperationException("Inspection unexpectedly created build or restore artifacts.");

    Console.WriteLine($"Packaged MCP smoke passed: {actual.Length} tools, VSTest inspection, no build/restore artifacts.");
    return 0;
}
catch (Exception exception)
{
    var message = exception.Message;
    Console.Error.WriteLine(message.Length > 8_000 ? message[..8_000] + " [truncated]" : message);
    return 1;
}
finally
{
    Directory.Delete(root, recursive: true);
}
