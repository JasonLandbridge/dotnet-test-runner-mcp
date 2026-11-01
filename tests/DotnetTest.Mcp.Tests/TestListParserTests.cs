using DotnetTest.Mcp.Tools;
using Xunit;

namespace DotnetTest.Mcp.Tests;

public sealed class TestListParserTests
{
    [Fact]
    public void ExtractTests_WhenTUnitListsMethodNames_ReturnsDiscoveredTests()
    {
        string[] lines =
        [
            "Discovering tests from /repo/tests/bin/Debug/net10.0/Reaparr.Data.UnitTests.dll (net10.0|x64)",
            string.Empty,
            "Discovered 3 tests in assembly - /repo/tests/bin/Debug/net10.0/Reaparr.Data.UnitTests.dll (net10.0|x64)",
            "  ShouldReturnFalse_WhenOptionsIsNull",
            "  ShouldReturnTrue_WhenFilterContainsCondition",
            "  ShouldReturnError_WhenUnsupportedDownloadTaskTypeProvided(Movie)",
            string.Empty,
            "Discovered 3 tests.",
        ];

        var tests = TestListParser.ExtractTests(lines);

        string[] expected =
        [
            "ShouldReturnError_WhenUnsupportedDownloadTaskTypeProvided(Movie)",
            "ShouldReturnFalse_WhenOptionsIsNull",
            "ShouldReturnTrue_WhenFilterContainsCondition",
        ];

        Assert.Equal(expected, tests);
    }
}