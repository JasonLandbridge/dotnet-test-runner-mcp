using System.Text.Json;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class InspectTestEnvironmentToolTests
{
    [Theory]
    [InlineData("TUnit", "TUnit", "tree-node")]
    [InlineData("Microsoft.NET.Test.Sdk", "VSTest", "FullyQualifiedName")]
    public async Task InspectTestEnvironment_IdentifiesRunnerAndHonestFilterCapabilities(
        string package, string expectedRunner, string filter
    )
    {
        using var fixture = new Fixture();
        var project = fixture.AddProject("tests/Sample/Sample.csproj", package);
        var runner = new FakeRunner(new Dictionary<string, CommandResult>
        {
            [project] = Success(Metadata(package)),
        });

        var result = await CreateTool(runner, fixture.Root)
            .InspectTestEnvironment(projectPath: project);

        Assert.Equal(TestOutcome.Passed, result.Outcome);
        var inspected = Assert.Single(result.Projects);
        Assert.Equal(expectedRunner, inspected.Runner);
        Assert.Equal(expectedRunner, inspected.ExecutionDialect);
        Assert.Contains(filter, inspected.MethodSelection, StringComparison.Ordinal);
        Assert.Contains("Console summary", inspected.ReportingSupport, StringComparison.Ordinal);
        Assert.Empty(inspected.Warnings);
        Assert.Empty(result.SolutionPaths);
    }

    [Fact]
    public async Task InspectTestEnvironment_UsesEvaluatedRunnerForImportedPackageMetadata()
    {
        using var fixture = new Fixture();
        var project = fixture.AddProject("tests/Imported/Imported.csproj", null);
        var runner = new FakeRunner(new Dictionary<string, CommandResult>
        {
            [project] = Success(Metadata("TUnit")),
        });

        var result = await CreateTool(runner, fixture.Root).InspectTestEnvironment(project);

        Assert.Equal(TestOutcome.Passed, result.Outcome);
        var inspected = Assert.Single(result.Projects);
        Assert.Equal("TUnit", inspected.Runner);
        Assert.Equal(inspected.Runner, inspected.ExecutionDialect);
        Assert.DoesNotContain(inspected.Warnings, warning => warning.Contains("direct-reference", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectTestEnvironment_ExplainsHelperOptOutAndUnknownMetadata()
    {
        using var fixture = new Fixture();
        var project = fixture.AddProject("tests/Helper/Helper.csproj", null);
        var runner = new FakeRunner(new Dictionary<string, CommandResult>
        {
            [project] = Success(Metadata(null, false, "Library")),
        });

        var result = await CreateTool(runner, fixture.Root).InspectTestEnvironment(project);

        var inspected = Assert.Single(result.Projects);
        Assert.False(inspected.IsTestProject);
        Assert.Equal("Library", inspected.OutputType);
        Assert.Equal("Unknown", inspected.Runner);
        Assert.Contains(inspected.Warnings, warning => warning.Contains("opts out", StringComparison.Ordinal));
        Assert.Contains("not verified", inspected.ReportingSupport, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InspectTestEnvironment_WithUnknownTestProperty_DoesNotClaimEligibility()
    {
        using var fixture = new Fixture();
        var project = fixture.AddProject("tests/Fresh/Fresh.csproj", "TUnit");
        var runner = new FakeRunner(new Dictionary<string, CommandResult>
        {
            [project] = Success(Metadata("TUnit", null)),
        });

        var result = await CreateTool(runner, fixture.Root).InspectTestEnvironment(project);

        var inspected = Assert.Single(result.Projects);
        Assert.Null(inspected.IsTestProject);
        Assert.Contains(inspected.Warnings, warning => warning.Contains("does not restore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectTestEnvironment_WhenOneProjectFails_ReturnsPartialWithDiagnostics()
    {
        using var fixture = new Fixture();
        var good = fixture.AddProject("tests/Good/Good.csproj", "TUnit");
        var broken = fixture.AddProject("tests/Broken/Broken.csproj", null);
        var runner = new FakeRunner(new Dictionary<string, CommandResult>
        {
            [good] = Success(Metadata("TUnit")),
            [broken] = new CommandResult(1, "", "error MSB4025: invalid project", [], [], TimeSpan.Zero),
        }, $"Project(s)\n----------\n{Path.GetRelativePath(fixture.Root, good)}\n{Path.GetRelativePath(fixture.Root, broken)}");

        var result = await CreateTool(runner, fixture.Root).InspectTestEnvironment();

        Assert.Equal(TestOutcome.Partial, result.Outcome);
        Assert.Equal("TUnit", Assert.Single(result.Projects).Runner);
        Assert.Contains(result.Issues, issue => issue.Contains("MSB4025", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectTestEnvironment_ResolvesOverrideAndAncestorGlobalJson()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "global.json"), "{}");
        var project = fixture.AddProject("nested/tests/Tests.csproj", "TUnit");
        var directory = Path.Combine(fixture.Root, "nested");
        var runner = new FakeRunner(new Dictionary<string, CommandResult>
        {
            [project] = Success(Metadata("TUnit")),
        });

        var result = await CreateTool(runner, Path.GetTempPath())
            .InspectTestEnvironment("tests/Tests.csproj", directory);

        Assert.Equal(TestOutcome.Passed, result.Outcome);
        Assert.Equal(directory, result.WorkingDirectory);
        Assert.Equal(Path.Combine(fixture.Root, "global.json"), result.GlobalJsonPath);
        Assert.Equal("TUnit", Assert.Single(result.Projects).ExecutionDialect);
    }

    [Fact]
    public async Task InspectTestEnvironment_WhenSdkCannotResolve_ReportsPartialNotFalseSuccess()
    {
        using var fixture = new Fixture();
        var project = fixture.AddProject("tests/Tests.csproj", "TUnit");
        var runner = new FakeRunner(new Dictionary<string, CommandResult>
        {
            [project] = Success(Metadata("TUnit")),
        }, sdkFails: true);

        var result = await CreateTool(runner, fixture.Root).InspectTestEnvironment(project);

        Assert.Equal(TestOutcome.Partial, result.Outcome);
        Assert.Null(result.SdkVersion);
        Assert.Contains(result.Issues, issue => issue.Contains("SDK resolution failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectTestEnvironment_WhenMetadataIsMalformed_ReturnsActionableError()
    {
        using var fixture = new Fixture();
        var project = fixture.AddProject("tests/Tests.csproj", "TUnit");
        var runner = new FakeRunner(new Dictionary<string, CommandResult> { [project] = Success("not json") });

        var result = await CreateTool(runner, fixture.Root).InspectTestEnvironment(project);

        Assert.Equal(TestOutcome.Error, result.Outcome);
        Assert.Empty(result.Projects);
        Assert.Contains(result.Issues, issue => issue.Contains("invalid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InspectTestEnvironment_WhenDirectoryDoesNotExist_ReturnsError()
    {
        var runner = new FakeRunner([]);
        var result = await CreateTool(runner, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))
            .InspectTestEnvironment();
        Assert.Equal(TestOutcome.Error, result.Outcome);
        Assert.Equal(new[] { "Working directory does not exist." }, result.Issues);
    }

    private static InspectTestEnvironmentTool CreateTool(ICommandRunner runner, string root) =>
        new(runner, Options.Create(new McpOptions { WorkingDirectory = root }));

    private static string Metadata(string? package, bool? isTest = true, string outputType = "Exe") =>
        JsonSerializer.Serialize(new
        {
            Properties = new Dictionary<string, string>
            {
                ["IsTestProject"] = isTest?.ToString() ?? "",
                ["IsTestingPlatformApplication"] = "true",
                ["OutputType"] = outputType,
                ["TargetFramework"] = "net10.0",
                ["TargetFrameworks"] = "",
            },
            Items = new
            {
                PackageReference = package is null
                    ? Array.Empty<object>()
                    : new[] { new { Identity = package } },
                ProjectReference = Array.Empty<object>(),
                Compile = Array.Empty<object>(),
            },
        });

    private static CommandResult Success(string output) =>
        new(0, output, "", output.Split('\n'), [], TimeSpan.Zero);

    private sealed class FakeRunner(
        Dictionary<string, CommandResult> metadata,
        string? solutionOutput = null,
        bool sdkFails = false
    ) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Arguments.SequenceEqual(["--version"]))
                return Task.FromResult(sdkFails
                    ? new CommandResult(1, "", "Requested SDK not installed.", [], [], TimeSpan.Zero)
                    : Success("10.0.100"));
            if (request.Arguments.SequenceEqual(["sln", "list"]))
                return Task.FromResult(Success(solutionOutput ?? "Project(s)\n----------"));
            if (request.Arguments is ["msbuild", var project, ..])
                return Task.FromResult(metadata[project]);
            throw new InvalidOperationException("Inspection attempted an operation other than SDK/project evaluation.");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"inspect-tests-{Guid.NewGuid():N}");
        public Fixture() => Directory.CreateDirectory(Root);
        public string AddProject(string relativePath, string? package)
        {
            var path = Path.GetFullPath(Path.Combine(Root, relativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, package is null
                ? "<Project />"
                : $"<Project><ItemGroup><PackageReference Include=\"{package}\" /></ItemGroup></Project>");
            return path;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
