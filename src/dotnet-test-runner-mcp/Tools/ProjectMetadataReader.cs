using System.Collections.Concurrent;
using System.Text.Json;
using DotnetTestRunnerMcp.Extensions;
using DotnetTestRunnerMcp.Terminal;
using Microsoft.Extensions.Options;

namespace DotnetTestRunnerMcp.Tools;

/// <summary>
/// Reads evaluated MSBuild metadata without restoring, building, or inspecting project XML.
/// </summary>
public sealed class ProjectMetadataReader
{
    private const int MaximumMetadataOutputCharacters = 8 * 1024 * 1024;
    private static readonly string[] MetadataArguments =
    [
        "-getProperty:IsTestProject,IsTestingPlatformApplication,OutputType,TargetFramework,TargetFrameworks",
        "-getItem:PackageReference,ProjectReference,Compile",
        "-nologo",
    ];
    private static readonly string[] RequiredItemNames =
    [
        "PackageReference",
        "ProjectReference",
        "Compile",
    ];

    private readonly ICommandRunner _commandRunner;
    private readonly McpOptions _options;
    private readonly ConcurrentDictionary<MetadataCacheKey, Lazy<Task<ProjectMetadata>>> _cache = new();

    public ProjectMetadataReader(ICommandRunner commandRunner, IOptions<McpOptions> options)
    {
        _commandRunner = commandRunner.ValidateNotNull();
        _options = options.ValidateNotNull().Value;
    }

    internal Task<TestRunnerDialect> GetDialectAsync(
        string? projectPath,
        string? workingDirectory,
        CancellationToken cancellationToken
    ) => GetDialectCoreAsync(projectPath, workingDirectory, cancellationToken);

    internal async Task<ProjectMetadata> ReadProjectAsync(
        string? projectPath,
        string? workingDirectory,
        CancellationToken cancellationToken
    )
    {
        var effectiveWorkingDirectory = ResolveWorkingDirectory(workingDirectory);
        var trimmedProjectPath = TrimOrNull(projectPath);
        if (trimmedProjectPath is null)
        {
            return ProjectMetadata.Failed(
                null,
                effectiveWorkingDirectory,
                "A project path is required to evaluate project metadata."
            );
        }

        var fullProjectPath = Path.GetFullPath(trimmedProjectPath, effectiveWorkingDirectory);
        var key = new MetadataCacheKey(fullProjectPath, effectiveWorkingDirectory);
        var lazy = _cache.GetOrAdd(
            key,
            static (cacheKey, state) =>
                new Lazy<Task<ProjectMetadata>>(
                    () => state.reader.EvaluateAsync(
                        state.commandProjectPath,
                        cacheKey.WorkingDirectory,
                        state.cancellationToken
                    ),
                    LazyThreadSafetyMode.ExecutionAndPublication
                ),
            (reader: this, commandProjectPath: trimmedProjectPath, cancellationToken)
        );

        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _cache.TryRemove(new KeyValuePair<MetadataCacheKey, Lazy<Task<ProjectMetadata>>>(key, lazy));
            throw;
        }
    }

    private async Task<TestRunnerDialect> GetDialectCoreAsync(
        string? projectPath,
        string? workingDirectory,
        CancellationToken cancellationToken
    )
    {
        var metadata = await ReadProjectAsync(projectPath, workingDirectory, cancellationToken)
            .ConfigureAwait(false);
        return metadata.Dialect;
    }

    private async Task<ProjectMetadata> EvaluateAsync(
        string projectPath,
        string workingDirectory,
        CancellationToken cancellationToken
    )
    {
        CommandResult result;
        try
        {
            result = await _commandRunner.RunAsync(
                    new CommandRequest(
                        "dotnet",
                        ["msbuild", projectPath, .. MetadataArguments]
                    )
                    {
                        WorkingDirectory = workingDirectory,
                        ThrowOnNonZeroExitCode = false,
                        Timeout = TimeSpan.FromSeconds(20),
                        MaxOutputChars = MaximumMetadataOutputCharacters,
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
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
            return ProjectMetadata.Failed(
                projectPath,
                workingDirectory,
                $"MSBuild metadata evaluation failed: {exception.Message}"
            );
        }

        if (result.StandardOutputTruncated || result.StandardErrorTruncated)
        {
            return ProjectMetadata.Failed(
                projectPath,
                workingDirectory,
                "MSBuild metadata evaluation output was truncated; the evaluated metadata is inconclusive."
            );
        }

        if (result.ExitCode != 0)
        {
            var diagnostics = Diagnostics(result);
            return ProjectMetadata.Failed(
                projectPath,
                workingDirectory,
                $"MSBuild metadata evaluation failed with exit code {result.ExitCode}: {diagnostics}"
            );
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            return ParseMetadata(projectPath, workingDirectory, document.RootElement);
        }
        catch (JsonException exception)
        {
            return ProjectMetadata.Failed(
                projectPath,
                workingDirectory,
                $"MSBuild metadata evaluation returned invalid JSON: {exception.Message}"
            );
        }
    }

    private static ProjectMetadata ParseMetadata(
        string projectPath,
        string workingDirectory,
        JsonElement root
    )
    {
        if (
            !root.TryGetProperty("Properties", out var propertiesElement)
            || propertiesElement.ValueKind != JsonValueKind.Object
        )
            return ProjectMetadata.Failed(
                projectPath,
                workingDirectory,
                "MSBuild metadata evaluation returned no Properties object."
            );

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in propertiesElement.EnumerateObject())
        {
            if (ReadString(property.Value) is { } value)
                properties[property.Name] = value;
        }

        if (
            !root.TryGetProperty("Items", out var itemsElement)
            || itemsElement.ValueKind != JsonValueKind.Object
        )
            return ProjectMetadata.Failed(
                projectPath,
                workingDirectory,
                "MSBuild metadata evaluation returned no Items object."
            );

        var items = new Dictionary<string, ProjectMetadataItem[]>(
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var itemName in RequiredItemNames)
        {
            if (
                !itemsElement.TryGetProperty(itemName, out var itemGroup)
                || itemGroup.ValueKind != JsonValueKind.Array
            )
                return ProjectMetadata.Failed(
                    projectPath,
                    workingDirectory,
                    $"MSBuild metadata evaluation returned no {itemName} array."
                );

            var parsedItems = new List<ProjectMetadataItem>();
            foreach (var item in itemGroup.EnumerateArray())
            {
                if (item.ValueKind is not (JsonValueKind.Object or JsonValueKind.String))
                    return ProjectMetadata.Failed(
                        projectPath,
                        workingDirectory,
                        $"MSBuild metadata evaluation returned malformed {itemName} item data."
                    );
                parsedItems.Add(ParseItem(item));
            }

            items[itemName] = parsedItems.ToArray();
        }

        var dialect = DetectDialect(
            items.TryGetValue("PackageReference", out var packages) ? packages : [],
            properties
        );
        return new ProjectMetadata(
            projectPath,
            workingDirectory,
            true,
            null,
            properties,
            items,
            dialect
        );
    }

    private static ProjectMetadataItem ParseItem(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            var value = item.GetString() ?? string.Empty;
            return new ProjectMetadataItem(value, value, null, null, null);
        }

        if (item.ValueKind != JsonValueKind.Object)
            return new ProjectMetadataItem(string.Empty, null, null, null, null);

        var evaluatedInclude = ReadProperty(item, "EvaluatedInclude");
        var identity = ReadProperty(item, "Identity") ?? evaluatedInclude ?? string.Empty;
        return new ProjectMetadataItem(
            identity,
            evaluatedInclude,
            ReadProperty(item, "FullPath"),
            ReadProperty(item, "Link"),
            ReadProperty(item, "DefiningProjectFullPath")
        );
    }

    private static TestRunnerDialect DetectDialect(
        IReadOnlyCollection<ProjectMetadataItem> packageReferences,
        IReadOnlyDictionary<string, string> properties
    )
    {
        var packageNames = packageReferences
            .Select(package => package.Identity)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        if (
            packageNames.Any(
                package =>
                    package.Equals("TUnit", StringComparison.OrdinalIgnoreCase)
                    || package.StartsWith("TUnit.", StringComparison.OrdinalIgnoreCase)
            )
        )
            return TestRunnerDialect.TUnit;

        if (
            packageNames.Any(
                package =>
                    package.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                    || package.Equals("xunit.runner.visualstudio", StringComparison.OrdinalIgnoreCase)
                    || package.Equals("MSTest.TestAdapter", StringComparison.OrdinalIgnoreCase)
                    || package.Equals("NUnit3TestAdapter", StringComparison.OrdinalIgnoreCase)
            )
        )
            return TestRunnerDialect.VSTest;

        if (
            properties.TryGetValue("IsTestingPlatformApplication", out var isMtp)
            && bool.TryParse(isMtp, out var isTestingPlatformApplication)
            && isTestingPlatformApplication
            && properties.TryGetValue("OutputType", out var outputType)
            && string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
        )
            return TestRunnerDialect.MTP;

        return TestRunnerDialect.Unknown;
    }

    private static string? ReadProperty(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? ReadString(value) : null;

    private static string? ReadString(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };

    private static string Diagnostics(CommandResult result)
    {
        var details = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();
        return string.IsNullOrWhiteSpace(details) ? "No diagnostic output." : details;
    }

    private string ResolveWorkingDirectory(string? workingDirectory)
    {
        var requested = TrimOrNull(workingDirectory) ?? TrimOrNull(_options.WorkingDirectory);
        return Path.GetFullPath(requested ?? Directory.GetCurrentDirectory());
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct MetadataCacheKey(string ProjectPath, string WorkingDirectory);
}

internal sealed record ProjectMetadata(
    string? ProjectPath,
    string WorkingDirectory,
    bool EvaluationSucceeded,
    string? EvaluationError,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyDictionary<string, ProjectMetadataItem[]> Items,
    TestRunnerDialect Dialect
)
{
    internal IReadOnlyList<ProjectMetadataItem> ProjectReferences => GetItems("ProjectReference");

    internal IReadOnlyList<ProjectMetadataItem> CompileItems => GetItems("Compile");

    internal static ProjectMetadata Failed(
        string? projectPath,
        string workingDirectory,
        string error
    ) => new(
        projectPath,
        workingDirectory,
        false,
        error,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, ProjectMetadataItem[]>(StringComparer.OrdinalIgnoreCase),
        TestRunnerDialect.Unknown
    );

    private IReadOnlyList<ProjectMetadataItem> GetItems(string itemType) =>
        Items.TryGetValue(itemType, out var items) ? items : [];
}

internal sealed record ProjectMetadataItem(
    string Identity,
    string? EvaluatedInclude,
    string? FullPath,
    string? Link,
    string? DefiningProjectFullPath
);
