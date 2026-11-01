using System.Text.Json;
using DotnetTest.Mcp;
using DotnetTest.Mcp.Terminal;
using DotnetTest.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

builder.Configuration.AddEnvironmentVariables();

builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.Configure<McpOptions>(builder.Configuration);
builder.Services.AddScoped<ICommandRunner, ProcessCommandRunner>();

var jsonOptions = new JsonSerializerOptions { TypeInfoResolver = JsonContext.Default };

builder.Services.AddSingleton(jsonOptions);

builder.Services
    .AddMcpServer(options => options.ScopeRequests = true)
    .WithStdioServerTransport()
    .WithTools<ListTestProjectsTool>(jsonOptions)
    .WithTools<ListTestsSummaryTool>(jsonOptions)
    .WithTools<RunSingleTestTool>(jsonOptions)
    .WithTools<RunAllTestsTool>(jsonOptions)
    .WithTools<RunAllTestsForProjectTool>(jsonOptions)
    .WithTools<RunAllTestsInClassTool>(jsonOptions);

await builder.Build().RunAsync();
