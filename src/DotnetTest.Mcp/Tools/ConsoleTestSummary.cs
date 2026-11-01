using System.Globalization;
using DotnetTest.Mcp.Models;
using DotnetTest.Mcp.Terminal;

namespace DotnetTest.Mcp.Tools;

internal sealed record ConsoleTestSummary(
    int TestCount,
    int Passed,
    int Failed,
    int Skipped,
    int Pending,
    int Other,
    int? DurationMilliseconds);

internal static class ConsoleTestSummaryParser
{
    internal static ConsoleTestSummary Parse(CommandResult commandResult)
    {
        var output = BuildOutput(commandResult);
        var summaryStart = FindSummaryStart(output);

        var searchStart = summaryStart >= 0 ? summaryStart : 0;
        var passed = FindLabeledCount(output, searchStart, "Succeeded")
            ?? FindLabeledCount(output, searchStart, "Passed")
            ?? 0;
        var failed = FindLabeledCount(output, searchStart, "Failed") ?? 0;
        var skipped = FindLabeledCount(output, searchStart, "Skipped") ?? 0;
        var total = FindLabeledCount(output, searchStart, "Total") ?? 0;
        var duration = FindDurationMilliseconds(output, searchStart);

        return new ConsoleTestSummary(
            total > 0 ? total : passed + failed + skipped,
            passed,
            failed,
            skipped,
            0,
            0,
            duration);
    }

    internal static bool HasSummary(CommandResult commandResult)
        => FindSummaryStart(BuildOutput(commandResult)) >= 0;

    internal static TestOutcome GetOutcome(CommandResult commandResult, ConsoleTestSummary summary)
    {
        if (commandResult.ExitCode != 0 && summary.Failed == 0)
            return TestOutcome.Error;

        if (summary.TestCount == 0)
            return TestOutcome.NotFound;

        return summary.Failed > 0
            ? TestOutcome.Failed
            : summary.Passed > 0
                ? TestOutcome.Passed
                : TestOutcome.Skipped;
    }

    internal static string GetMessage(TestOutcome outcome)
        => outcome switch
        {
            TestOutcome.Passed => "All tests passed.",
            TestOutcome.Failed => "Test run completed with failures.",
            TestOutcome.Skipped => "Tests completed without failures.",
            TestOutcome.NotFound => "No tests matched the filter.",
            _ => "Test execution failed.",
        };

    private static string BuildOutput(CommandResult commandResult)
        => string.Join(
            Environment.NewLine,
            commandResult.StandardOutput,
            commandResult.StandardError);

    private static int FindSummaryStart(string output)
    {
        var summaryStart = output.LastIndexOf("Test run summary:", StringComparison.OrdinalIgnoreCase);
        if (summaryStart >= 0)
            return summaryStart;

        var passedMarker = output.LastIndexOf("Passed!", StringComparison.OrdinalIgnoreCase);
        var failedMarker = output.LastIndexOf("Failed!", StringComparison.OrdinalIgnoreCase);
        var statusMarker = Math.Max(passedMarker, failedMarker);
        return statusMarker >= 0 ? output.LastIndexOf('\n', statusMarker) + 1 : -1;
    }

    private static int? FindLabeledCount(string output, int searchStart, string label)
    {
        var searchIndex = searchStart;
        while (searchIndex < output.Length)
        {
            var labelIndex = output.IndexOf(
                label,
                searchIndex,
                output.Length - searchIndex,
                StringComparison.OrdinalIgnoreCase);
            if (labelIndex < 0)
                return null;

            var labelEnd = labelIndex + label.Length;
            var hasValidStart = labelIndex == 0 || !IsIdentifierCharacter(output[labelIndex - 1]);
            var hasValidEnd = labelEnd == output.Length || !IsIdentifierCharacter(output[labelEnd]);
            if (hasValidStart && hasValidEnd)
            {
                var valueIndex = SkipHorizontalWhitespace(output, labelEnd);
                if (label.Equals("Total", StringComparison.OrdinalIgnoreCase))
                {
                    var testsIndex = valueIndex;
                    if (testsIndex + 5 <= output.Length
                        && output.AsSpan(testsIndex, 5).Equals("tests", StringComparison.OrdinalIgnoreCase)
                        && (testsIndex + 5 == output.Length
                            || !IsIdentifierCharacter(output[testsIndex + 5])))
                    {
                        valueIndex = testsIndex + 5;
                    }
                }

                valueIndex = SkipHorizontalWhitespace(output, valueIndex);
                if (valueIndex < output.Length && (output[valueIndex] == ':' || output[valueIndex] == '='))
                {
                    valueIndex = SkipHorizontalWhitespace(output, valueIndex + 1);
                    var valueStart = valueIndex;
                    while (valueIndex < output.Length && char.IsDigit(output[valueIndex]))
                        valueIndex++;

                    if (valueStart < valueIndex
                        && (valueIndex == output.Length
                            || (!IsIdentifierCharacter(output[valueIndex]) && output[valueIndex] != '.'))
                        && int.TryParse(
                            output.AsSpan(valueStart, valueIndex - valueStart),
                            NumberStyles.None,
                            CultureInfo.InvariantCulture,
                            out var value))
                    {
                        return value;
                    }
                }
            }

            searchIndex = labelEnd;
        }

        return null;
    }

    private static int? FindDurationMilliseconds(string output, int searchStart)
    {
        var durationIndex = output.IndexOf(
            "Duration",
            searchStart,
            output.Length - searchStart,
            StringComparison.OrdinalIgnoreCase);
        while (durationIndex >= 0)
        {
            var durationEnd = durationIndex + "Duration".Length;
            var hasValidStart = durationIndex == 0 || !IsIdentifierCharacter(output[durationIndex - 1]);
            var hasValidEnd = durationEnd == output.Length || !IsIdentifierCharacter(output[durationEnd]);
            if (hasValidStart && hasValidEnd)
            {
                var valueIndex = SkipHorizontalWhitespace(output, durationEnd);
                if (valueIndex < output.Length && (output[valueIndex] == ':' || output[valueIndex] == '='))
                {
                    return ParseDurationMilliseconds(output, SkipHorizontalWhitespace(output, valueIndex + 1));
                }
            }

            durationIndex = durationEnd < output.Length
                ? output.IndexOf(
                    "Duration",
                    durationEnd,
                    output.Length - durationEnd,
                    StringComparison.OrdinalIgnoreCase)
                : -1;
        }

        return null;
    }

    private static int? ParseDurationMilliseconds(string output, int valueIndex)
    {
        decimal totalMilliseconds = 0;
        var componentCount = 0;

        while (true)
        {
            valueIndex = SkipHorizontalWhitespace(output, valueIndex);
            if (valueIndex >= output.Length
                || output[valueIndex] == '\r'
                || output[valueIndex] == '\n'
                || output[valueIndex] == '-')
            {
                break;
            }

            var numberStart = valueIndex;
            while (valueIndex < output.Length && char.IsDigit(output[valueIndex]))
                valueIndex++;

            if (valueIndex < output.Length && output[valueIndex] == '.')
            {
                valueIndex++;
                var fractionStart = valueIndex;
                while (valueIndex < output.Length && char.IsDigit(output[valueIndex]))
                    valueIndex++;

                if (fractionStart == valueIndex)
                    return null;
            }

            if (numberStart == valueIndex
                || !decimal.TryParse(
                    output.AsSpan(numberStart, valueIndex - numberStart),
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                return null;
            }

            valueIndex = SkipHorizontalWhitespace(output, valueIndex);
            decimal multiplier;
            if (valueIndex + 2 <= output.Length
                && output.AsSpan(valueIndex, 2).Equals("ms", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1;
                valueIndex += 2;
            }
            else if (valueIndex < output.Length
                && (output[valueIndex] == 's' || output[valueIndex] == 'S'))
            {
                multiplier = 1000;
                valueIndex++;
            }
            else if (valueIndex < output.Length
                && (output[valueIndex] == 'm' || output[valueIndex] == 'M'))
            {
                multiplier = 60_000;
                valueIndex++;
            }
            else if (valueIndex < output.Length
                && (output[valueIndex] == 'h' || output[valueIndex] == 'H'))
            {
                multiplier = 3_600_000;
                valueIndex++;
            }
            else if (valueIndex < output.Length
                && (output[valueIndex] == 'd' || output[valueIndex] == 'D'))
            {
                multiplier = 86_400_000;
                valueIndex++;
            }
            else
            {
                return null;
            }

            if (value > (int.MaxValue - totalMilliseconds) / multiplier)
                return null;

            totalMilliseconds += value * multiplier;
            if (totalMilliseconds > int.MaxValue)
                return null;

            componentCount++;
            if (valueIndex < output.Length
                && output[valueIndex] != ' '
                && output[valueIndex] != '\t'
                && output[valueIndex] != '\r'
                && output[valueIndex] != '\n'
                && output[valueIndex] != '-')
            {
                return null;
            }
        }

        if (componentCount == 0)
            return null;

        var roundedMilliseconds = decimal.Round(totalMilliseconds, 0, MidpointRounding.AwayFromZero);
        return roundedMilliseconds <= int.MaxValue ? (int)roundedMilliseconds : null;
    }

    private static int SkipHorizontalWhitespace(string output, int index)
    {
        while (index < output.Length && (output[index] == ' ' || output[index] == '\t'))
            index++;

        return index;
    }

    private static bool IsIdentifierCharacter(char character)
        => char.IsLetterOrDigit(character) || character == '_';
}
