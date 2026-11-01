namespace DotnetTestRunnerMcp.Tools;

internal static class TestListParser
{
    internal static string[] ExtractTests(string[] lines)
    {
        if (lines.Length == 0)
            return Array.Empty<string>();

        var tests = new List<string>();
        var inListSection = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (IsListHeader(trimmed))
            {
                inListSection = true;
                continue;
            }

            if (!inListSection)
                continue;

            if (IsListBoundary(trimmed))
            {
                inListSection = false;
                continue;
            }

            if (IsNoiseLine(trimmed))
                continue;

            tests.Add(trimmed);
        }

        return tests.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static bool IsListHeader(string line) =>
        IsTUnitAssemblyHeader(line) || IsVSTestHeader(line);

    private static bool IsListBoundary(string line) =>
        IsTUnitFooter(line)
        || IsTestRunSummary(line)
        || line.StartsWith("Exit code:", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Test discovery completed", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Starting test execution", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Total tests", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Build ", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Test Run", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Results File:", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("A total of", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoiseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        if (line.StartsWith("Discovering tests from", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Test run for", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsListHeader(line) || IsTestRunSummary(line))
            return true;

        return false;
    }

    private static bool IsTUnitAssemblyHeader(string line)
    {
        const string prefix = "Discovered ";
        const string marker = " tests in assembly - ";

        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = line.AsSpan(prefix.Length);
        var markerIndex = remainder.IndexOf(marker.AsSpan(), StringComparison.OrdinalIgnoreCase);

        return markerIndex > 0
            && IsUnsignedInteger(remainder[..markerIndex])
            && !remainder[(markerIndex + marker.Length)..].Trim().IsEmpty;
    }

    private static bool IsTUnitFooter(string line)
    {
        const string prefix = "Discovered ";
        const string suffix = " tests.";

        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = line.AsSpan(prefix.Length);
        var countLength = remainder.Length - suffix.Length;

        return countLength > 0
            && remainder.EndsWith(suffix.AsSpan(), StringComparison.OrdinalIgnoreCase)
            && IsUnsignedInteger(remainder[..countLength]);
    }

    private static bool IsVSTestHeader(string line) =>
        HasOptionalColon(line, "The following Tests are available")
        || HasOptionalColon(line, "Tests are available")
        || HasOptionalColon(line, "Available Tests");

    private static bool HasOptionalColon(string line, string header)
    {
        if (!line.StartsWith(header, StringComparison.OrdinalIgnoreCase))
            return false;

        var remainder = line.AsSpan(header.Length).Trim();
        return remainder.IsEmpty || remainder.SequenceEqual(":".AsSpan());
    }

    private static bool IsTestRunSummary(string line) =>
        line.StartsWith("Passed!", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Failed!", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Skipped!", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnsignedInteger(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return false;

        foreach (var character in value)
        {
            if (character is < '0' or > '9')
                return false;
        }

        return true;
    }
}
