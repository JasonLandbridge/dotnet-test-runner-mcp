using Microsoft.Extensions.Configuration;

namespace DotnetTest.Mcp;

public sealed class McpOptions
{
    /// <summary>Base directory used by tools that run dotnet commands. Environment variable: WORKING_DIRECTORY.</summary>
    [ConfigurationKeyName("WORKING_DIRECTORY")]
    public string WorkingDirectory { get; set; } = Directory.GetCurrentDirectory();

    /// <summary>Relative folder name under <see cref="WorkingDirectory" /> that contains tests. Environment variable: TESTS_DIRECTORY_NAME.</summary>
    [ConfigurationKeyName("TESTS_DIRECTORY_NAME")]
    public string TestsDirectoryName { get; set; } = "tests";

    /// <summary>Disables CTRF report arguments when test hosts do not support them. Environment variable: DISABLE_CTRF.</summary>
    [ConfigurationKeyName("DISABLE_CTRF")]
    public bool DisableCtrf { get; set; }

    /// <summary>Timeout in seconds for dotnet test/run invocations. Environment variable: TEST_RUN_TIMEOUT_SECONDS.</summary>
    [ConfigurationKeyName("TEST_RUN_TIMEOUT_SECONDS")]
    public int TestRunTimeoutSeconds { get; set; } = 180;

    /// <summary>Full path to the tests directory, derived from <see cref="WorkingDirectory" /> and <see cref="TestsDirectoryName" />.</summary>
    public string TestsDirectoryPath => Path.Combine(WorkingDirectory, TestsDirectoryName);
}