using DotnetTestRunnerMcp.Tools;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

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

    [Fact]
    public void ExtractTests_WhenTUnitDisplayNameContainsNewline_ReconstructsSingleTest()
    {
        string[] lines =
        [
            "Discovered 3 tests in assembly - /repo/tests/bin/Debug/net10.0/Tests.dll",
            "  ShouldValidateIpOrCidrCorrectly(",
            ", False)",
            "  ShouldSupportAllMappingTypes",
            "  ShouldHandleParentheses(value (inner))",
            "Discovered 3 tests.",
        ];

        var tests = TestListParser.ExtractTests(lines);

        string[] expected =
        [
            "ShouldHandleParentheses(value (inner))",
            "ShouldSupportAllMappingTypes",
            "ShouldValidateIpOrCidrCorrectly(\n, False)",
        ];

        Assert.Equal(expected, tests);
    }

    [Fact]
    public void ExtractTests_WhenTUnitDisplayNameContainsBlankContinuation_PreservesIt()
    {
        string[] lines =
        [
            "Discovered 2 tests in assembly - /repo/tests/bin/Debug/net10.0/Tests.dll",
            "  TestWithBlankContinuation(",
            string.Empty,
            ", False)",
            "  TestAfterContinuation",
            "Discovered 2 tests.",
        ];

        var tests = TestListParser.ExtractTests(lines);

        string[] expected =
        [
            "TestAfterContinuation",
            "TestWithBlankContinuation(\n\n, False)",
        ];

        Assert.Equal(expected, tests);
    }

    [Fact]
    public void ExtractTests_WhenTUnitListsPunctuationFreeAndSpacedNames_PreservesNames()
    {
        string[] lines =
        [
            "Build started 11/1/2026 12:00:00.",
            "Discovering tests from /repo/tests/bin/Debug/net10.0/Reaparr.FluentResultExtension.UnitTests.dll",
            "Discovered 6 tests in assembly - /repo/tests/bin/Debug/net10.0/Reaparr.FluentResultExtension.UnitTests.dll",
            "  NoPunctuation",
            "  PassedValidation",
            "  FailedAuthorization",
            "  SkippedCase",
            "  ParameterizedTest(\"first value\", \"second value\")",
            "  ParameterizedTest(\"first value\", \"third value\")",
            string.Empty,
            "Discovered 6 tests.",
            "  NotAListedTest",
            "Build succeeded.",
        ];

        var tests = TestListParser.ExtractTests(lines);

        string[] expected =
        [
            "FailedAuthorization",
            "NoPunctuation",
            "ParameterizedTest(\"first value\", \"second value\")",
            "ParameterizedTest(\"first value\", \"third value\")",
            "PassedValidation",
            "SkippedCase",
        ];

        Assert.Equal(expected, tests);
    }

    [Fact]
    public void ExtractTests_WhenVSTestListsAvailableTests_StopsAtRunSummary()
    {
        string[] lines =
        [
            "Build succeeded.",
            "Test run for /repo/tests/bin/Release/net10.0/Reaparr.Tests.dll (.NETCoreApp,Version=v10.0)",
            "The following Tests are available:",
            "  Reaparr.Tests.ParserTests.NoPunctuation",
            "  Reaparr.Tests.ParserTests.ParameterizedTest(\"first value\", \"second value\")",
            "Passed! - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 1 ms",
            "AfterSummaryIsNotATest",
        ];

        var tests = TestListParser.ExtractTests(lines);

        string[] expected =
        [
            "Reaparr.Tests.ParserTests.NoPunctuation",
            "Reaparr.Tests.ParserTests.ParameterizedTest(\"first value\", \"second value\")",
        ];

        Assert.Equal(expected, tests);
    }

    [Fact]
    public void ExtractTests_WhenDisplayNamesRepeat_PreservesEachCase()
    {
        string[] lines =
        [
            "Discovered 3 tests in assembly - /repo/tests/bin/Debug/net10.0/Tests.dll",
            "  SameDisplayName(value)",
            "  AnotherCase",
            "  SameDisplayName(value)",
            "Discovered 3 tests.",
        ];

        var tests = TestListParser.ExtractTests(lines);

        string[] expected =
        [
            "AnotherCase",
            "SameDisplayName(value)",
            "SameDisplayName(value)",
        ];

        Assert.Equal(expected, tests);
    }

    [Fact]
    public void ExtractTests_WhenTUnitListsMethodNames_ReturnsDiscoveredTestsFromMultipleAssemblies()
    {
        string[] lines =
        [
            "Discovered 1 tests in assembly - /repo/net8.0/Tests.dll",
            "  ShouldWork",
            "Discovered 1 tests.",
            "Discovered 1 tests in assembly - /repo/net10.0/Tests.dll",
            "  ShouldWork",
            "Discovered 1 tests.",
        ];

        Assert.Equal(new[] { "ShouldWork", "ShouldWork" }, TestListParser.ExtractTests(lines));
    }
}
