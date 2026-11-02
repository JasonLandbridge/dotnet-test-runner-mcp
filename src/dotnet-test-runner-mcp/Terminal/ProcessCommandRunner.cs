using System.Diagnostics;
using System.Text;

using Microsoft.Extensions.Options;

namespace DotnetTestRunnerMcp.Terminal;

public sealed class ProcessCommandRunner : ICommandRunner
{
    private const int DefaultMaximumOutputChars = 64 * 1024;

    private readonly string? _defaultWorkingDirectory;

    public ProcessCommandRunner(IOptions<McpOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _defaultWorkingDirectory = options.Value.WorkingDirectory;
    }

    public async Task<CommandResult> RunAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default
    )
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
        var maximumOutputChars = Math.Max(
            request.MaxOutputChars ?? DefaultMaximumOutputChars,
            0
        );
        var stdoutCapture = new BoundedOutputCapture(maximumOutputChars);
        var stderrCapture = new BoundedOutputCapture(maximumOutputChars);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout is not null)
            linkedCts.CancelAfter(request.Timeout.Value);

        Task stdoutTask = Task.CompletedTask;
        Task stderrTask = Task.CompletedTask;
        var timedOut = false;
        try
        {
            stdoutTask = ReadStreamAsync(
                process.StandardOutput,
                stdoutCapture,
                linkedCts.Token
            );
            stderrTask = ReadStreamAsync(
                process.StandardError,
                stderrCapture,
                linkedCts.Token
            );

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested && request.Timeout is not null)
            {
                timedOut = true;
                TryKillProcess(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(
                        SuppressCancellationAsync(stdoutTask),
                        SuppressCancellationAsync(stderrTask)
                    )
                    .ConfigureAwait(false);
            }

            if (timedOut)
                stderrCapture.Append(
                    $"{Environment.NewLine}Command timed out after {request.Timeout!.Value.TotalSeconds:0.###} seconds."
                );
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(
                    SuppressCancellationAsync(stdoutTask),
                    SuppressCancellationAsync(stderrTask)
                )
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            stopwatch.Stop();
        }

        var stdout = stdoutCapture.GetText();
        var stderr = stderrCapture.GetText();
        var result = new CommandResult(
            process.ExitCode,
            stdout,
            stderr,
            BoundedOutputCapture.GetLines(stdout),
            BoundedOutputCapture.GetLines(stderr),
            stopwatch.Elapsed
        )
        {
            FileName = request.FileName,
            Arguments = request.Arguments ?? [],
            WorkingDirectory = workingDirectory,
            StandardOutputTruncated = stdoutCapture.Truncated,
            StandardErrorTruncated = stderrCapture.Truncated,
            StandardOutputOriginalCharacterCount = stdoutCapture.OriginalCharacterCount,
            StandardErrorOriginalCharacterCount = stderrCapture.OriginalCharacterCount,
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

    private static async Task ReadStreamAsync(
        StreamReader reader,
        BoundedOutputCapture capture,
        CancellationToken cancellationToken
    )
    {
        var buffer = new char[4_096];
        while (true)
        {
            var count = await reader
                .ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
                break;

            capture.Append(buffer.AsSpan(0, count));
        }
    }

    private sealed class BoundedOutputCapture
    {
        private const string TruncationMarker = "[output truncated]";
        private readonly int _maximumCharacters;
        private readonly int _headLimit;
        private readonly int _tailLimit;
        private readonly StringBuilder _head = new();
        private readonly StringBuilder _tail = new();
        private StringBuilder? _full = new();
        private long _originalCharacterCount;

        public BoundedOutputCapture(int maximumCharacters)
        {
            _maximumCharacters = maximumCharacters;
            var markerLength =
                TruncationMarker.Length + (Environment.NewLine.Length * 2);
            if (maximumCharacters <= markerLength)
            {
                _headLimit = maximumCharacters;
                _tailLimit = 0;
            }
            else
            {
                var contentLength = maximumCharacters - markerLength;
                _headLimit = (contentLength + 1) / 2;
                _tailLimit = contentLength - _headLimit;
            }
        }

        public bool Truncated { get; private set; }

        public int OriginalCharacterCount =>
            _originalCharacterCount >= int.MaxValue
                ? int.MaxValue
                : (int)_originalCharacterCount;

        public void Append(ReadOnlySpan<char> value)
        {
            if (value.IsEmpty)
                return;

            _originalCharacterCount = Math.Min(
                (long)int.MaxValue,
                _originalCharacterCount + value.Length
            );

            if (Truncated)
            {
                AppendRetained(value);
                return;
            }

            if (_maximumCharacters > 0 && _full!.Length + value.Length <= _maximumCharacters)
            {
                _full.Append(value);
                return;
            }

            Truncated = true;
            var existing = _full!.ToString();
            _full = null;
            AppendRetained(existing.AsSpan());
            AppendRetained(value);
        }

        public string GetText()
        {
            if (!Truncated)
                return _full?.ToString() ?? string.Empty;

            if (_tailLimit == 0)
                return _head.ToString();

            return string.Concat(
                _head,
                Environment.NewLine,
                TruncationMarker,
                Environment.NewLine,
                _tail
            );
        }

        public static string[] GetLines(string text)
        {
            if (text.Length == 0)
                return [];

            return text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        }

        private void AppendRetained(ReadOnlySpan<char> value)
        {
            var headRemaining = _headLimit - _head.Length;
            var headCount = Math.Min(headRemaining, value.Length);
            if (headCount > 0)
                _head.Append(value[..headCount]);

            if (_tailLimit == 0 || headCount == value.Length)
                return;

            AppendTail(value[headCount..]);
        }

        private void AppendTail(ReadOnlySpan<char> value)
        {
            if (value.Length >= _tailLimit)
            {
                _tail.Clear();
                _tail.Append(value[^_tailLimit..]);
                return;
            }

            var overflow = _tail.Length + value.Length - _tailLimit;
            if (overflow > 0)
                _tail.Remove(0, overflow);
            _tail.Append(value);
        }
    }
}
