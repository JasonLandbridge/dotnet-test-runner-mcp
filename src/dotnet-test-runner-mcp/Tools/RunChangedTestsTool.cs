using System.ComponentModel;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DotnetTestRunnerMcp.Tools;

[McpServerToolType]
public sealed class RunChangedTestsTool
{
    private readonly ChangedTestsImpactAnalyzer _analyzer;
    private readonly RunTestsTool _runTestsTool;

    public RunChangedTestsTool(
        ICommandRunner commandRunner,
        IOptions<McpOptions> options,
        RunTestsTool runTestsTool,
        ProjectMetadataReader? metadataReader = null
    )
    {
        ArgumentNullException.ThrowIfNull(commandRunner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runTestsTool);

        _analyzer = new ChangedTestsImpactAnalyzer(
            commandRunner,
            options.Value,
            metadataReader
        );
        _runTestsTool = runTestsTool;
    }

    [McpServerTool(UseStructuredContent = true)]
    [Description(
        "Selects test projects affected by tracked and untracked Git changes and optionally runs them. "
            + "Impact is conservative at project level; this tool never claims method-level precision."
    )]
    public async Task<Result> RunChangedTests(
        [Description("Git revision to compare with. Defaults to HEAD.")] string baseRef = "HEAD",
        [Description("Select and explain affected test projects without executing tests.")] bool dryRun = false,
        [Description("Optional working directory, including a Git worktree or repository subdirectory.")]
            string? workingDirectory = null,
        CancellationToken cancellationToken = default
    )
    {
        var requestedBaseRef = string.IsNullOrWhiteSpace(baseRef) ? "HEAD" : baseRef.Trim();
        ChangedTestsImpactAnalyzer.ImpactAnalysis analysis;
        try
        {
            analysis = await _analyzer.AnalyzeAsync(
                requestedBaseRef,
                workingDirectory,
                cancellationToken
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new Result(
                RunId: null,
                Outcome: TestOutcome.Error,
                Message: "Changed-test impact analysis failed; no tests were executed.",
                BaseRef: requestedBaseRef,
                DryRun: dryRun,
                ImpactUnknown: false,
                ChangedFiles: [],
                SelectedTestProjects: [],
                Reasons: [],
                Execution: null,
                Error: exception.Message,
                ImpactExplanations: []
            );
        }

        if (!analysis.Succeeded)
        {
            return new Result(
                RunId: null,
                Outcome: TestOutcome.Error,
                Message: analysis.Message,
                BaseRef: requestedBaseRef,
                DryRun: dryRun,
                ImpactUnknown: false,
                ChangedFiles: analysis.ChangedFiles,
                SelectedTestProjects: [],
                Reasons: analysis.Reasons,
                Execution: null,
                Error: analysis.Error,
                ImpactExplanations: analysis.ImpactExplanations
            );
        }

        if (analysis.SelectedTestProjects.Length == 0)
        {
            var message = analysis.NoChanges
                ? "No tracked or untracked changes were found relative to the base revision."
                : "No eligible test projects were affected by the changes.";
            return new Result(
                RunId: null,
                Outcome: TestOutcome.Skipped,
                Message: message,
                BaseRef: requestedBaseRef,
                DryRun: dryRun,
                ImpactUnknown: analysis.ImpactUnknown,
                ChangedFiles: analysis.ChangedFiles,
                SelectedTestProjects: [],
                Reasons: analysis.Reasons,
                Execution: null,
                Error: null,
                ImpactExplanations: analysis.ImpactExplanations
            );
        }

        if (dryRun)
        {
            return new Result(
                RunId: null,
                Outcome: TestOutcome.Skipped,
                Message:
                    $"Dry run selected {analysis.SelectedTestProjects.Length} test project(s); no tests were executed.",
                BaseRef: requestedBaseRef,
                DryRun: true,
                ImpactUnknown: analysis.ImpactUnknown,
                ChangedFiles: analysis.ChangedFiles,
                SelectedTestProjects: analysis.SelectedTestProjects,
                Reasons: analysis.Reasons,
                Execution: null,
                Error: null,
                ImpactExplanations: analysis.ImpactExplanations
            );
        }

        var selections = analysis
            .SelectedTestProjects.Select(project => new TestSelection(project))
            .ToArray();
        var execution = await _runTestsTool.RunTests(
            selections,
            allowMultipleMatches: false,
            workingDirectory: analysis.WorkingDirectory,
            cancellationToken: cancellationToken
        );

        return new Result(
            RunId: execution.RunId,
            Outcome: execution.Outcome,
            Message: analysis.Message + " " + execution.Message,
            BaseRef: requestedBaseRef,
            DryRun: false,
            ImpactUnknown: analysis.ImpactUnknown,
            ChangedFiles: analysis.ChangedFiles,
            SelectedTestProjects: analysis.SelectedTestProjects,
            Reasons: analysis.Reasons,
            Execution: execution,
            Error: execution.Error?.Reason,
            ImpactExplanations: analysis.ImpactExplanations
        );
    }

    [Description("Result of conservative changed-test selection and optional execution.")]
    public sealed record Result(
        [property: Description("Run identifier, or null when no execution occurred.")] string? RunId,
        [property: Description("Overall selection or execution outcome.")] TestOutcome Outcome,
        [property: Description("Human-readable summary of selection and execution.")] string Message,
        [property: Description("Revision used as the comparison base.")] string BaseRef,
        [property: Description("True when test selection was requested without execution.")] bool DryRun,
        [property: Description("True when the impact analyzer selected all eligible tests conservatively.")]
            bool ImpactUnknown,
        [property: Description("Changed paths reported by Git, including both sides of renames.")]
            string[] ChangedFiles,
        [property: Description("Whole-project test selections passed to RunTestsTool.")]
            string[] SelectedTestProjects,
        [property: Description("Reasons and uncertainty explanations for the selected projects.")]
            string[] Reasons,
        [property: Description("Underlying batch execution result, or null for dry runs and errors.")]
            RunTestsTool.Result? Execution,
        [property: Description("Error detail when Git or required project discovery failed.")] string? Error,
        [property: Description(
            "Bounded project-level changed-path to owner to reverse-dependency to selected-test explanations."
        )]
            ProjectImpactExplanation[] ImpactExplanations
    );

    [Description("One bounded, project-level explanation for a selected whole-project test run.")]
    public sealed record ProjectImpactExplanation(
        [property: Description("Selected whole-project test project.")] string SelectedTestProject,
        [property: Description("Bounded changed-path ownership and dependency chains.")]
            ImpactExplanationChain[] Chains,
        [property: Description("True when this explanation contains an unknown ownership or graph reason.")]
            bool IsUnknown,
        [property: Description("True when one or more chains were omitted by an explanation bound.")]
            bool IsTruncated,
        [property: Description("Explicit unknown, cycle, or truncation reasons.")] string[] Reasons
    );

    [Description("A cycle-safe changed-path to selected-test dependency chain.")]
    public sealed record ImpactExplanationChain(
        [property: Description("Changed Git path, including an old rename path when applicable.")]
            string ChangedPath,
        [property: Description("Evaluated owning project, or null when ownership is unknown.")]
            string? OwnerProject,
        [property: Description("Evaluated reverse-dependency path from owner to selected test project.")]
            string[] ReverseDependencyPath,
        [property: Description("Explicit reason when ownership or dependency traversal is unknown.")]
            string? Reason
    );
}

internal sealed class ChangedTestsImpactAnalyzer
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private const int MaxExplanationProjects = 256;
    private const int MaxExplanationChainsPerProject = 64;
    private const int MaxExplanationDependencyDepth = 64;
    private const int MaxExplanationVisitedProjects = 4_096;
    private const int MaxExplanationCharacters = 32_768;
    private readonly ICommandRunner _commandRunner;
    private readonly McpOptions _options;
    private readonly ProjectMetadataReader _metadataReader;

    public ChangedTestsImpactAnalyzer(
        ICommandRunner commandRunner,
        McpOptions options,
        ProjectMetadataReader? metadataReader = null
    )
    {
        ArgumentNullException.ThrowIfNull(commandRunner);
        ArgumentNullException.ThrowIfNull(options);
        _commandRunner = commandRunner;
        _options = options;
        _metadataReader = metadataReader ?? new ProjectMetadataReader(commandRunner, Options.Create(options));
    }

    public async Task<ImpactAnalysis> AnalyzeAsync(
        string baseRef,
        string? workingDirectory,
        CancellationToken cancellationToken
    )
    {
        var requestedWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? null
            : workingDirectory.Trim();

        var rootResult = await RunAsync(
            new CommandRequest("git", "rev-parse", "--show-toplevel")
            {
                WorkingDirectory = requestedWorkingDirectory,
                ThrowOnNonZeroExitCode = false,
            },
            cancellationToken
        );
        if (rootResult.ExitCode != 0)
            return Failure(
                baseRef,
                [],
                "Unable to determine the Git repository root.",
                DescribeCommandFailure(rootResult)
            );

        var repositoryRoot = FirstNonEmptyLine(GetStandardOutput(rootResult));
        if (repositoryRoot is null)
            return Failure(
                baseRef,
                [],
                "Git did not return a repository root.",
                "git rev-parse --show-toplevel returned empty output."
            );

        repositoryRoot = NormalizeFullPath(repositoryRoot);
        var baseResult = await RunAsync(
            new CommandRequest(
                "git",
                "rev-parse",
                "--verify",
                "--end-of-options",
                $"{baseRef}^{{commit}}"
            )
            {
                WorkingDirectory = repositoryRoot,
                ThrowOnNonZeroExitCode = false,
            },
            cancellationToken
        );
        if (baseResult.ExitCode != 0)
            return Failure(
                baseRef,
                [],
                $"The Git base revision '{baseRef}' is invalid or unavailable; no tests were executed.",
                DescribeCommandFailure(baseResult)
            );

        var resolvedBase = FirstNonEmptyLine(GetStandardOutput(baseResult));
        if (resolvedBase is null)
            return Failure(
                baseRef,
                [],
                $"Git did not resolve the base revision '{baseRef}'; no tests were executed.",
                "git rev-parse --verify returned empty output."
            );

        var diffResult = await RunAsync(
            new CommandRequest(
                "git",
                "diff",
                "--name-status",
                "-z",
                "--find-renames",
                resolvedBase,
                "--"
            )
            {
                WorkingDirectory = repositoryRoot,
                ThrowOnNonZeroExitCode = false,
            },
            cancellationToken
        );
        if (diffResult.ExitCode != 0)
            return Failure(
                baseRef,
                [],
                "Git could not read tracked changes; no tests were executed.",
                DescribeCommandFailure(diffResult)
            );

        var untrackedResult = await RunAsync(
            new CommandRequest("git", "ls-files", "--others", "--exclude-standard", "-z", "--")
            {
                WorkingDirectory = repositoryRoot,
                ThrowOnNonZeroExitCode = false,
            },
            cancellationToken
        );
        if (untrackedResult.ExitCode != 0)
            return Failure(
                baseRef,
                [],
                "Git could not read untracked files; no tests were executed.",
                DescribeCommandFailure(untrackedResult)
            );

        var changes = ParseChanges(GetStandardOutput(diffResult), GetStandardOutput(untrackedResult));
        var changedFiles = changes
            .SelectMany(change => change.Paths)
            .Distinct(PathComparer)
            .ToArray();

        if (changes.Length == 0)
        {
            return new ImpactAnalysis(
                Succeeded: true,
                NoChanges: true,
                ImpactUnknown: false,
                WorkingDirectory: repositoryRoot,
                ChangedFiles: [],
                SelectedTestProjects: [],
                Reasons: ["Compared tracked changes and untracked files against the resolved base revision."],
                Message: "No changed files.",
                Error: null
            );
        }

        var solutionDirectory = FindSolutionDirectory(
            repositoryRoot,
            requestedWorkingDirectory,
            _options.WorkingDirectory
        );
        if (solutionDirectory is null)
        {
            return Failure(
                baseRef,
                changedFiles,
                "No solution file was found from the requested working directory; no tests were executed.",
                "Place the invocation in a solution directory or pass workingDirectory pointing to one."
            );
        }

        string[] eligibleProjects;
        try
        {
            eligibleProjects = await TestProjectDiscovery.ListAsync(
                _commandRunner,
                _options,
                solutionDirectory,
                cancellationToken,
                _metadataReader
            );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var discoveryFailure = exception.Message.Contains(
                "dotnet sln list failed",
                StringComparison.OrdinalIgnoreCase
            );
            return Failure(
                baseRef,
                changedFiles,
                discoveryFailure
                    ? "No solution file could be listed from the requested working directory; no tests were executed."
                    : "Test project discovery failed; no tests were executed.",
                discoveryFailure
                    ? exception.Message
                        + " Pass workingDirectory pointing to the directory containing the intended solution."
                    : exception.Message
            );
        }

        var normalizedEligibleProjects = NormalizeProjects(eligibleProjects, solutionDirectory);
        var eligibleProjectPaths = eligibleProjects
            .Select(project => ResolvePath(solutionDirectory, project))
            .Distinct(PathComparer)
            .ToArray();
        var reasons = new List<string>
        {
            $"Compared tracked and untracked changes against '{baseRef}'.",
        };

        if (changes.Any(change => change.Paths.Any(IsSharedOrBuildPath)))
        {
            reasons.Add(
                "Shared configuration, build, solution, or repository metadata changed; "
                    + "selected all eligible test projects conservatively."
            );
            return CreateSelection(
                solutionDirectory,
                changedFiles,
                normalizedEligibleProjects,
                impactUnknown: true,
                reasons,
                "Selected all eligible test projects because shared files may affect any project."
            );
        }

        var solutionResult = await ListSolutionProjectsAsync(solutionDirectory, cancellationToken);
        if (!solutionResult.Succeeded)
        {
            reasons.Add(solutionResult.Error ?? "The solution project set could not be established.");
            reasons.Add("Selected all eligible test projects because project ownership is unknown.");
            return CreateSelection(
                solutionDirectory,
                changedFiles,
                normalizedEligibleProjects,
                impactUnknown: true,
                reasons,
                "Selected all eligible test projects because the solution project set is unknown."
            );
        }

        if (solutionResult.Projects.Length == 0)
        {
            reasons.Add("The solution contains no project entries; project ownership is unknown.");
            reasons.Add("Selected all eligible test projects conservatively.");
            return CreateSelection(
                solutionDirectory,
                changedFiles,
                normalizedEligibleProjects,
                impactUnknown: true,
                reasons,
                "Selected all eligible test projects because no solution project set was available."
            );
        }

        var graph = await BuildProjectGraphAsync(
            solutionResult.Projects,
            solutionDirectory,
            cancellationToken
        );
        if (graph.Errors.Length > 0)
        {
            reasons.AddRange(graph.Errors);
            reasons.Add(
                "MSBuild project-reference metadata is incomplete; selected all eligible test projects conservatively."
            );
            return CreateSelection(
                solutionDirectory,
                changedFiles,
                normalizedEligibleProjects,
                impactUnknown: true,
                reasons,
                "Selected all eligible test projects because project impact is unknown."
            );
        }

        var ownership = FindOwners(changes, graph.Projects, repositoryRoot);
        if (ownership.Unknown)
        {
            reasons.AddRange(ownership.Reasons);
            reasons.Add(
                "One or more changed paths have unknown or ambiguous ownership; selected all eligible test projects conservatively."
            );
            return new ImpactAnalysis(
                Succeeded: true,
                NoChanges: false,
                ImpactUnknown: true,
                WorkingDirectory: solutionDirectory,
                ChangedFiles: changedFiles,
                SelectedTestProjects: normalizedEligibleProjects,
                Reasons: reasons.Distinct(StringComparer.Ordinal).ToArray(),
                Message: "Selected all eligible test projects because changed-file ownership is unknown.",
                Error: null
            )
            {
                ImpactExplanations = BuildKnownExplanations(
                    eligibleProjectPaths,
                    ownership.PathOwnerships,
                    graph,
                    solutionDirectory
                ),
            };
        }

        var selected = new HashSet<string>(PathComparer);
        foreach (var owner in ownership.Owners)
        {
            foreach (var impacted in graph.GetDependents(owner))
            {
                if (graph.Projects.ContainsKey(impacted) && IsEligible(impacted, eligibleProjectPaths))
                    selected.Add(impacted);
            }
        }

        var selectedFullPaths = OrderForOutput(selected, eligibleProjectPaths).ToArray();
        var selectedProjects = selectedFullPaths
            .Select(project => NormalizeProject(project, solutionDirectory))
            .ToArray();
        if (selectedProjects.Length == 0)
        {
            reasons.Add("No eligible test project transitively depends on the changed project(s).");
            return new ImpactAnalysis(
                Succeeded: true,
                NoChanges: false,
                ImpactUnknown: false,
                WorkingDirectory: solutionDirectory,
                ChangedFiles: changedFiles,
                SelectedTestProjects: [],
                Reasons: reasons.ToArray(),
                Message: "No eligible test projects were affected.",
                Error: null
            );
        }

        reasons.Add(
            "Selected affected test projects through evaluated transitive ProjectReference dependencies; "
                + "impact is conservative at project level and does not claim method-level coverage."
        );
        return new ImpactAnalysis(
            Succeeded: true,
            NoChanges: false,
            ImpactUnknown: false,
            WorkingDirectory: solutionDirectory,
            ChangedFiles: changedFiles,
            SelectedTestProjects: selectedProjects,
            Reasons: reasons.ToArray(),
            Message: $"Selected {selectedProjects.Length} affected test project(s).",
            Error: null
        )
        {
            ImpactExplanations = BuildKnownExplanations(
                selectedFullPaths,
                ownership.PathOwnerships,
                graph,
                solutionDirectory
            ),
        };
    }

    private async Task<ProjectGraphResult> BuildProjectGraphAsync(
        string[] projectPaths,
        string projectWorkingDirectory,
        CancellationToken cancellationToken
    )
    {
        var projects = new Dictionary<string, ProjectMetadata>(PathComparer);
        var errors = new List<string>();

        foreach (var projectPath in projectPaths)
        {
            string fullPath;
            try
            {
                fullPath = ResolvePath(projectWorkingDirectory, projectPath);
            }
            catch (Exception exception)
            {
                errors.Add(
                    $"Solution project path '{projectPath}' could not be resolved: {exception.Message}"
                );
                continue;
            }

            var evaluation = await _metadataReader.ReadProjectAsync(
                projectPath,
                projectWorkingDirectory,
                cancellationToken
            );

            if (!evaluation.EvaluationSucceeded)
            {
                errors.Add(
                    $"MSBuild evaluation failed for '{projectPath}' and impact is unknown: "
                        + (evaluation.EvaluationError ?? "No diagnostic output.")
                );
                continue;
            }

            if (HasMultipleTargetFrameworks(evaluation.Properties))
            {
                errors.Add(
                    $"Project '{projectPath}' targets multiple frameworks; conditional ProjectReference impact "
                        + "could not be established for every target framework."
                );
            }

            try
            {
                var references = ReadMetadataItemPaths(
                    evaluation.ProjectReferences,
                    Path.GetDirectoryName(fullPath) ?? projectWorkingDirectory
                );
                var compileFiles = ReadMetadataItemPaths(
                    evaluation.CompileItems,
                    Path.GetDirectoryName(fullPath) ?? projectWorkingDirectory
                );
                projects[fullPath] = new ProjectMetadata(
                    fullPath,
                    references,
                    compileFiles
                );
            }
            catch (Exception exception)
            {
                errors.Add(
                    $"Evaluated MSBuild metadata for '{projectPath}' could not be normalized: "
                        + exception.Message
                );
            }
        }

        if (errors.Count > 0)
            return new ProjectGraphResult(
                projects,
                new Dictionary<string, HashSet<string>>(PathComparer),
                errors.Distinct(StringComparer.Ordinal).ToArray()
            );

        var reverseReferences = projects.Keys.ToDictionary(
            project => project,
            _ => new HashSet<string>(PathComparer),
            PathComparer
        );
        foreach (var project in projects.Values)
        {
            foreach (var reference in project.ProjectReferences)
            {
                string referencedPath;
                try
                {
                    referencedPath = ResolvePath(Path.GetDirectoryName(project.FullPath)!, reference);
                }
                catch (Exception exception)
                {
                    errors.Add(
                        $"ProjectReference '{reference}' from '{project.FullPath}' could not be resolved: "
                            + exception.Message
                    );
                    continue;
                }

                if (!projects.ContainsKey(referencedPath))
                {
                    errors.Add(
                        $"ProjectReference '{reference}' from '{project.FullPath}' is not in the solution project set."
                    );
                    continue;
                }

                reverseReferences[referencedPath].Add(project.FullPath);
            }
        }

        return new ProjectGraphResult(projects, reverseReferences, errors.Distinct().ToArray());
    }


    private async Task<SolutionProjectsResult> ListSolutionProjectsAsync(
        string solutionDirectory,
        CancellationToken cancellationToken
    )
    {
        var result = await RunAsync(
            new CommandRequest("dotnet", "sln", "list")
            {
                WorkingDirectory = solutionDirectory,
                ThrowOnNonZeroExitCode = false,
            },
            cancellationToken
        );
        if (result.ExitCode != 0)
            return new SolutionProjectsResult(false, [], DescribeCommandFailure(result));

        var lines = result.StandardOutputLines.Length > 0
            ? result.StandardOutputLines
            : result.StandardOutput.Split('\n');
        var headerIndex = Array.FindIndex(
            lines,
            line => string.Equals(line.Trim(), "Project(s)", StringComparison.OrdinalIgnoreCase)
        );
        if (headerIndex < 0)
            return new SolutionProjectsResult(
                false,
                [],
                $"dotnet sln list returned unexpected output: {GetStandardOutput(result).Trim()}"
            );

        var projects = lines
            .Skip(headerIndex + 1)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.All(character => character == '-'))
            .ToArray();
        try
        {
            return new SolutionProjectsResult(
                true,
                NormalizeProjects(projects, solutionDirectory),
                null
            );
        }
        catch (Exception exception)
        {
            return new SolutionProjectsResult(
                false,
                [],
                $"Solution project paths could not be normalized: {exception.Message}"
            );
        }
    }

    private OwnershipResult FindOwners(
        ChangedFile[] changes,
        IReadOnlyDictionary<string, ProjectMetadata> projects,
        string repositoryRoot
    )
    {
        var owners = new HashSet<string>(PathComparer);
        var reasons = new List<string>();
        var pathOwnerships = new List<PathOwnership>();
        var unknown = false;

        foreach (
            var path in changes.SelectMany(change => change.Paths).Distinct(PathComparer)
        )
        {
            string fullPath;
            try
            {
                fullPath = ResolvePath(repositoryRoot, path);
            }
            catch (Exception exception)
            {
                unknown = true;
                var reason = $"Changed path '{path}' could not be normalized: {exception.Message}";
                reasons.Add(reason);
                pathOwnerships.Add(new PathOwnership(path, [], reason));
                continue;
            }

            var exactOwners = projects.Values
                .Where(project => project.CompileFiles.Contains(fullPath, PathComparer))
                .Select(project => project.FullPath)
                .Distinct(PathComparer)
                .ToArray();
            if (exactOwners.Length > 0)
            {
                foreach (var owner in exactOwners)
                    owners.Add(owner);
                pathOwnerships.Add(new PathOwnership(path, exactOwners, null));
                continue;
            }

            unknown = true;
            var unknownReason =
                $"Could not establish a unique project owner for changed path '{path}': "
                + "No evaluated Compile item established ownership; directory containment was not used "
                + "for deleted or renamed-old paths.";
            reasons.Add(unknownReason);
            pathOwnerships.Add(new PathOwnership(path, [], unknownReason));
        }

        return new OwnershipResult(
            unknown,
            owners.ToArray(),
            reasons.Distinct(StringComparer.Ordinal).ToArray(),
            pathOwnerships.ToArray()
        );
    }

    private static string[] ReadMetadataItemPaths(
        IReadOnlyList<ProjectMetadataItem> items,
        string projectDirectory
    )
    {
        var result = new HashSet<string>(PathComparer);
        foreach (var item in items)
        {
            var path = item.FullPath;
            var itemBaseDirectory = projectDirectory;
            if (
                string.IsNullOrWhiteSpace(path)
                && !string.IsNullOrWhiteSpace(item.DefiningProjectFullPath)
            )
            {
                var definingProjectPath = ResolvePath(
                    projectDirectory,
                    item.DefiningProjectFullPath
                );
                itemBaseDirectory = Path.GetDirectoryName(definingProjectPath) ?? projectDirectory;
            }
            if (string.IsNullOrWhiteSpace(path))
                path = item.EvaluatedInclude;
            if (string.IsNullOrWhiteSpace(path))
                path = item.Identity;
            if (string.IsNullOrWhiteSpace(path))
                continue;
            result.Add(ResolvePath(itemBaseDirectory, path));
        }

        return result.ToArray();
    }


    private static ChangedFile[] ParseChanges(string diffOutput, string untrackedOutput)
    {
        var changes = new List<ChangedFile>();
        var tokens = SplitNullDelimited(diffOutput);
        for (var index = 0; index < tokens.Count; index++)
        {
            var status = tokens[index];
            if (status.Length == 0)
                continue;

            var kind = status[0];
            if (kind is 'R' or 'C')
            {
                if (index + 2 >= tokens.Count)
                    break;
                var oldPath = tokens[++index];
                var newPath = tokens[++index];
                changes.Add(new ChangedFile(oldPath, newPath));
            }
            else
            {
                if (index + 1 >= tokens.Count)
                    break;
                changes.Add(new ChangedFile(null, tokens[++index]));
            }
        }

        foreach (var path in SplitNullDelimited(untrackedOutput))
        {
            if (path.Length > 0)
                changes.Add(new ChangedFile(null, path));
        }

        return changes
            .Where(change => change.Paths.Any(path => path.Length > 0))
            .ToArray();
    }

    private static List<string> SplitNullDelimited(string output)
    {
        return output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.Length > 0)
            .ToList();
    }

    private static bool IsSharedOrBuildPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var fileName = Path.GetFileName(normalized);
        var extension = Path.GetExtension(fileName);
        if (
            string.Equals(fileName, "global.json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, ".gitignore", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, ".gitattributes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "NuGet.Config", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "nuget.config", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("Directory.Packages.", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("Directory.Solution.", StringComparison.OrdinalIgnoreCase)
            || extension is ".props" or ".targets" or ".sln" or ".slnx"
        )
            return true;

        return extension is ".csproj" or ".fsproj" or ".vbproj" or ".proj";
    }


    private static bool IsWithin(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && !relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !relative.StartsWith("../", StringComparison.Ordinal));
    }

    private static bool IsEligible(string project, IReadOnlyCollection<string> eligibleProjects)
    {
        return eligibleProjects.Contains(project, PathComparer);
    }

    private static string[] OrderForOutput(
        IEnumerable<string> selected,
        IReadOnlyList<string> eligibleProjects
    )
    {
        var selectedSet = new HashSet<string>(selected, PathComparer);
        return eligibleProjects.Where(selectedSet.Contains).ToArray();
    }

    private static string[] NormalizeProjects(IEnumerable<string> projects, string baseDirectory)
    {
        return projects
            .Select(project => NormalizeProject(project, baseDirectory))
            .Where(project => project.Length > 0)
            .Distinct(PathComparer)
            .ToArray();
    }

    private static string NormalizeProject(string project, string baseDirectory)
    {
        var fullPath = ResolvePath(baseDirectory, project);
        var relativePath = Path.GetRelativePath(baseDirectory, fullPath);
        return relativePath == "."
            ? Path.GetFileName(fullPath)
            : relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string? FindSolutionDirectory(
        string repositoryRoot,
        string? requestedWorkingDirectory,
        string? defaultWorkingDirectory
    )
    {
        try
        {
            repositoryRoot = NormalizeFullPath(repositoryRoot);
            var startPath = string.IsNullOrWhiteSpace(requestedWorkingDirectory)
                ? defaultWorkingDirectory
                : requestedWorkingDirectory;
            var current = string.IsNullOrWhiteSpace(startPath)
                ? repositoryRoot
                : NormalizeFullPath(startPath);

            if (!Directory.Exists(current) || !IsWithin(repositoryRoot, current))
                return null;

            while (true)
            {
                var solution = Directory
                    .EnumerateFiles(current, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(path =>
                        path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                    )
                    .OrderBy(path => path, PathComparer)
                    .FirstOrDefault();
                if (solution is not null)
                    return current;

                if (PathComparer.Equals(current, repositoryRoot))
                    break;

                var parent = Directory.GetParent(current)?.FullName;
                if (parent is null || PathComparer.Equals(parent, current))
                    break;
                current = NormalizeFullPath(parent);
            }
        }
        catch
        {
            // The caller reports an actionable error when no solution directory can be established.
        }

        return null;
    }

    private static string ResolvePath(string baseDirectory, string path)
    {
        var normalizedPath = path.Replace('\\', Path.DirectorySeparatorChar);
        return NormalizeFullPath(
            Path.IsPathRooted(normalizedPath)
                ? normalizedPath
                : Path.Combine(baseDirectory, normalizedPath)
        );
    }

    private static string NormalizeFullPath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string? FirstNonEmptyLine(string output)
    {
        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);
    }


    private static bool HasMultipleTargetFrameworks(
        IReadOnlyDictionary<string, string> properties
    )
    {
        var frameworks = properties
            .Where(pair => string.Equals(pair.Key, "TargetFrameworks", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .FirstOrDefault();
        return !string.IsNullOrWhiteSpace(frameworks)
            && frameworks.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
                .Length > 1;
    }

    private async Task<CommandResult> RunAsync(
        CommandRequest request,
        CancellationToken cancellationToken
    ) => await _commandRunner.RunAsync(request, cancellationToken);

    private static RunChangedTestsTool.ProjectImpactExplanation[] BuildUnknownExplanations(
        IReadOnlyList<string> selectedProjects,
        IReadOnlyList<string> changedFiles,
        IReadOnlyList<string> reasons
    )
    {
        if (selectedProjects.Count == 0)
            return [];

        if (changedFiles.Any(IsSharedOrBuildPath))
            return BuildSharedUnknownExplanations(selectedProjects);

        var reason = BuildBoundedReason(reasons, "Changed-path ownership or project impact is unknown.");
        var paths = changedFiles.Distinct(PathComparer).ToArray();
        var projectCount = Math.Min(selectedProjects.Count, MaxExplanationProjects);
        var result = new List<RunChangedTestsTool.ProjectImpactExplanation>(projectCount + 1);
        var characterCount = 0;
        for (var projectIndex = 0; projectIndex < projectCount; projectIndex++)
        {
            var chains = new List<RunChangedTestsTool.ImpactExplanationChain>(
                Math.Min(paths.Length, MaxExplanationChainsPerProject)
            );
            var truncated = false;
            foreach (var path in paths)
            {
                if (chains.Count >= MaxExplanationChainsPerProject)
                {
                    truncated = true;
                    break;
                }

                if (
                    !TryAddExplanationChain(
                        chains,
                        path,
                        null,
                        [],
                        reason,
                        ref characterCount
                    )
                )
                {
                    truncated = true;
                    break;
                }
            }

            var projectReasons = new List<string>();
            if (truncated)
            {
                TryAddExplanationReason(
                    projectReasons,
                    $"Explanation chains were truncated after {MaxExplanationChainsPerProject} chains "
                        + $"or {MaxExplanationCharacters} aggregate characters.",
                    ref characterCount
                );
            }
            result.Add(
                new RunChangedTestsTool.ProjectImpactExplanation(
                    selectedProjects[projectIndex],
                    chains.ToArray(),
                    IsUnknown: true,
                    IsTruncated: truncated,
                    Reasons: projectReasons.Distinct(StringComparer.Ordinal).ToArray()
                )
            );
        }

        if (selectedProjects.Count > projectCount)
        {
            result.Add(
                new RunChangedTestsTool.ProjectImpactExplanation(
                    "<additional selected projects omitted>",
                    [],
                    IsUnknown: true,
                    IsTruncated: true,
                    Reasons: [$"Explanation project output was bounded at {MaxExplanationProjects} projects."]
                )
            );
        }

        return result.ToArray();
    }

    private static RunChangedTestsTool.ProjectImpactExplanation[] BuildSharedUnknownExplanations(
        IReadOnlyList<string> selectedProjects
    )
    {
        var projectCount = Math.Min(selectedProjects.Count, MaxExplanationProjects);
        var result = new List<RunChangedTestsTool.ProjectImpactExplanation>(projectCount + 1);
        for (var projectIndex = 0; projectIndex < projectCount; projectIndex++)
        {
            result.Add(
                new RunChangedTestsTool.ProjectImpactExplanation(
                    selectedProjects[projectIndex],
                    [],
                    IsUnknown: true,
                    IsTruncated: false,
                    Reasons: []
                )
            );
        }

        if (selectedProjects.Count > projectCount)
        {
            result.Add(
                new RunChangedTestsTool.ProjectImpactExplanation(
                    "<additional selected projects omitted>",
                    [],
                    IsUnknown: true,
                    IsTruncated: true,
                    Reasons: [$"Explanation project output was bounded at {MaxExplanationProjects} projects."]
                )
            );
        }

        return result.ToArray();
    }

    private static RunChangedTestsTool.ProjectImpactExplanation[] BuildKnownExplanations(
        IReadOnlyList<string> selectedProjects,
        IReadOnlyList<PathOwnership> pathOwnerships,
        ProjectGraphResult graph,
        string solutionDirectory
    )
    {
        var projectCount = Math.Min(selectedProjects.Count, MaxExplanationProjects);
        var result = new List<RunChangedTestsTool.ProjectImpactExplanation>(projectCount + 1);
        var characterCount = 0;
        for (var projectIndex = 0; projectIndex < projectCount; projectIndex++)
        {
            var selectedProject = selectedProjects[projectIndex];
            var selectedProjectDisplay = NormalizeProject(selectedProject, solutionDirectory);
            var chains = new List<RunChangedTestsTool.ImpactExplanationChain>();
            var reasons = new List<string>();
            var unknown = false;
            var truncated = false;

            foreach (var pathOwnership in pathOwnerships)
            {
                if (pathOwnership.Owners.Length == 0)
                {
                    unknown = true;
                    var unknownReason =
                        pathOwnership.UnknownReason
                        ?? $"No evaluated owner was established for changed path '{pathOwnership.ChangedPath}'.";
                    if (!TryAddExplanationChain(
                            chains,
                            pathOwnership.ChangedPath,
                            null,
                            [],
                            unknownReason,
                            ref characterCount
                        ))
                    {
                        truncated = true;
                        break;
                    }

                    continue;
                }

                foreach (var owner in pathOwnership.Owners)
                {
                    if (chains.Count >= MaxExplanationChainsPerProject)
                    {
                        truncated = true;
                        break;
                    }

                    var dependencyPath = graph.FindReverseDependencyPath(
                        owner,
                        selectedProject,
                        MaxExplanationDependencyDepth
                    );
                    var chainReason = dependencyPath.Reason;
                    if (dependencyPath.Path is null)
                    {
                        unknown = true;
                        chainReason ??=
                            $"No evaluated reverse dependency path from '{NormalizeProject(owner, solutionDirectory)}' "
                            + $"to selected test project '{selectedProjectDisplay}'.";
                        if (!TryAddExplanationReason(reasons, chainReason, ref characterCount))
                            truncated = true;
                    }
                    else if (dependencyPath.CycleDetected)
                    {
                        var cycleReason =
                            $"Cycle-safe traversal skipped repeated reverse-dependency edges while tracing "
                            + $"'{pathOwnership.ChangedPath}' to '{selectedProjectDisplay}'.";
                        if (!TryAddExplanationReason(reasons, cycleReason, ref characterCount))
                            truncated = true;
                    }
                    if (dependencyPath.Truncated)
                    {
                        truncated = true;
                        if (
                            chainReason is not null
                            && !TryAddExplanationReason(reasons, chainReason, ref characterCount)
                        )
                            truncated = true;
                    }

                    var dependencyDisplay = dependencyPath.Path is null
                        ? Array.Empty<string>()
                        : dependencyPath.Path
                            .Select(project => NormalizeProject(project, solutionDirectory))
                            .ToArray();
                    if (
                        !TryAddExplanationChain(
                            chains,
                            pathOwnership.ChangedPath,
                            NormalizeProject(owner, solutionDirectory),
                            dependencyDisplay,
                            chainReason,
                            ref characterCount
                        )
                    )
                    {
                        truncated = true;
                        break;
                    }
                }

                if (truncated)
                    break;
            }

            if (truncated)
            {
                TryAddExplanationReason(
                    reasons,
                    $"Explanation chains were truncated after {MaxExplanationChainsPerProject} chains, "
                        + $"{MaxExplanationDependencyDepth} dependency steps, or {MaxExplanationCharacters} aggregate characters.",
                    ref characterCount
                );
            }
            result.Add(
                new RunChangedTestsTool.ProjectImpactExplanation(
                    selectedProjectDisplay,
                    chains.ToArray(),
                    unknown,
                    truncated,
                    reasons.Distinct(StringComparer.Ordinal).ToArray()
                )
            );
        }

        if (selectedProjects.Count > projectCount)
        {
            result.Add(
                new RunChangedTestsTool.ProjectImpactExplanation(
                    "<additional selected projects omitted>",
                    [],
                    IsUnknown: true,
                    IsTruncated: true,
                    Reasons: [$"Explanation project output was bounded at {MaxExplanationProjects} projects."]
                )
            );
        }

        return result.ToArray();
    }

    private static bool TryAddExplanationReason(
        ICollection<string> reasons,
        string reason,
        ref int characterCount
    )
    {
        if (reasons.Contains(reason, StringComparer.Ordinal))
            return true;

        if (characterCount + reason.Length > MaxExplanationCharacters)
            return false;

        reasons.Add(reason);
        characterCount += reason.Length;
        return true;
    }

    private static bool TryAddExplanationChain(
        ICollection<RunChangedTestsTool.ImpactExplanationChain> chains,
        string changedPath,
        string? ownerProject,
        string[] dependencyPath,
        string? reason,
        ref int characterCount
    )
    {
        if (chains.Count >= MaxExplanationChainsPerProject)
            return false;

        var chainCharacters =
            changedPath.Length
            + (ownerProject?.Length ?? 0)
            + dependencyPath.Sum(path => path.Length)
            + (reason?.Length ?? 0);
        if (characterCount + chainCharacters > MaxExplanationCharacters)
            return false;

        chains.Add(
            new RunChangedTestsTool.ImpactExplanationChain(
                changedPath,
                ownerProject,
                dependencyPath,
                reason
            )
        );
        characterCount += chainCharacters;
        return true;
    }

    private static string BuildBoundedReason(
        IReadOnlyList<string> reasons,
        string fallback
    )
    {
        var combined = reasons.Count == 0
            ? string.Empty
            : string.Join(" ", reasons.Take(8).Where(value => !string.IsNullOrWhiteSpace(value)));
        var reason = string.IsNullOrWhiteSpace(combined) ? fallback : combined;
        if (reason.Length <= 2_048)
            return reason;
        return reason[..2_048] + " [reason truncated]";
    }

    private static ImpactAnalysis CreateSelection(
        string workingDirectory,
        string[] changedFiles,
        string[] eligibleProjects,
        bool impactUnknown,
        List<string> reasons,
        string message
    )
    {
        var distinctReasons = reasons.Distinct(StringComparer.Ordinal).ToArray();
        return new ImpactAnalysis(
            Succeeded: true,
            NoChanges: false,
            ImpactUnknown: impactUnknown,
            WorkingDirectory: workingDirectory,
            ChangedFiles: changedFiles,
            SelectedTestProjects: eligibleProjects,
            Reasons: distinctReasons,
            Message: message,
            Error: null
        )
        {
            ImpactExplanations = BuildUnknownExplanations(
                eligibleProjects,
                changedFiles,
                distinctReasons
            ),
        };
    }

    private static ImpactAnalysis Failure(
        string baseRef,
        string[] changedFiles,
        string message,
        string error
    ) => new(
        Succeeded: false,
        NoChanges: false,
        ImpactUnknown: false,
        WorkingDirectory: null,
        ChangedFiles: changedFiles,
        SelectedTestProjects: [],
        Reasons: [$"Base revision: {baseRef}"],
        Message: message,
        Error: error
    );

    private static string DescribeCommandFailure(CommandResult result)
    {
        var details = string.IsNullOrWhiteSpace(result.StandardError)
            ? GetStandardOutput(result).Trim()
            : result.StandardError.Trim();
        return string.IsNullOrWhiteSpace(details)
            ? $"Command exited with code {result.ExitCode}."
            : $"Command exited with code {result.ExitCode}: {details}";
    }

    private static string GetStandardOutput(CommandResult result)
    {
        return string.IsNullOrEmpty(result.StandardOutput)
            && result.StandardOutputLines.Length > 0
            ? string.Join(Environment.NewLine, result.StandardOutputLines)
            : result.StandardOutput;
    }

    private sealed record ChangedFile(string? OldPath, string? NewPath)
    {
        public IEnumerable<string> Paths
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(OldPath))
                    yield return OldPath;
                if (!string.IsNullOrWhiteSpace(NewPath))
                    yield return NewPath;
            }
        }
    }

    private sealed record ProjectMetadata(
        string FullPath,
        string[] ProjectReferences,
        string[] CompileFiles
    );

    private sealed record ProjectGraphResult(
        IReadOnlyDictionary<string, ProjectMetadata> Projects,
        IReadOnlyDictionary<string, HashSet<string>> ReverseReferences,
        string[] Errors
    )
    {
        public IEnumerable<string> GetDependents(string project)
        {
            var visited = new HashSet<string>(PathComparer);
            var pending = new Queue<string>();
            pending.Enqueue(project);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                if (!visited.Add(current))
                    continue;
                yield return current;
                if (!ReverseReferences.TryGetValue(current, out var dependents))
                    continue;
                foreach (var dependent in dependents.OrderBy(value => value, PathComparer))
                    pending.Enqueue(dependent);
            }
        }

        public DependencyPathResult FindReverseDependencyPath(
            string owner,
            string selectedProject,
            int maxDepth
        )
        {
            var pending = new Queue<(string Project, string[] Path)>();
            var visited = new HashSet<string>(PathComparer) { owner };
            var cycleDetected = false;
            var truncated = false;
            string[]? foundPath = null;
            pending.Enqueue((owner, [owner]));

            while (pending.Count > 0)
            {
                var (current, path) = pending.Dequeue();
                if (PathComparer.Equals(current, selectedProject))
                {
                    foundPath ??= path;
                    continue;
                }

                if (path.Length >= maxDepth)
                {
                    truncated = true;
                    continue;
                }

                if (!ReverseReferences.TryGetValue(current, out var dependents))
                    continue;
                foreach (var dependent in dependents.OrderBy(value => value, PathComparer))
                {
                    if (path.Contains(dependent, PathComparer))
                    {
                        cycleDetected = true;
                        continue;
                    }

                    if (!visited.Contains(dependent) && visited.Count >= MaxExplanationVisitedProjects)
                    {
                        truncated = true;
                        continue;
                    }

                    if (!visited.Add(dependent))
                        continue;

                    pending.Enqueue((dependent, [.. path, dependent]));
                }
            }

            var reason = truncated
                ? $"Reverse-dependency provenance was truncated at {maxDepth} project steps "
                    + $"or {MaxExplanationVisitedProjects} visited projects."
                : cycleDetected
                    ? "Reverse-dependency traversal stopped at repeated project edges in a cycle."
                    : null;
            return new DependencyPathResult(foundPath, cycleDetected, truncated, reason);
        }
    }

    private sealed record DependencyPathResult(
        string[]? Path,
        bool CycleDetected,
        bool Truncated,
        string? Reason
    );

    private sealed record SolutionProjectsResult(bool Succeeded, string[] Projects, string? Error);

    private sealed record PathOwnership(
        string ChangedPath,
        string[] Owners,
        string? UnknownReason
    );

    private sealed record OwnershipResult(
        bool Unknown,
        string[] Owners,
        string[] Reasons,
        PathOwnership[] PathOwnerships
    );

    internal sealed record ImpactAnalysis(
        bool Succeeded,
        bool NoChanges,
        bool ImpactUnknown,
        string? WorkingDirectory,
        string[] ChangedFiles,
        string[] SelectedTestProjects,
        string[] Reasons,
        string Message,
        string? Error
    )
    {
        public RunChangedTestsTool.ProjectImpactExplanation[] ImpactExplanations { get; init; } = [];
    }
}
