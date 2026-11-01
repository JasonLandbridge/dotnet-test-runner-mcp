namespace DotnetTest.Mcp.Tools;

internal static class TestListParser
{
    internal static string[] ExtractTests(string[] lines)
    {
        if (lines.Length == 0)
            return Array.Empty<string>();

        var startIndex = FindListStartIndex(lines);
        var candidates = startIndex >= 0 ? lines.Skip(startIndex + 1) : lines.AsEnumerable();

        var tests = new List<string>();
        foreach (var line in candidates)
        {
            var trimmed = line.Trim();
            if (IsNoiseLine(trimmed))
                continue;

            var candidate = ExtractCandidate(trimmed);
            if (!LooksLikeTestName(candidate))
                continue;

            tests.Add(candidate);
        }

        return tests.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static int FindListStartIndex(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains("Tests are available", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Available Tests", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static bool IsNoiseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        if (line.StartsWith("Discovering tests from", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Discovered ", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Exit code:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Test discovery completed", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("The following Tests are available", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Test run for", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Starting test execution", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Total tests", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Passed", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Failed", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Build ", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Test Run", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Results File:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("A total of", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string ExtractCandidate(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        var firstToken = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        return firstToken ?? string.Empty;
    }

    private static bool LooksLikeTestName(string line) => line.Contains('.', StringComparison.Ordinal)
        || line.Contains('_', StringComparison.Ordinal)
        || line.Contains('(', StringComparison.Ordinal);
}