using System.Text.Json;
using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;

namespace DotnetTestRunnerMcp.Tools;

internal static class CtrfTestRun
{
    private static readonly TimeSpan DefaultTestRunTimeout = TimeSpan.FromSeconds(180);

    internal sealed record Result(
        CommandResult CommandResult,
        CtrfReport? Report,
        bool ReportFileFound,
        string? ReadErrorMessage
    );

    internal sealed record RunOptions(TimeSpan Timeout, int? MaxOutputChars);

    internal static RunOptions CreateRunOptions(McpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var timeout =
            options.TestRunTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(options.TestRunTimeoutSeconds)
                : DefaultTestRunTimeout;

        return new RunOptions(timeout, MaxOutputChars: null);
    }

    internal static async Task<Result> ExecuteAsync(
        ICommandRunner commandRunner,
        JsonSerializerOptions jsonOptions,
        IReadOnlyList<string> arguments,
        bool disableCtrf,
        McpOptions options,
        string? workingDirectory,
        CancellationToken cancellationToken
    ) =>
        await ExecuteAsync(
            commandRunner,
            jsonOptions,
            arguments,
            disableCtrf,
            supportsCtrf: true,
            options,
            workingDirectory,
            cancellationToken
        );

    internal static async Task<Result> ExecuteAsync(
        ICommandRunner commandRunner,
        JsonSerializerOptions jsonOptions,
        IReadOnlyList<string> arguments,
        bool disableCtrf,
        bool supportsCtrf,
        McpOptions options,
        string? workingDirectory,
        CancellationToken cancellationToken
    )
    {
        var ctrfEnabled = !disableCtrf && supportsCtrf;
        var runOptions = CreateRunOptions(options);

        var firstAttempt = await ExecuteAttemptAsync(
            commandRunner,
            jsonOptions,
            arguments,
            ctrfEnabled,
            runOptions,
            workingDirectory,
            cancellationToken
        );

        if (!ctrfEnabled || firstAttempt.Report is not null)
            return firstAttempt;

        var firstAttemptKind = ClassifyErrorKind(
            firstAttempt.CommandResult,
            firstAttempt.ReportFileFound,
            firstAttempt.ReadErrorMessage
        );

        if (firstAttemptKind != ErrorKind.InvocationError)
            return firstAttempt;

        return await ExecuteAttemptAsync(
            commandRunner,
            jsonOptions,
            arguments,
            includeCtrfArgs: false,
            runOptions,
            workingDirectory,
            cancellationToken
        );
    }

    private static async Task<Result> ExecuteAttemptAsync(
        ICommandRunner commandRunner,
        JsonSerializerOptions jsonOptions,
        IReadOnlyList<string> arguments,
        bool includeCtrfArgs,
        RunOptions runOptions,
        string? workingDirectory,
        CancellationToken cancellationToken
    )
    {
        var ctrfFileName = $"TestResults_{Guid.NewGuid():N}.ctrf";
        var tempPath = Path.GetTempPath();
        var fullCtrfPath = Path.Combine(tempPath, ctrfFileName);

        var commandArguments = new List<string>(arguments);
        if (includeCtrfArgs)
            commandArguments.AddRange([
                "--report-ctrf",
                "--report-ctrf-filename",
                ctrfFileName,
                "--results-directory",
                tempPath,
                "--no-ansi",
                "--no-progress",
            ]);

        CommandResult commandResult;
        try
        {
            commandResult = await commandRunner.RunAsync(
                new CommandRequest("dotnet", commandArguments.ToArray())
                {
                    WorkingDirectory = workingDirectory,
                    ThrowOnNonZeroExitCode = false,
                    Timeout = runOptions.Timeout,
                    MaxOutputChars = runOptions.MaxOutputChars,
                },
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            commandResult = new CommandResult(-1, string.Empty, string.Empty, [], [], TimeSpan.Zero)
            {
                FileName = "dotnet",
                Arguments = commandArguments.ToArray(),
                WorkingDirectory = workingDirectory,
                ExceptionType = exception.GetType().FullName,
                ExceptionMessage = exception.Message,
                ExceptionStackTrace = exception.StackTrace,
            };
        }

        CtrfReport? report = null;
        string? readErrorMessage = null;
        var reportFileFound = File.Exists(fullCtrfPath);

        try
        {
            if (reportFileFound)
            {
                var json = await File.ReadAllTextAsync(fullCtrfPath, cancellationToken);
#pragma warning disable IL2026, IL3050
                report = JsonSerializer.Deserialize<CtrfReport>(json, jsonOptions);
#pragma warning restore IL2026, IL3050
                if (report is null)
                    readErrorMessage = "Failed to read test results.";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            readErrorMessage = "Failed to read test results.";
        }
        finally
        {
            TryDelete(fullCtrfPath);
        }

        return new Result(commandResult, report, reportFileFound, readErrorMessage);
    }

    internal static ErrorKind ClassifyErrorKind(
        CommandResult commandResult,
        bool reportFileFound,
        string? readErrorMessage
    )
    {
        if (!string.IsNullOrWhiteSpace(readErrorMessage))
            return ErrorKind.ReadFailed;

        var output = BuildOutputCombined(commandResult);
        if (!string.IsNullOrWhiteSpace(commandResult.ExceptionMessage))
            return ErrorKind.InvocationError;

        if (
            ContainsErrorToken(
                output,
                "Specifying a project for 'dotnet test' should be via '--project'"
            )
            || ContainsErrorToken(output, "Unrecognized command or argument")
            || ContainsErrorToken(output, "Unknown option")
            || (
                ContainsErrorToken(output, "Unknown switch")
                && ContainsErrorToken(output, "--report-ctrf")
            )
        )
            return ErrorKind.InvocationError;

        if (
            ContainsErrorToken(output, "testhost")
            || ContainsErrorToken(output, "TypeLoadException")
            || ContainsErrorToken(output, "Unhandled exception")
        )
            return ErrorKind.TestHostCrashed;

        if (
            ContainsErrorToken(output, "Build FAILED")
            || ContainsErrorToken(output, "error CS")
            || ContainsErrorToken(output, "error MSB")
        )
            return ErrorKind.BuildFailed;

        if (
            ContainsErrorToken(output, "Discovered 0 tests")
            || ContainsErrorToken(output, "No tests available")
            || ContainsErrorToken(output, "No test is available")
            || ContainsErrorToken(output, "No tests to run")
        )
            return ErrorKind.NoTestsDiscovered;

        if (
            ContainsErrorToken(output, "Test discovery")
            || ContainsErrorToken(output, "Discovering tests")
            || ContainsErrorToken(output, "Failed to discover tests")
        )
            return ErrorKind.DiscoveryFailed;

        if (!reportFileFound)
            return ErrorKind.ResultFileMissing;

        return ErrorKind.Unknown;
    }

    internal static ErrorInfo BuildErrorInfo(
        CommandResult commandResult,
        string? readErrorMessage,
        ErrorKind errorKind,
        string? toolName = null,
        string? projectPath = null
    )
    {
        var stderr = string.IsNullOrEmpty(commandResult.StandardError)
            ? null
            : new TruncatedText(
                commandResult.StandardError,
                false,
                commandResult.StandardError.Length,
                commandResult.StandardError.Count(c => c == '\n') + 1
            );

        var stdout = string.IsNullOrEmpty(commandResult.StandardOutput)
            ? null
            : new TruncatedText(
                commandResult.StandardOutput,
                false,
                commandResult.StandardOutput.Length,
                commandResult.StandardOutput.Count(c => c == '\n') + 1
            );

        var reason = readErrorMessage;
        if (string.IsNullOrWhiteSpace(reason))
        {
            if (!string.IsNullOrWhiteSpace(commandResult.ExceptionMessage))
                reason = commandResult.ExceptionMessage;
            else if (!string.IsNullOrWhiteSpace(commandResult.StandardError))
                reason = commandResult.StandardError;
            else if (!string.IsNullOrWhiteSpace(commandResult.StandardOutput))
                reason = commandResult.StandardOutput;
            else
                reason = "Test execution failed.";
        }

        return new ErrorInfo(errorKind, reason, stdout, stderr)
        {
            ToolName = toolName,
            ProjectPath = projectPath,
            Command = commandResult.FileName,
            Arguments = commandResult.Arguments,
            WorkingDirectory = commandResult.WorkingDirectory,
            ExitCode = commandResult.ExitCode,
            ExceptionType = commandResult.ExceptionType,
            ExceptionMessage = commandResult.ExceptionMessage,
            ExceptionStackTrace = commandResult.ExceptionStackTrace,
        };
    }

    internal static int? TryParseDurationMilliseconds(long? duration)
    {
        if (!duration.HasValue || duration.Value <= 0)
            return null;

        return duration.Value > int.MaxValue ? int.MaxValue : (int)duration.Value;
    }

    private static string BuildOutputCombined(CommandResult commandResult)
    {
        if (string.IsNullOrWhiteSpace(commandResult.StandardError))
            return commandResult.StandardOutput ?? string.Empty;

        if (string.IsNullOrWhiteSpace(commandResult.StandardOutput))
            return commandResult.StandardError ?? string.Empty;

        return string.Concat(
            commandResult.StandardError,
            Environment.NewLine,
            commandResult.StandardOutput
        );
    }

    private static bool ContainsErrorToken(string source, string token) =>
        source.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        if (!File.Exists(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Ignored.
        }
    }
}
