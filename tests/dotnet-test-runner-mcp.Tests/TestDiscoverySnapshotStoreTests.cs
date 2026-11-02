using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Tools;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class TestDiscoverySnapshotStoreTests
{
    [Fact]
    public void Create_ClonesCatalogSoLaterSourceMutationDoesNotChangePages()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new TestDiscoverySnapshotStore(clock, maximumSnapshots: 4, maximumCharacters: 10_000);
        var discovery = CreateDiscovery("Original.Tests.Run");

        var id = Assert.IsType<string>(store.Create(discovery));
        discovery.Tests[0] = discovery.Tests[0] with { DisplayName = "Changed.Tests.Run" };

        var result = store.Get(id);
        Assert.Equal(TestDiscoverySnapshotStore.SnapshotLookupStatus.Found, result.Status);
        Assert.Equal("Original.Tests.Run", Assert.Single(result.Discovery!.Tests).DisplayName);
    }

    [Fact]
    public void Get_ReturnsExpiredForExpiredSnapshotId()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new TestDiscoverySnapshotStore(
            clock,
            maximumSnapshots: 4,
            maximumCharacters: 10_000,
            snapshotLifetime: TimeSpan.FromMinutes(1)
        );
        var id = Assert.IsType<string>(store.Create(CreateDiscovery("Expired.Tests.Run")));

        clock.Advance(TimeSpan.FromMinutes(2));

        var result = store.Get(id);
        Assert.Equal(TestDiscoverySnapshotStore.SnapshotLookupStatus.Expired, result.Status);
        Assert.Null(result.Discovery);
    }

    [Fact]
    public void Create_EvictsOldestEntriesWithinCountAndCharacterBounds()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = new TestDiscoverySnapshotStore(
            clock,
            maximumSnapshots: 2,
            maximumCharacters: 10_000
        );
        var first = Assert.IsType<string>(store.Create(CreateDiscovery("First.Tests.Run")));
        var second = Assert.IsType<string>(store.Create(CreateDiscovery("Second.Tests.Run")));
        var third = Assert.IsType<string>(store.Create(CreateDiscovery("Third.Tests.Run")));

        Assert.Equal(2, store.Count);
        Assert.Equal(TestDiscoverySnapshotStore.SnapshotLookupStatus.NotFound, store.Get(first).Status);
        Assert.Equal(TestDiscoverySnapshotStore.SnapshotLookupStatus.Found, store.Get(second).Status);
        Assert.Equal(TestDiscoverySnapshotStore.SnapshotLookupStatus.Found, store.Get(third).Status);
        Assert.InRange(store.TotalCharacters, 1, store.MaximumCharacters);
    }

    [Fact]
    public void Create_RejectsSnapshotThatExceedsCharacterBudget()
    {
        var store = new TestDiscoverySnapshotStore(
            maximumSnapshots: 2,
            maximumCharacters: 8
        );

        var id = store.Create(CreateDiscovery("TooLarge.Tests.Run"));

        Assert.Null(id);
        Assert.Equal(0, store.Count);
    }

    private static TestCatalog.CatalogDiscovery CreateDiscovery(string displayName) =>
        new(
            [
                new TestCatalog.DiscoveredTest(
                    "tests/Example/Example.csproj",
                    displayName,
                    displayName,
                    TestRunnerDialect.VSTest,
                    false
                ),
            ],
            [
                new ProjectDiscoveryResult(
                    "tests/Example/Example.csproj",
                    TestOutcome.Passed,
                    "Discovered 1 tests.",
                    0,
                    1,
                    null,
                    null
                ),
            ]
        );

    private sealed class ManualTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
