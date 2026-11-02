using System.Text.Json.Serialization;

namespace DotnetTestRunnerMcp.Models;

public record TestSelection(
    [property: JsonPropertyName("projectPath")] string ProjectPath,
    [property: JsonPropertyName("testName")] string? TestName = null
);
