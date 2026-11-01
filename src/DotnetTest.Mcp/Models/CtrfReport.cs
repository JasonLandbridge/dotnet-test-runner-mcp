using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetTest.Mcp.Models;

public sealed record CtrfReport
{
    [JsonPropertyName("reportFormat")]
    public string ReportFormat { get; init; } = string.Empty;

    [JsonPropertyName("specVersion")]
    public string SpecVersion { get; init; } = string.Empty;

    [JsonPropertyName("reportId")]
    public string? ReportId { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    [JsonPropertyName("generatedBy")]
    public string? GeneratedBy { get; init; }

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }

    [JsonPropertyName("results")]
    public CtrfResults Results { get; init; } = new();

    [JsonPropertyName("insights")]
    public CtrfInsights? Insights { get; init; }

    [JsonPropertyName("baseline")]
    public CtrfBaseline? Baseline { get; init; }
}

public sealed record CtrfResults
{
    [JsonPropertyName("tool")]
    public CtrfTool Tool { get; init; } = new();

    [JsonPropertyName("summary")]
    public CtrfSummary Summary { get; init; } = new();

    [JsonPropertyName("tests")]
    public List<CtrfTest> Tests { get; init; } = [];

    [JsonPropertyName("environment")]
    public CtrfEnvironment? Environment { get; init; }

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}

public sealed record CtrfTool
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}

public sealed record CtrfSummary
{
    [JsonPropertyName("tests")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Tests { get; init; }

    [JsonPropertyName("passed")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Passed { get; init; }

    [JsonPropertyName("failed")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Failed { get; init; }

    [JsonPropertyName("skipped")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Skipped { get; init; }

    [JsonPropertyName("pending")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Pending { get; init; }

    [JsonPropertyName("other")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Other { get; init; }

    [JsonPropertyName("flaky")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? Flaky { get; init; }

    [JsonPropertyName("suites")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? Suites { get; init; }

    [JsonPropertyName("start")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Start { get; init; }

    [JsonPropertyName("stop")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Stop { get; init; }

    [JsonPropertyName("duration")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? Duration { get; init; }

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}

public sealed record CtrfTest
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("duration")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long Duration { get; init; }

    [JsonPropertyName("start")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? Start { get; init; }

    [JsonPropertyName("stop")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? Stop { get; init; }

    [JsonPropertyName("suite")]
    public string Suite { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("trace")]
    public string? Trace { get; init; }

    [JsonPropertyName("snippet")]
    public string? Snippet { get; init; }

    [JsonPropertyName("ai")]
    public string? Ai { get; init; }

    [JsonPropertyName("line")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? Line { get; init; }

    [JsonPropertyName("rawStatus")]
    public string? RawStatus { get; init; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; init; } = [];

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("filePath")]
    public string? FilePath { get; init; }

    [JsonPropertyName("retries")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? Retries { get; init; }

    [JsonPropertyName("retryAttempts")]
    public List<CtrfRetryAttempt> RetryAttempts { get; init; } = [];

    [JsonPropertyName("flaky")]
    public bool? Flaky { get; init; }

    [JsonPropertyName("stdout")]
    public List<string> Stdout { get; init; } = [];

    [JsonPropertyName("stderr")]
    public List<string> Stderr { get; init; } = [];

    [JsonPropertyName("threadId")]
    public string? ThreadId { get; init; }

    [JsonPropertyName("browser")]
    public string? Browser { get; init; }

    [JsonPropertyName("device")]
    public string? Device { get; init; }

    [JsonPropertyName("screenshot")]
    public string? Screenshot { get; init; }

    [JsonPropertyName("attachments")]
    public List<CtrfAttachment> Attachments { get; init; } = [];

    [JsonPropertyName("parameters")]
    public JsonElement? Parameters { get; init; }

    [JsonPropertyName("steps")]
    public List<CtrfStep> Steps { get; init; } = [];

    [JsonPropertyName("insights")]
    public CtrfTestInsights? Insights { get; init; }

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}

public sealed record CtrfRetryAttempt
{
    [JsonPropertyName("attempt")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Attempt { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("duration")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? Duration { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("trace")]
    public string? Trace { get; init; }

    [JsonPropertyName("line")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? Line { get; init; }

    [JsonPropertyName("snippet")]
    public string? Snippet { get; init; }

    [JsonPropertyName("stdout")]
    public List<string> Stdout { get; init; } = [];

    [JsonPropertyName("stderr")]
    public List<string> Stderr { get; init; } = [];

    [JsonPropertyName("start")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? Start { get; init; }

    [JsonPropertyName("stop")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? Stop { get; init; }

    [JsonPropertyName("attachments")]
    public List<CtrfAttachment> Attachments { get; init; } = [];

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}

public sealed record CtrfAttachment
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}

public sealed record CtrfStep
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}

public sealed record CtrfEnvironment
{
    [JsonPropertyName("reportName")]
    public string? ReportName { get; init; }

    [JsonPropertyName("appName")]
    public string? AppName { get; init; }

    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; init; }

    [JsonPropertyName("buildId")]
    public string? BuildId { get; init; }

    [JsonPropertyName("buildName")]
    public string? BuildName { get; init; }

    [JsonPropertyName("buildNumber")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? BuildNumber { get; init; }

    [JsonPropertyName("buildUrl")]
    public string? BuildUrl { get; init; }

    [JsonPropertyName("repositoryName")]
    public string? RepositoryName { get; init; }

    [JsonPropertyName("repositoryUrl")]
    public string? RepositoryUrl { get; init; }

    [JsonPropertyName("commit")]
    public string? Commit { get; init; }

    [JsonPropertyName("branchName")]
    public string? BranchName { get; init; }

    [JsonPropertyName("osPlatform")]
    public string? OsPlatform { get; init; }

    [JsonPropertyName("osRelease")]
    public string? OsRelease { get; init; }

    [JsonPropertyName("osVersion")]
    public string? OsVersion { get; init; }

    [JsonPropertyName("testEnvironment")]
    public string? TestEnvironment { get; init; }

    [JsonPropertyName("healthy")]
    public bool? Healthy { get; init; }

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}

public sealed record CtrfInsights
{
    [JsonPropertyName("passRate")]
    public CtrfMetricDelta? PassRate { get; init; }

    [JsonPropertyName("failRate")]
    public CtrfMetricDelta? FailRate { get; init; }

    [JsonPropertyName("flakyRate")]
    public CtrfMetricDelta? FlakyRate { get; init; }

    [JsonPropertyName("averageRunDuration")]
    public CtrfMetricDelta? AverageRunDuration { get; init; }

    [JsonPropertyName("p95RunDuration")]
    public CtrfMetricDelta? P95RunDuration { get; init; }

    [JsonPropertyName("averageTestDuration")]
    public CtrfMetricDelta? AverageTestDuration { get; init; }

    [JsonPropertyName("runsAnalyzed")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? RunsAnalyzed { get; init; }

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}

public sealed record CtrfTestInsights
{
    [JsonPropertyName("passRate")]
    public CtrfMetricDelta? PassRate { get; init; }

    [JsonPropertyName("failRate")]
    public CtrfMetricDelta? FailRate { get; init; }

    [JsonPropertyName("flakyRate")]
    public CtrfMetricDelta? FlakyRate { get; init; }

    [JsonPropertyName("averageTestDuration")]
    public CtrfMetricDelta? AverageTestDuration { get; init; }

    [JsonPropertyName("p95TestDuration")]
    public CtrfMetricDelta? P95TestDuration { get; init; }

    [JsonPropertyName("executedInRuns")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? ExecutedInRuns { get; init; }

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}

public sealed record CtrfMetricDelta
{
    [JsonPropertyName("current")]
    public double? Current { get; init; }

    [JsonPropertyName("baseline")]
    public double? Baseline { get; init; }

    [JsonPropertyName("change")]
    public double? Change { get; init; }
}

public sealed record CtrfBaseline
{
    [JsonPropertyName("reportId")]
    public string? ReportId { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("buildNumber")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int? BuildNumber { get; init; }

    [JsonPropertyName("buildName")]
    public string? BuildName { get; init; }

    [JsonPropertyName("buildUrl")]
    public string? BuildUrl { get; init; }

    [JsonPropertyName("commit")]
    public string? Commit { get; init; }

    [JsonPropertyName("extra")]
    public JsonElement? Extra { get; init; }
}