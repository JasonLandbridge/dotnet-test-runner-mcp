using DotnetTest.Mcp.Terminal;
using DotnetTest.Mcp.Tools;
using Xunit;

namespace DotnetTest.Mcp.Tests;

public sealed class TestProjectDiscoveryTests
{
    [Fact]
    public async Task ListAsync_FiltersConfiguredDirectoryByNormalizedBoundary()
    {
        var runner = new FakeCommandRunner(
            "Project(s)\n"
            + "----------\n"
            + "tests/Good/Good.csproj\n"
            + "tests-tools/NotTests/NotTests.csproj\n"
            + "tests\\Windows/Windows.csproj\n"
            + "Tests/Case/Case.csproj");

        var projects = await TestProjectDiscovery.ListAsync(
            runner,
            new McpOptions { TestsDirectoryName = "tests" },
            null,
            CancellationToken.None);

        var expected = OperatingSystem.IsWindows()
            ? new[]
            {
                "tests/Good/Good.csproj",
                "tests\\Windows/Windows.csproj",
                "Tests/Case/Case.csproj",
            }
            : new[]
            {
                "tests/Good/Good.csproj",
                "tests\\Windows/Windows.csproj",
            };
        Assert.Equal(expected, projects);
    }

    private sealed class FakeCommandRunner(string output) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            var lines = output.Split('\n', StringSplitOptions.None);
            return Task.FromResult(
                new CommandResult(0, output, string.Empty, lines, [], TimeSpan.Zero));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
