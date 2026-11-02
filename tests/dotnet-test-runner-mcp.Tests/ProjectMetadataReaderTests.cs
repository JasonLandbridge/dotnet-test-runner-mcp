using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class ProjectMetadataReaderTests
{
    [Fact]
    public async Task ReadProjectAsync_UsesEvaluatedPackageOutputInsteadOfProjectText()
    {
        var runner = new FakeCommandRunner(_ => Success(Metadata(
            "TUnit",
            isTestingPlatformApplication: "false",
            outputType: "Library"
        )));
        var reader = new ProjectMetadataReader(
            runner,
            Options.Create(new McpOptions { WorkingDirectory = "/repo" })
        );

        var result = await reader.ReadProjectAsync(
            "tests/Conditional/Conditional.csproj",
            "/repo",
            CancellationToken.None
        );

        Assert.True(result.EvaluationSucceeded);
        Assert.Equal(TestRunnerDialect.TUnit, result.Dialect);
        Assert.Equal("TUnit", Assert.Single(result.Items["PackageReference"]).Identity);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(
            new[]
            {
                "msbuild",
                "tests/Conditional/Conditional.csproj",
                "-getProperty:IsTestProject,IsTestingPlatformApplication,OutputType,TargetFramework,TargetFrameworks",
                "-getItem:PackageReference,ProjectReference,Compile",
                "-nologo",
            },
            request.Arguments
        );
        Assert.Equal(8 * 1024 * 1024, request.MaxOutputChars);
    }

    [Fact]
    public async Task GetDialectAsync_ReusesEvaluationWithinReaderInstance()
    {
        var runner = new FakeCommandRunner(_ => Success(Metadata("Microsoft.NET.Test.Sdk")));
        var reader = new ProjectMetadataReader(
            runner,
            Options.Create(new McpOptions { WorkingDirectory = "/repo" })
        );

        var first = await reader.GetDialectAsync("tests/Sample/Sample.csproj", "/repo", CancellationToken.None);
        var second = await reader.GetDialectAsync("tests/Sample/Sample.csproj", "/repo", CancellationToken.None);
        var metadata = await reader.ReadProjectAsync("tests/Sample/Sample.csproj", "/repo", CancellationToken.None);

        Assert.Equal(TestRunnerDialect.VSTest, first);
        Assert.Equal(first, second);
        Assert.Equal(second, metadata.Dialect);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task GetDialectAsync_ReturnsUnknownWhenEvaluationFailsOrIsTruncated()
    {
        var runner = new FakeCommandRunner(_ =>
            new CommandResult(1, "", "error MSB", [], ["error MSB"], TimeSpan.Zero)
        );
        var reader = new ProjectMetadataReader(
            runner,
            Options.Create(new McpOptions { WorkingDirectory = "/repo" })
        );

        var failed = await reader.GetDialectAsync("tests/Broken/Broken.csproj", "/repo", CancellationToken.None);
        Assert.Equal(TestRunnerDialect.Unknown, failed);

        var truncatedRunner = new FakeCommandRunner(_ =>
            Success(Metadata("TUnit")) with { StandardOutputTruncated = true }
        );
        var truncatedReader = new ProjectMetadataReader(
            truncatedRunner,
            Options.Create(new McpOptions { WorkingDirectory = "/repo" })
        );

        var truncated = await truncatedReader.GetDialectAsync("tests/Large/Large.csproj", "/repo", CancellationToken.None);
        Assert.Equal(TestRunnerDialect.Unknown, truncated);
    }

    [Fact]
    public async Task GetDialectAsync_RecognizesEvaluatedGenericMtpProperties()
    {
        var runner = new FakeCommandRunner(_ => Success(Metadata(
            null,
            isTestingPlatformApplication: "true",
            outputType: "Exe"
        )));
        var reader = new ProjectMetadataReader(
            runner,
            Options.Create(new McpOptions { WorkingDirectory = "/repo" })
        );

        var dialect = await reader.GetDialectAsync("tests/Mtp/Mtp.csproj", "/repo", CancellationToken.None);

        Assert.Equal(TestRunnerDialect.MTP, dialect);
    }

    [Theory]
    [InlineData("PackageReference")]
    [InlineData("ProjectReference")]
    [InlineData("Compile")]
    public async Task ReadProjectAsync_RejectsMissingRequiredItemArrays(string missingItem)
    {
        var metadata = JsonNode.Parse(Metadata("TUnit"))!.AsObject();
        metadata["Items"]!.AsObject().Remove(missingItem);
        var reader = new ProjectMetadataReader(
            new FakeCommandRunner(_ => Success(metadata.ToJsonString())),
            Options.Create(new McpOptions { WorkingDirectory = "/repo" })
        );

        var result = await reader.ReadProjectAsync(
            "tests/Missing/Missing.csproj",
            "/repo",
            CancellationToken.None
        );

        Assert.False(result.EvaluationSucceeded);
        Assert.Equal(TestRunnerDialect.Unknown, result.Dialect);
        Assert.Contains(missingItem, result.EvaluationError ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadProjectAsync_RejectsMalformedRequiredItemGroup()
    {
        var metadata = JsonNode.Parse(Metadata("TUnit"))!.AsObject();
        metadata["Items"]!.AsObject()["Compile"] = new JsonObject();
        var reader = new ProjectMetadataReader(
            new FakeCommandRunner(_ => Success(metadata.ToJsonString())),
            Options.Create(new McpOptions { WorkingDirectory = "/repo" })
        );

        var result = await reader.ReadProjectAsync(
            "tests/Malformed/Malformed.csproj",
            "/repo",
            CancellationToken.None
        );

        Assert.False(result.EvaluationSucceeded);
        Assert.Equal(TestRunnerDialect.Unknown, result.Dialect);
        Assert.Contains("Compile", result.EvaluationError ?? "", StringComparison.Ordinal);
    }

    private static string Metadata(
        string? package,
        string isTestingPlatformApplication = "true",
        string outputType = "Exe"
    ) =>
        JsonSerializer.Serialize(
            new
            {
                Properties = new Dictionary<string, string>
                {
                    ["IsTestProject"] = "true",
                    ["IsTestingPlatformApplication"] = isTestingPlatformApplication,
                    ["OutputType"] = outputType,
                    ["TargetFramework"] = "net10.0",
                    ["TargetFrameworks"] = "",
                },
                Items = new
                {
                    PackageReference = package is null
                        ? Array.Empty<object>()
                        : new[] { new { Identity = package, EvaluatedInclude = package } },
                    ProjectReference = Array.Empty<object>(),
                    Compile = new[]
                    {
                        new
                        {
                            Identity = "Tests/Example.cs",
                            EvaluatedInclude = "Tests/Example.cs",
                            FullPath = "/repo/tests/Example.cs",
                            Link = "Example.cs",
                            DefiningProjectFullPath = "/repo/tests/Conditional/Conditional.csproj",
                        },
                    },
                },
            }
        );

    private static CommandResult Success(string output) =>
        new(0, output, "", output.Split('\n'), [], TimeSpan.Zero);

    private sealed class FakeCommandRunner(Func<CommandRequest, CommandResult> callback) : ICommandRunner
    {
        internal List<CommandRequest> Requests { get; } = [];

        public Task<CommandResult> RunAsync(
            CommandRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            return Task.FromResult(callback(request));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
