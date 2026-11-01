using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class ConsoleTestSummaryParserTests
{
    [Fact]
    public void Parse_WhenVstestSummaryHasFrameworkDigits_UsesExplicitLabelsAndSeconds()
    {
        var commandResult = new CommandResult(
            0,
            """
            Test run for /repo/tests/bin/Debug/net10.0/Reaparr.Tests.dll (.NETCoreApp,Version=v10.0)
            A total of 26 test files matched the specified pattern.

            Passed! - /repo/tests/bin/Debug/net10.0/Reaparr.Tests.dll (net10.0|x64)
            Passed: 26, Failed: 0, Skipped: 0, Total: 26, Duration: 8 s
            """,
            string.Empty,
            [],
            [],
            TimeSpan.Zero
        );

        var summary = ConsoleTestSummaryParser.Parse(commandResult);

        Assert.Equal(26, summary.TestCount);
        Assert.Equal(26, summary.Passed);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(8000, summary.DurationMilliseconds);
        Assert.Equal(
            TestOutcome.Passed,
            ConsoleTestSummaryParser.GetOutcome(commandResult, summary)
        );
    }

    [Fact]
    public void Parse_WhenMtpSummaryUsesSucceeded_ParsesCompoundDuration()
    {
        var commandResult = new CommandResult(
            0,
            """
            Test run summary: Passed! - bin/Debug/net10.0/Reaparr.Tests.dll (net10.0|x64)
              total: 5
              failed: 0
              succeeded: 5
              skipped: 0
              duration: 1m 02s 003ms
            """,
            string.Empty,
            [],
            [],
            TimeSpan.Zero
        );

        var summary = ConsoleTestSummaryParser.Parse(commandResult);

        Assert.Equal(5, summary.TestCount);
        Assert.Equal(5, summary.Passed);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Skipped);
        Assert.Equal(62_003, summary.DurationMilliseconds);
        Assert.Equal(
            TestOutcome.Passed,
            ConsoleTestSummaryParser.GetOutcome(commandResult, summary)
        );
    }

    [Fact]
    public void Parse_WhenMtpSummaryHasFailuresAndSkips_ReportsFailedOutcome()
    {
        var commandResult = new CommandResult(
            1,
            """
            Test run summary: Failed! - bin/Debug/net10.0/Reaparr.Tests.dll (net10.0|x64)
              total: 4
              failed: 1
              succeeded: 1
              skipped: 2
              duration: 1.5s
            """,
            string.Empty,
            [],
            [],
            TimeSpan.Zero
        );

        var summary = ConsoleTestSummaryParser.Parse(commandResult);

        Assert.Equal(4, summary.TestCount);
        Assert.Equal(1, summary.Passed);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(1500, summary.DurationMilliseconds);
        Assert.Equal(
            TestOutcome.Failed,
            ConsoleTestSummaryParser.GetOutcome(commandResult, summary)
        );
    }

    [Fact]
    public void Parse_WhenAllTestsAreSkipped_ReportsSkippedOutcome()
    {
        var commandResult = new CommandResult(
            0,
            """
            Test run summary: Passed! - bin/Debug/net10.0/Reaparr.Tests.dll (net10.0|x64)
              total: 2
              failed: 0
              succeeded: 0
              skipped: 2
              duration: 250ms
            """,
            string.Empty,
            [],
            [],
            TimeSpan.Zero
        );

        var summary = ConsoleTestSummaryParser.Parse(commandResult);

        Assert.Equal(2, summary.TestCount);
        Assert.Equal(0, summary.Passed);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(2, summary.Skipped);
        Assert.Equal(250, summary.DurationMilliseconds);
        Assert.Equal(
            TestOutcome.Skipped,
            ConsoleTestSummaryParser.GetOutcome(commandResult, summary)
        );
    }

    [Fact]
    public void Parse_WhenCountsAreAbsent_DoesNotUseHeaderDigitsOrDiagnosticProse()
    {
        var commandResult = new CommandResult(
            0,
            """
            Test run summary: Passed! - bin/Debug/net10.0/Reaparr.Tests.dll (net10.0|x64)
            Total test count was unavailable; diagnostic path was /repo/net10.0.
            No explicit result summary was emitted.
            """,
            string.Empty,
            [],
            [],
            TimeSpan.Zero
        );

        var summary = ConsoleTestSummaryParser.Parse(commandResult);

        Assert.Equal(0, summary.TestCount);
        Assert.Equal(0, summary.Passed);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(0, summary.Skipped);
        Assert.Null(summary.DurationMilliseconds);
        Assert.Equal(
            TestOutcome.NotFound,
            ConsoleTestSummaryParser.GetOutcome(commandResult, summary)
        );
    }

    [Theory]
    [InlineData("125ms", 125)]
    [InlineData("1.5s", 1500)]
    [InlineData("2m", 120000)]
    [InlineData("1m 02s", 62000)]
    public void Parse_WhenMtpDurationUsesSupportedUnits_ConvertsToMilliseconds(
        string duration,
        int expectedMilliseconds
    )
    {
        var commandResult = new CommandResult(
            0,
            $"""
            Test run summary: Passed! - bin/Debug/net10.0/Reaparr.Tests.dll (net10.0|x64)
              total: 1
              failed: 0
              succeeded: 1
              skipped: 0
              duration: {duration}
            """,
            string.Empty,
            [],
            [],
            TimeSpan.Zero
        );

        var summary = ConsoleTestSummaryParser.Parse(commandResult);

        Assert.Equal(expectedMilliseconds, summary.DurationMilliseconds);
    }

    [Theory]
    [InlineData("8 ticks")]
    [InlineData("8")]
    [InlineData("79228162514264337593543950335s")]
    public void Parse_WhenDurationIsUnsupportedOrOutOfRange_ReturnsNoDuration(string duration)
    {
        var commandResult = new CommandResult(
            0,
            $"""
            Test run summary: Passed! - bin/Debug/net10.0/Reaparr.Tests.dll (net10.0|x64)
              total: 1
              failed: 0
              succeeded: 1
              skipped: 0
              duration: {duration}
            """,
            string.Empty,
            [],
            [],
            TimeSpan.Zero
        );

        var summary = ConsoleTestSummaryParser.Parse(commandResult);

        Assert.Null(summary.DurationMilliseconds);
    }
}
