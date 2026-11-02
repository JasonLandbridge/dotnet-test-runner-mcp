using System.ComponentModel;
using DotnetTestRunnerMcp.Extensions;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DotnetTestRunnerMcp.Tools;

[McpServerToolType]
public sealed class InspectTestEnvironmentTool
{
    private readonly ICommandRunner _commandRunner;
    private readonly McpOptions _options;
    private readonly ProjectMetadataReader _metadataReader;

    public InspectTestEnvironmentTool(
        ICommandRunner commandRunner,
        IOptions<McpOptions> options,
        ProjectMetadataReader? metadataReader = null
    )
    {
        _commandRunner = commandRunner.ValidateNotNull();
        _options = options.ValidateNotNull().Value;
        _metadataReader =
            metadataReader ?? new ProjectMetadataReader(commandRunner, options);
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Inspects the resolved directory, SDK and evaluated MSBuild project runner settings without restoring, building or running tests."
    )]
    public async Task<Result> InspectTestEnvironment(
        [Description("Optional project to inspect; otherwise inspect solution projects under the configured tests directory.")]
            string? projectPath = null,
        [Description("Optional directory override; defaults to the server launch/configured directory.")]
            string? workingDirectory = null,
        CancellationToken cancellationToken = default
    )
    {
        var directory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(workingDirectory)
                ? _options.WorkingDirectory
                : workingDirectory.Trim()
        );
        var issues = new List<string>();
        var projects = new List<ProjectEnvironment>();
        string? sdkVersion = null;
        if (!Directory.Exists(directory))
            return new Result(TestOutcome.Error, directory, null, null, [], [], ["Working directory does not exist."]);

        var solutions = Directory.EnumerateFiles(directory)
            .Where(path => Path.GetExtension(path) is ".sln" or ".slnx")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var globalJson = FindGlobalJson(directory);
        try
        {
            var sdk = await _commandRunner.RunAsync(
                new CommandRequest("dotnet", "--version")
                {
                    WorkingDirectory = directory,
                    ThrowOnNonZeroExitCode = false,
                    Timeout = TimeSpan.FromSeconds(20),
                },
                cancellationToken
            );
            if (sdk.ExitCode == 0)
                sdkVersion = sdk.StandardOutput.Trim();
            else
                issues.Add($"SDK resolution failed: {Diagnostics(sdk)}");

            string[] candidates = string.IsNullOrWhiteSpace(projectPath)
                ? await TestProjectDiscovery.ListCandidatesAsync(
                    _commandRunner,
                    _options,
                    directory,
                    cancellationToken
                )
                : [projectPath.Trim()];
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(candidate, directory);
                if (!File.Exists(fullPath))
                {
                    issues.Add($"Project does not exist: {candidate}");
                    continue;
                }

                var metadata = await _metadataReader.ReadProjectAsync(
                    fullPath,
                    directory,
                    cancellationToken
                );
                if (!metadata.EvaluationSucceeded)
                {
                    issues.Add(
                        $"Project evaluation failed for '{candidate}': "
                            + (metadata.EvaluationError ?? "No diagnostic output.")
                    );
                    continue;
                }

                projects.Add(ReadProject(candidate, metadata));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or IOException
                or UnauthorizedAccessException
        )
        {
            issues.Add(exception.Message);
        }

        var outcome = issues.Count == 0
            ? TestOutcome.Passed
            : projects.Count > 0 ? TestOutcome.Partial : TestOutcome.Error;
        return new Result(outcome, directory, sdkVersion, globalJson, solutions, projects.ToArray(), issues.ToArray());
    }

    private ProjectEnvironment ReadProject(string project, ProjectMetadata metadata)
    {
        var runner = metadata.Dialect.ToString();
        var warnings = new List<string>();
        var isTest = BooleanProperty(metadata.Properties, "IsTestProject");
        if (isTest == false)
            warnings.Add("This project explicitly opts out of test-project discovery.");
        if (isTest is null)
            warnings.Add("IsTestProject is unknown; package restore may be needed to evaluate imported test SDK properties. Inspection does not restore packages.");
        if (metadata.Dialect == TestRunnerDialect.Unknown)
            warnings.Add("No supported runner was identified from evaluated package references.");

        var methodSelection = metadata.Dialect switch
        {
            TestRunnerDialect.TUnit => "Method-scoped tree-node filter; multiple cases/classes may match.",
            TestRunnerDialect.VSTest => "VSTest FullyQualifiedName filter; parameterized methods can contain multiple cases.",
            TestRunnerDialect.MTP => "Native MTP run is supported; exact method selection is not verified.",
            _ => "Unknown; generic MTP filter support is not verified.",
        };
        var classSelection = metadata.Dialect switch
        {
            TestRunnerDialect.TUnit => "Short class tree-node filter; classes with the same short name may match.",
            TestRunnerDialect.VSTest => "VSTest FullyQualifiedName substring filter.",
            TestRunnerDialect.MTP => "Native MTP run is supported; exact class selection is not verified.",
            _ => "Unknown; generic MTP class filter support is not verified.",
        };
        var reporting = _options.DisableCtrf
            ? "CTRF disabled; console summary only."
            : metadata.Dialect is TestRunnerDialect.TUnit or TestRunnerDialect.VSTest
                ? "Console summary; this execution path does not request CTRF."
                : metadata.Dialect == TestRunnerDialect.MTP
                    ? "CTRF report; native MTP run."
                    : "Unknown; CTRF support is probed during execution, not verified by inspection.";
        return new ProjectEnvironment(
            project,
            runner,
            runner,
            isTest,
            BooleanProperty(metadata.Properties, "IsTestingPlatformApplication"),
            StringProperty(metadata.Properties, "OutputType"),
            StringProperty(metadata.Properties, "TargetFramework"),
            StringProperty(metadata.Properties, "TargetFrameworks"),
            methodSelection,
            classSelection,
            reporting,
            warnings.ToArray()
        );
    }

    private static string? StringProperty(
        IReadOnlyDictionary<string, string> properties,
        string name
    ) => properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : null;

    private static bool? BooleanProperty(
        IReadOnlyDictionary<string, string> properties,
        string name
    ) => bool.TryParse(StringProperty(properties, name), out var value) ? value : null;

    private static string Diagnostics(CommandResult result)
    {
        var text = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim() : result.StandardError.Trim();
        return text.Length > 4000 ? text[..4000] + " [truncated]" : text;
    }

    private static string? FindGlobalJson(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "global.json");
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    public sealed record Result(
        TestOutcome Outcome,
        string WorkingDirectory,
        string? SdkVersion,
        string? GlobalJsonPath,
        string[] SolutionPaths,
        ProjectEnvironment[] Projects,
        string[] Issues
    );

    public sealed record ProjectEnvironment(
        string ProjectPath,
        string Runner,
        string ExecutionDialect,
        bool? IsTestProject,
        bool? IsTestingPlatformApplication,
        string? OutputType,
        string? TargetFramework,
        string? TargetFrameworks,
        string MethodSelection,
        string ClassSelection,
        string ReportingSupport,
        string[] Warnings
    );
}
