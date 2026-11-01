using DotnetTestRunnerMcp.Tools;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class TestRunnerDialectTests
{
    [Fact]
    public void Detect_WhenMicrosoftTestingPlatformIsOnlyPackage_ReturnsUnknown()
    {
        var projectPath = CreateProject("Microsoft.Testing.Platform");
        try
        {
            var dialect = TestRunnerDialectDetector.Detect(projectPath, new McpOptions());

            Assert.Equal(TestRunnerDialect.Unknown, dialect);
        }
        finally
        {
            File.Delete(projectPath);
        }
    }

    [Fact]
    public void Detect_WhenTUnitPackageIsPresent_ReturnsTUnit()
    {
        var projectPath = CreateProject("TUnit");
        try
        {
            var dialect = TestRunnerDialectDetector.Detect(projectPath, new McpOptions());

            Assert.Equal(TestRunnerDialect.TUnit, dialect);
        }
        finally
        {
            File.Delete(projectPath);
        }
    }

    private static string CreateProject(string packageName)
    {
        var projectPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csproj");
        File.WriteAllText(
            projectPath,
            $"""
            <Project>
              <ItemGroup>
                <PackageReference Include="{packageName}" />
              </ItemGroup>
            </Project>
            """
        );
        return projectPath;
    }
}
