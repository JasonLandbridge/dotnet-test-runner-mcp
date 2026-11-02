using System.Text;

using DotnetTestRunnerMcp.Models;

namespace DotnetTestRunnerMcp.Tools;

/// <summary>
/// Retains bounded, in-memory artifacts for completed test runs during the server session.
/// </summary>
public sealed class TestRunStore
{
    private const int MaximumFailureRecords = 200;
    private const int OutputBudgetDivisor = 4;

    private const int DefaultMaximumRuns = 32;
    private const int DefaultMaximumCharacters = 1_000_000;
    private static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _maximumRuns;
    private readonly TimeSpan _maximumAge;
    private readonly int _maximumCharacters;
    private readonly Dictionary<string, RunRecord> _runs = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = [];
    private int _retainedCharacters;

    public TestRunStore()
        : this(TimeProvider.System)
    {
    }

    public TestRunStore(
        TimeProvider timeProvider,
        int maximumRuns = DefaultMaximumRuns,
        TimeSpan? maximumAge = null,
        int maximumCharacters = DefaultMaximumCharacters
    )
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (maximumRuns <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRuns));
        if (maximumCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

        var age = maximumAge ?? DefaultMaximumAge;
        if (age <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAge));

        _timeProvider = timeProvider;
        _maximumRuns = maximumRuns;
        _maximumAge = age;
        _maximumCharacters = maximumCharacters;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                PurgeExpired(_timeProvider.GetUtcNow());
                return _runs.Count;
            }
        }
    }

    public int RetainedCharacters
    {
        get
        {
            lock (_gate)
            {
                PurgeExpired(_timeProvider.GetUtcNow());
                return _retainedCharacters;
            }
        }
    }

    /// <summary>Adds or replaces a run and returns its retained identifier.</summary>
    public string Store(RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var now = _timeProvider.GetUtcNow();
        var runId = string.IsNullOrWhiteSpace(run.RunId)
            ? Guid.NewGuid().ToString("N")
            : run.RunId.Trim();
        var createdAt = run.CreatedAt == default ? now : run.CreatedAt;
        var boundedRun = Bound(run with
        {
            RunId = runId,
            CreatedAt = createdAt,
        });
        var retainedCharacters = CountCharacters(boundedRun);

        lock (_gate)
        {
            PurgeExpired(now);
            RemoveExisting(runId);
            _runs.Add(runId, boundedRun);
            _order.AddLast(runId);
            _retainedCharacters += retainedCharacters;
            EvictToBounds();
        }

        return runId;
    }

    /// <summary>
    /// Starts an incremental bounded accumulator for a run whose plans execute one at a time.
    /// </summary>
    public RunAccumulator BeginAccumulator(string runId, DateTimeOffset createdAt = default) =>
        new(
            string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId.Trim(),
            createdAt == default ? _timeProvider.GetUtcNow() : createdAt,
            _maximumCharacters
        );

    public bool TryGet(string? runId, out RunRecord run)
    {
        run = default!;
        if (string.IsNullOrWhiteSpace(runId))
            return false;

        lock (_gate)
        {
            PurgeExpired(_timeProvider.GetUtcNow());
            if (!_runs.TryGetValue(runId.Trim(), out var stored))
                return false;

            run = Clone(stored);
            return true;
        }
    }

    public bool Remove(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return false;

        lock (_gate)
        {
            PurgeExpired(_timeProvider.GetUtcNow());
            return RemoveExisting(runId.Trim());
        }
    }

    private RunRecord Bound(RunRecord run)
    {
        var sourceFailures = run.Failures ?? Array.Empty<FailureRecord>();
        var originalFailureCount = Math.Max(
            run.OriginalFailureCount,
            sourceFailures.Length
        );
        var output = run.Output ?? new CapturedOutput();
        var outputBudget = GetOutputBudget(output, sourceFailures.Length > 0);
        var remaining = _maximumCharacters - outputBudget;
        var failureCandidates = new List<FailureRecord>(
            Math.Min(sourceFailures.Length, MaximumFailureRecords)
        );

        foreach (var sourceFailure in sourceFailures.Take(MaximumFailureRecords))
        {
            var failureId = MakeFailureId(
                sourceFailure.FailureId,
                failureCandidates.Count + 1
            );
            var testName = sourceFailure.TestName ?? string.Empty;
            var identityLength = failureId.Length + testName.Length;
            if (identityLength > Math.Max(remaining, 0))
                break;

            remaining -= identityLength;
            failureCandidates.Add(
                sourceFailure with
                {
                    FailureId = failureId,
                    TestName = testName,
                }
            );
        }

        var failures = new FailureRecord[failureCandidates.Count];
        for (var index = 0; index < failures.Length; index++)
            failures[index] = BoundPayload(failureCandidates[index], ref remaining);

        var outputRemaining = outputBudget;
        var boundedOutput = BoundOutput(
            output,
            ref outputRemaining,
            out var standardOutputTruncated,
            out var standardOutputOriginalCharacterCount,
            out var standardErrorTruncated,
            out var standardErrorOriginalCharacterCount
        );
        var failuresTruncated = run.FailuresTruncated
            || sourceFailures.Length > failures.Length
            || originalFailureCount > failures.Length;

        return run with
        {
            Failures = failures,
            OriginalFailureCount = originalFailureCount,
            FailuresTruncated = failuresTruncated,
            Output = new CapturedOutput(
                boundedOutput.StandardOutput,
                boundedOutput.StandardError,
                standardOutputTruncated,
                standardErrorTruncated,
                standardOutputOriginalCharacterCount,
                standardErrorOriginalCharacterCount
            ),
        };
    }

    private static string MakeFailureId(string failureId, int ordinal) =>
        string.IsNullOrWhiteSpace(failureId) ? $"failure-{ordinal}" : failureId;

    private int GetOutputBudget(CapturedOutput output, bool hasFailures)
    {
        var outputLength = (long)(output.StandardOutput?.Length ?? 0)
            + (output.StandardError?.Length ?? 0);
        if (outputLength <= 0)
            return 0;

        var maximum = hasFailures
            ? Math.Max(_maximumCharacters / OutputBudgetDivisor, 1)
            : _maximumCharacters;
        return (int)Math.Min(outputLength, maximum);
    }

    private static CapturedOutput BoundOutput(
        CapturedOutput output,
        ref int remaining,
        out bool standardOutputTruncated,
        out int? standardOutputOriginalCharacterCount,
        out bool standardErrorTruncated,
        out int? standardErrorOriginalCharacterCount
    )
    {
        var values = new[] { output.StandardOutput, output.StandardError };
        var alreadyTruncated = new[]
        {
            output.StandardOutputTruncated,
            output.StandardErrorTruncated,
        };
        var originalCounts = new[]
        {
            output.StandardOutputOriginalCharacterCount,
            output.StandardErrorOriginalCharacterCount,
        };
        var bounded = BoundFields(
            values,
            alreadyTruncated,
            originalCounts,
            ref remaining,
            out var truncated,
            out var counts
        );
        standardOutputTruncated = truncated[0];
        standardOutputOriginalCharacterCount = counts[0];
        standardErrorTruncated = truncated[1];
        standardErrorOriginalCharacterCount = counts[1];
        return new CapturedOutput(bounded[0], bounded[1]);
    }

    private static FailureRecord BoundPayload(FailureRecord failure, ref int remaining)
    {
        var projectPath = Bound(
            failure.ProjectPath,
            false,
            null,
            ref remaining,
            out _,
            out _
        );
        var values = new[]
        {
            failure.Message,
            failure.Trace,
            failure.StandardOutput,
            failure.StandardError,
        };
        var alreadyTruncated = new[]
        {
            failure.MessageTruncated,
            failure.TraceTruncated,
            failure.StandardOutputTruncated,
            failure.StandardErrorTruncated,
        };
        var originalCounts = new[]
        {
            failure.MessageOriginalCharacterCount,
            failure.TraceOriginalCharacterCount,
            failure.StandardOutputOriginalCharacterCount,
            failure.StandardErrorOriginalCharacterCount,
        };
        var bounded = BoundFields(
            values,
            alreadyTruncated,
            originalCounts,
            ref remaining,
            out var truncated,
            out var counts
        );

        return failure with
        {
            ProjectPath = projectPath,
            Message = bounded[0],
            Trace = bounded[1],
            StandardOutput = bounded[2],
            StandardError = bounded[3],
            MessageTruncated = truncated[0],
            TraceTruncated = truncated[1],
            StandardOutputTruncated = truncated[2],
            StandardErrorTruncated = truncated[3],
            MessageOriginalCharacterCount = counts[0],
            TraceOriginalCharacterCount = counts[1],
            StandardOutputOriginalCharacterCount = counts[2],
            StandardErrorOriginalCharacterCount = counts[3],
        };
    }

    private static string?[] BoundFields(
        string?[] values,
        bool[] alreadyTruncated,
        int?[] originalCounts,
        ref int remaining,
        out bool[] truncated,
        out int?[] counts
    )
    {
        truncated = new bool[values.Length];
        counts = new int?[values.Length];
        var lengths = new int[values.Length];
        var present = 0;
        long totalLength = 0;
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (value is null)
            {
                truncated[index] = alreadyTruncated[index] || originalCounts[index] is > 0;
                counts[index] = originalCounts[index];
                continue;
            }

            lengths[index] = value.Length;
            if (value.Length > 0)
                present++;
            totalLength += value.Length;
            counts[index] = Math.Max(value.Length, originalCounts[index] ?? value.Length);
        }

        var target = (int)Math.Min(
            totalLength,
            Math.Max(remaining, 0)
        );
        var allocations = new int[values.Length];
        if (present > 0 && target > 0)
        {
            var minimum = target / present;
            var remainder = target;
            for (var index = 0; index < values.Length; index++)
            {
                if (lengths[index] == 0)
                    continue;

                allocations[index] = Math.Min(lengths[index], minimum);
                remainder -= allocations[index];
            }

            for (var index = 0; index < values.Length && remainder > 0; index++)
            {
                var extra = Math.Min(lengths[index] - allocations[index], remainder);
                allocations[index] += extra;
                remainder -= extra;
            }
        }

        remaining -= allocations.Sum();
        var bounded = new string?[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            if (value is null)
                continue;

            bounded[index] = value[..allocations[index]];
            truncated[index] =
                alreadyTruncated[index]
                || allocations[index] < value.Length
                || (counts[index] ?? allocations[index]) > allocations[index];
        }

        return bounded;
    }

    private static string? Bound(
        string? value,
        bool alreadyTruncated,
        int? originalCharacterCount,
        ref int remaining,
        out bool truncated,
        out int? originalCount
    )
    {
        var bounded = BoundFields(
            [value],
            [alreadyTruncated],
            [originalCharacterCount],
            ref remaining,
            out var truncatedValues,
            out var counts
        );
        truncated = truncatedValues[0];
        originalCount = counts[0];
        return bounded[0];
    }

    /// <summary>
    /// Incrementally bounds failure and process-output artifacts while plans are executing.
    /// </summary>
    public sealed class RunAccumulator
    {
        private readonly string _runId;
        private readonly DateTimeOffset _createdAt;
        private readonly List<FailureRecord> _failures = [];
        private readonly BoundedTextAccumulator _standardOutput;
        private readonly BoundedTextAccumulator _standardError;
        private int _remainingFailureCharacters;
        private int _originalFailureCount;
        private bool _failuresTruncated;

        internal RunAccumulator(string runId, DateTimeOffset createdAt, int maximumCharacters)
        {
            _runId = runId;
            _createdAt = createdAt;
            var outputBudget = Math.Max(maximumCharacters / OutputBudgetDivisor, 1);
            var standardOutputBudget = (outputBudget + 1) / 2;
            _standardOutput = new BoundedTextAccumulator(standardOutputBudget);
            _standardError = new BoundedTextAccumulator(outputBudget - standardOutputBudget);
            _remainingFailureCharacters = maximumCharacters - outputBudget;
        }

        public int OriginalFailureCount => _originalFailureCount;

        public bool FailuresTruncated => _failuresTruncated;

        /// <summary>
        /// Adds one failure and returns whether its bounded artifact is retained.
        /// </summary>
        public bool AddFailure(FailureRecord failure)
        {
            ArgumentNullException.ThrowIfNull(failure);
            _originalFailureCount++;
            if (_failures.Count >= MaximumFailureRecords)
            {
                _failuresTruncated = true;
                return false;
            }

            var failureId = MakeFailureId(failure.FailureId, _originalFailureCount);
            var testName = failure.TestName ?? string.Empty;
            var identityLength = failureId.Length + testName.Length;
            if (identityLength > Math.Max(_remainingFailureCharacters, 0))
            {
                _failuresTruncated = true;
                return false;
            }

            var recordBudget = _remainingFailureCharacters
                / (MaximumFailureRecords - _failures.Count);
            _remainingFailureCharacters -= identityLength;
            var payloadBudget = Math.Max(recordBudget - identityLength, 0);
            var payloadRemaining = payloadBudget;
            var bounded = BoundPayload(
                failure with
                {
                    FailureId = failureId,
                    TestName = testName,
                },
                ref payloadRemaining
            );
            _remainingFailureCharacters -= payloadBudget - payloadRemaining;
            _failures.Add(bounded);
            return true;
        }

        /// <summary>Adds one plan's already bounded process output without retaining its raw copy.</summary>
        public void AddOutput(CapturedOutput output)
        {
            ArgumentNullException.ThrowIfNull(output);
            _standardOutput.Append(
                output.StandardOutput,
                output.StandardOutputTruncated,
                output.StandardOutputOriginalCharacterCount
            );
            _standardError.Append(
                output.StandardError,
                output.StandardErrorTruncated,
                output.StandardErrorOriginalCharacterCount
            );
        }

        public RunRecord Complete(TestOutcome outcome) =>
            new(
                _runId,
                _createdAt,
                _failures.ToArray(),
                new CapturedOutput(
                    _standardOutput.HasValue ? _standardOutput.GetText() : null,
                    _standardError.HasValue ? _standardError.GetText() : null,
                    _standardOutput.Truncated,
                    _standardError.Truncated,
                    _standardOutput.OriginalCharacterCount,
                    _standardError.OriginalCharacterCount
                )
            )
            {
                Outcome = outcome,
                OriginalFailureCount = _originalFailureCount,
                FailuresTruncated = _failuresTruncated,
            };
    }

    private sealed class BoundedTextAccumulator
    {
        private const string TruncationMarker = "[output truncated]";
        private readonly int _maximumCharacters;
        private readonly int _headLimit;
        private readonly int _tailLimit;
        private readonly StringBuilder _head = new();
        private readonly StringBuilder _tail = new();
        private StringBuilder? _full = new();
        private long _originalCharacterCount;
        private bool _overflowed;
        private bool _hasAppendedValue;

        public BoundedTextAccumulator(int maximumCharacters)
        {
            _maximumCharacters = Math.Max(maximumCharacters, 0);
            var markerLength =
                TruncationMarker.Length + (Environment.NewLine.Length * 2);
            if (_maximumCharacters <= markerLength)
            {
                _headLimit = _maximumCharacters;
                _tailLimit = 0;
            }
            else
            {
                var contentLength = _maximumCharacters - markerLength;
                _headLimit = (contentLength + 1) / 2;
                _tailLimit = contentLength - _headLimit;
            }
        }

        public bool HasValue { get; private set; }

        public bool Truncated { get; private set; }

        public int? OriginalCharacterCount =>
            HasValue
                ? _originalCharacterCount >= int.MaxValue
                    ? int.MaxValue
                    : (int)_originalCharacterCount
                : null;

        public void Append(string? value, bool alreadyTruncated, int? originalCount)
        {
            if (value is null)
            {
                Truncated |= alreadyTruncated;
                if (originalCount is > 0)
                {
                    HasValue = true;
                    _originalCharacterCount = Math.Max(
                        _originalCharacterCount,
                        originalCount.Value
                    );
                }
                return;
            }
            if (value.Length == 0 && !alreadyTruncated && originalCount is not > 0)
                return;

            HasValue = true;
            var sourceLength = Math.Max(value.Length, originalCount ?? value.Length);
            _originalCharacterCount = Math.Min(
                (long)int.MaxValue,
                _originalCharacterCount + sourceLength + (_hasAppendedValue ? 1 : 0)
            );
            Truncated |= alreadyTruncated || sourceLength > value.Length;

            if (_hasAppendedValue)
                AppendRaw(Environment.NewLine.AsSpan());
            AppendRaw(value.AsSpan());
            if (value.Length > 0)
                _hasAppendedValue = true;
        }

        public string GetText()
        {
            if (!_overflowed)
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

        private void AppendRaw(ReadOnlySpan<char> value)
        {
            if (value.IsEmpty)
                return;

            if (_overflowed)
            {
                AppendRetained(value);
                return;
            }

            if (_maximumCharacters > 0 && _full!.Length + value.Length <= _maximumCharacters)
            {
                _full.Append(value);
                return;
            }

            _overflowed = true;
            Truncated = true;
            var existing = _full!.ToString();
            _full = null;
            AppendRetained(existing.AsSpan());
            AppendRetained(value);
        }

        private void AppendRetained(ReadOnlySpan<char> value)
        {
            var headRemaining = _headLimit - _head.Length;
            var headCount = Math.Min(headRemaining, value.Length);
            if (headCount > 0)
                _head.Append(value[..headCount]);

            if (_tailLimit == 0 || headCount == value.Length)
                return;

            var tail = value[headCount..];
            if (tail.Length >= _tailLimit)
            {
                _tail.Clear();
                _tail.Append(tail[^_tailLimit..]);
                return;
            }

            var overflow = _tail.Length + tail.Length - _tailLimit;
            if (overflow > 0)
                _tail.Remove(0, overflow);
            _tail.Append(tail);
        }
    }

    private static int CountCharacters(RunRecord run)
    {
        var count = 0;
        foreach (var failure in run.Failures)
        {
            count += failure.FailureId.Length;
            count += failure.TestName.Length;
            count += failure.ProjectPath?.Length ?? 0;
            count += failure.Message?.Length ?? 0;
            count += failure.Trace?.Length ?? 0;
            count += failure.StandardOutput?.Length ?? 0;
            count += failure.StandardError?.Length ?? 0;
        }

        count += run.Output.StandardOutput?.Length ?? 0;
        count += run.Output.StandardError?.Length ?? 0;
        return count;
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        var node = _order.First;
        while (node is not null)
        {
            var next = node.Next;
            if (_runs.TryGetValue(node.Value, out var run) && now - run.CreatedAt >= _maximumAge)
                RemoveExisting(node.Value);
            node = next;
        }
    }

    private void EvictToBounds()
    {
        while (_runs.Count > _maximumRuns || _retainedCharacters > _maximumCharacters)
        {
            if (_order.First is not { } first)
                break;
            RemoveExisting(first.Value);
        }
    }

    private bool RemoveExisting(string runId)
    {
        if (!_runs.Remove(runId, out var run))
            return false;

        _retainedCharacters -= CountCharacters(run);
        var node = _order.Find(runId);
        if (node is not null)
            _order.Remove(node);
        return true;
    }

    private static RunRecord Clone(RunRecord run) =>
        run with
        {
            Failures = run.Failures.ToArray(),
        };

    public sealed record RunRecord(
        string RunId,
        DateTimeOffset CreatedAt,
        FailureRecord[] Failures,
        CapturedOutput Output
    )
    {
        public TestOutcome Outcome { get; init; } = TestOutcome.Error;
        public int OriginalFailureCount { get; init; }

        public bool FailuresTruncated { get; init; }

        public RunRecord(string runId, DateTimeOffset createdAt)
            : this(runId, createdAt, [], new CapturedOutput()) { }

        public RunRecord(string runId)
            : this(runId, default, [], new CapturedOutput()) { }
    }

    public sealed record FailureRecord(
        string FailureId,
        string TestName,
        string? ProjectPath,
        string? Message,
        string? Trace,
        string? StandardOutput,
        string? StandardError,
        bool MessageTruncated = false,
        bool TraceTruncated = false,
        bool StandardOutputTruncated = false,
        bool StandardErrorTruncated = false,
        int? MessageOriginalCharacterCount = null,
        int? TraceOriginalCharacterCount = null,
        int? StandardOutputOriginalCharacterCount = null,
        int? StandardErrorOriginalCharacterCount = null
    );

    public sealed record CapturedOutput(
        string? StandardOutput = null,
        string? StandardError = null,
        bool StandardOutputTruncated = false,
        bool StandardErrorTruncated = false,
        int? StandardOutputOriginalCharacterCount = null,
        int? StandardErrorOriginalCharacterCount = null
    );
}
