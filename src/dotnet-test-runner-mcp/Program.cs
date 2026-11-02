using System.Text.Json;
using DotnetTestRunnerMcp;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JsonContext = DotnetTestRunnerMcp.JsonContext;

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

builder.Configuration.AddEnvironmentVariables();

builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.Configure<McpOptions>(builder.Configuration);
builder.Services.AddScoped<ICommandRunner, ProcessCommandRunner>();
builder.Services.AddScoped<ProjectMetadataReader>();
builder.Services.AddSingleton<TestDiscoverySnapshotStore>();
builder.Services.AddSingleton<TestRunStore>();
builder.Services.AddScoped<RunTestsTool>();

var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = JsonContext.Default };

builder.Services.AddSingleton(jsonOptions);

builder
    .Services.AddMcpServer(options => options.ScopeRequests = true)
    .WithStdioServerTransport()
    .WithTools<ListTestProjectsTool>(jsonOptions)
    .WithTools<ListTestsSummaryTool>(jsonOptions)
    .WithTools<RunSingleTestTool>(jsonOptions)
    .WithTools<RunAllTestsTool>(jsonOptions)
    .WithTools<RunAllTestsForProjectTool>(jsonOptions)
    .WithTools<RunAllTestsInClassTool>(jsonOptions)
    .WithTools<FindTestsTool>(jsonOptions)
    .WithTools<RunTestsTool>(jsonOptions)
    .WithTools<GetFailureDetailsTool>(jsonOptions)
    .WithTools<InspectTestEnvironmentTool>(jsonOptions)
    .WithTools<RunChangedTestsTool>(jsonOptions);

await builder.Build().RunAsync();
