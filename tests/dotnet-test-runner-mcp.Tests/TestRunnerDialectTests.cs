using DotnetTestRunnerMcp.Tools;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class TestRunnerDialectTests
{
    [Fact]
    public void BuildMethodUnion_ForVSTest_UsesExactOrSegments()
    {
        var built = TestCommandBuilder.TryBuildMethodUnion(
            TestRunnerDialect.VSTest,
            ["Tests.First.Run", "Tests.Second.Run"],
            out var filter,
            out var reason
        );

        Assert.True(built, reason);
        Assert.Equal(
            "FullyQualifiedName=Tests.First.Run|FullyQualifiedName=Tests.Second.Run",
            filter
        );
    }
    [Fact]
    public void BuildMethodUnion_ForTUnit_UsesTreeNodeOrSegments()
    {
        var built = TestCommandBuilder.TryBuildMethodUnion(
            TestRunnerDialect.TUnit,
            ["Tests.First.FirstRun", "Tests.Second.SecondRun"],
            out var filter,
            out var reason
        );

        Assert.True(built, reason);
        Assert.Equal("/*/*/*/((FirstRun)|(SecondRun))&(*)", filter);
    }

    [Fact]
    public void BuildProjectRun_ForGenericMtp_UsesDotnetRunProjectPrefix()
    {
        Assert.Equal(
            new[] { "run", "--project", "tests/Mtp/Mtp.csproj", "--" },
            TestCommandBuilder.BuildProjectRun(
                TestRunnerDialect.MTP,
                "tests/Mtp/Mtp.csproj"
            )
        );
    }

    [Theory]
    [InlineData((int)TestRunnerDialect.TUnit, "Tests.Run|Other")]
    [InlineData((int)TestRunnerDialect.TUnit, "Tests.Run*")]
    [InlineData((int)TestRunnerDialect.VSTest, "Tests.Run|Other")]
    [InlineData((int)TestRunnerDialect.VSTest, "Tests.Run*")]
    public void BuildMethodUnion_RejectsRawFilterSyntax(
        int dialect,
        string selector
    )
    {
        var built = TestCommandBuilder.TryBuildMethodUnion(
            (TestRunnerDialect)dialect,
            [selector],
            out _,
            out var reason
        );

        Assert.False(built);
        Assert.Contains("syntax", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildMethodUnion_RejectsUnknownDialect()
    {
        var built = TestCommandBuilder.TryBuildMethodUnion(
            TestRunnerDialect.Unknown,
            ["Tests.Run"],
            out _,
            out var reason
        );

        Assert.False(built);
        Assert.Contains("runner dialect is unknown", reason, StringComparison.OrdinalIgnoreCase);
    }
}
