using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace DotnetTest.Mcp.Terminal;

public sealed class ProcessCommandRunner : ICommandRunner
{
    private readonly string? _defaultWorkingDirectory;

    public ProcessCommandRunner(IOptions<McpOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _defaultWorkingDirectory = options.Value.WorkingDirectory;
    }

    public async Task<CommandResult> RunAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FileName))
            throw new ArgumentException("FileName cannot be null or empty.", nameof(request));

        var startInfo = new ProcessStartInfo(request.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? _defaultWorkingDirectory
            : request.WorkingDirectory;

        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;

        if (request.Arguments is not null)
            foreach (var argument in request.Arguments)
                startInfo.ArgumentList.Add(argument);

        if (request.Environment is not null)
            foreach (var pair in request.Environment)
                startInfo.Environment[pair.Key] = pair.Value;

        using var process = new Process();
        process.StartInfo = startInfo;
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process '{request.FileName}'.");

        var stopwatch = Stopwatch.StartNew();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is not null)
            linkedCts.CancelAfter(request.Timeout.Value);

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        string stdout;
        string stderr;
        var timedOut = false;

        try
        {
            var stdoutTask = ReadLinesAsync(process.StandardOutput, stdoutLines, linkedCts.Token);
            var stderrTask = ReadLinesAsync(process.StandardError, stderrLines, linkedCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                    && request.Timeout is not null)
            {
                timedOut = true;
                TryKillProcess(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(SuppressCancellationAsync(stdoutTask), SuppressCancellationAsync(stderrTask))
                    .ConfigureAwait(false);
            }

            stdout = string.Join(Environment.NewLine, stdoutLines);
            stderr = string.Join(Environment.NewLine, stderrLines);
            if (timedOut)
                stderr = AppendLine(
                    stderr,
                    $"Command timed out after {request.Timeout!.Value.TotalSeconds:0.###} seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
        finally
        {
            stopwatch.Stop();
        }

        if (request.MaxOutputChars is int maxOutputChars)
        {
            stdout = Truncate(stdout, maxOutputChars);
            stderr = Truncate(stderr, maxOutputChars);
        }

        var result = new CommandResult(
            process.ExitCode,
            stdout,
            stderr,
            stdoutLines.ToArray(),
            stderrLines.ToArray(),
            stopwatch.Elapsed)
        {
            FileName = request.FileName,
            Arguments = request.Arguments ?? [],
            WorkingDirectory = workingDirectory,
        };

        if (request.ThrowOnNonZeroExitCode && result.ExitCode != 0)
        {
            var message = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"Command '{request.FileName}' failed with exit code {result.ExitCode}."
                : $"Command '{request.FileName}' failed with exit code {result.ExitCode}: {result.StandardError.Trim()}";
            throw new InvalidOperationException(message);
        }

        return result;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(true);
        }
        catch
        {
            // Ignored.
        }
    }

    private static async Task SuppressCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Preserve output captured before the timeout.
        }
    }

    private static string AppendLine(string value, string line)
        => string.IsNullOrWhiteSpace(value)
            ? line
            : string.Concat(value, Environment.NewLine, line);

    private static async Task ReadLinesAsync(
        StreamReader reader,
        List<string> lines,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;

            lines.Add(line);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        return value.Substring(0, maxLength);
    }
}