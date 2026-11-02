using DotnetTestRunnerMcp.Models;

namespace DotnetTestRunnerMcp.Tools;

/// <summary>
/// Retains bounded, fixed discovery snapshots for cursor-based paging.
/// </summary>
public sealed class TestDiscoverySnapshotStore
{
    public const int DefaultMaximumSnapshots = 128;
    public const int DefaultMaximumCharacters = 4_000_000;
    public static readonly TimeSpan DefaultSnapshotLifetime = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _maximumSnapshots;
    private readonly int _maximumCharacters;
    private readonly TimeSpan _snapshotLifetime;
    private readonly Dictionary<string, SnapshotEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _insertionOrder = new();
    private long _totalCharacters;

    public TestDiscoverySnapshotStore(
        TimeProvider? timeProvider = null,
        int maximumSnapshots = DefaultMaximumSnapshots,
        int maximumCharacters = DefaultMaximumCharacters,
        TimeSpan? snapshotLifetime = null
    )
    {
        if (maximumSnapshots <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumSnapshots));
        if (maximumCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

        var lifetime = snapshotLifetime ?? DefaultSnapshotLifetime;
        if (lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(snapshotLifetime));

        _timeProvider = timeProvider ?? TimeProvider.System;
        _maximumSnapshots = maximumSnapshots;
        _maximumCharacters = maximumCharacters;
        _snapshotLifetime = lifetime;
    }

    public int MaximumSnapshots => _maximumSnapshots;

    public int MaximumCharacters => _maximumCharacters;

    public TimeSpan SnapshotLifetime => _snapshotLifetime;

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                RemoveExpired(_timeProvider.GetUtcNow());
                return _entries.Count;
            }
        }
    }

    internal long TotalCharacters
    {
        get
        {
            lock (_gate)
            {
                RemoveExpired(_timeProvider.GetUtcNow());
                return _totalCharacters;
            }
        }
    }

    internal string? Create(TestCatalog.CatalogDiscovery discovery)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        var snapshot = CloneDiscovery(discovery);
        var characterCount = EstimateCharacters(snapshot);
        if (characterCount > _maximumCharacters)
            return null;

        var id = Guid.NewGuid().ToString("N");
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            RemoveExpired(now);
            while (
                _entries.Count >= _maximumSnapshots
                || _totalCharacters + characterCount > _maximumCharacters
            )
            {
                if (_insertionOrder.First is null)
                    return null;
                Remove(_insertionOrder.First.Value);
            }

            var node = _insertionOrder.AddLast(id);
            _entries.Add(
                id,
                new SnapshotEntry(snapshot, characterCount, now + _snapshotLifetime, node)
            );
            _totalCharacters += characterCount;
            return id;
        }
    }

    internal SnapshotLookup Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return new SnapshotLookup(SnapshotLookupStatus.Invalid, null);

        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_entries.TryGetValue(id, out var requested))
            {
                if (now >= requested.ExpiresAt)
                {
                    Remove(id);
                    return new SnapshotLookup(SnapshotLookupStatus.Expired, null);
                }

                RemoveExpired(now);
                return new SnapshotLookup(SnapshotLookupStatus.Found, requested.Discovery);
            }

            RemoveExpired(now);
            return new SnapshotLookup(SnapshotLookupStatus.NotFound, null);
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        var node = _insertionOrder.First;
        while (node is not null)
        {
            var next = node.Next;
            if (now >= _entries[node.Value].ExpiresAt)
                Remove(node.Value);
            node = next;
        }
    }

    private void Remove(string id)
    {
        if (!_entries.Remove(id, out var entry))
            return;
        _insertionOrder.Remove(entry.Node);
        _totalCharacters -= entry.CharacterCount;
    }

    private static TestCatalog.CatalogDiscovery CloneDiscovery(
        TestCatalog.CatalogDiscovery discovery
    ) => new(
        discovery.Tests
            .Select(test => test with { })
            .ToArray(),
        discovery.ProjectResults
            .Select(CloneProjectResult)
            .ToArray()
    );

    private static ProjectDiscoveryResult CloneProjectResult(ProjectDiscoveryResult result) =>
        result with { Error = CloneError(result.Error) };

    private static ErrorInfo? CloneError(ErrorInfo? error)
    {
        if (error is null)
            return null;

        return error with
        {
            Arguments = error.Arguments.ToArray(),
            Stdout = error.Stdout is null ? null : error.Stdout with { },
            Stderr = error.Stderr is null ? null : error.Stderr with { },
        };
    }

    private static int EstimateCharacters(TestCatalog.CatalogDiscovery discovery)
    {
        long total = 0;
        foreach (var test in discovery.Tests)
        {
            total += test.ProjectPath.Length;
            total += test.DisplayName.Length;
            total += test.Selector.Length;
        }

        foreach (var result in discovery.ProjectResults)
        {
            total += result.ProjectPath.Length;
            total += result.Message.Length;
            total += result.Warning?.Length ?? 0;
            total += CharacterCount(result.Error);
        }

        return total > int.MaxValue ? int.MaxValue : (int)Math.Max(1, total);
    }

    private static long CharacterCount(ErrorInfo? error)
    {
        if (error is null)
            return 0;

        long total = error.Reason.Length;
        total += error.ToolName?.Length ?? 0;
        total += error.ProjectPath?.Length ?? 0;
        total += error.Command?.Length ?? 0;
        total += error.WorkingDirectory?.Length ?? 0;
        total += error.ExceptionType?.Length ?? 0;
        total += error.ExceptionMessage?.Length ?? 0;
        total += error.ExceptionStackTrace?.Length ?? 0;
        total += error.Arguments.Sum(argument => (long)argument.Length);
        total += error.Stdout?.Text?.Length ?? 0;
        total += error.Stderr?.Text?.Length ?? 0;
        return total;
    }

    private sealed record SnapshotEntry(
        TestCatalog.CatalogDiscovery Discovery,
        int CharacterCount,
        DateTimeOffset ExpiresAt,
        LinkedListNode<string> Node
    );

    internal readonly record struct SnapshotLookup(
        SnapshotLookupStatus Status,
        TestCatalog.CatalogDiscovery? Discovery
    );

    internal enum SnapshotLookupStatus
    {
        Found,
        Invalid,
        NotFound,
        Expired,
    }
}
