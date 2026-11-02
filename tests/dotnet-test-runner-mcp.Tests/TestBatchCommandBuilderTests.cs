using DotnetTestRunnerMcp.Tools;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class TestBatchCommandBuilderTests
{
    [Fact]
    public void TryBuild_VSTest_UsesOneExactUnionFilter()
    {
        var built = TestBatchCommandBuilder.TryBuild(
            TestRunnerDialect.VSTest,
            "/repo/tests.csproj",
            ["Ns.Tests.First.Run", "Ns.Tests.Second.Run"],
            out var arguments,
            out var reason
        );

        Assert.True(built, reason);
        Assert.Equal("test", arguments[0]);
        Assert.Contains("--filter", arguments);
        Assert.Contains(
            "FullyQualifiedName=Ns.Tests.First.Run|FullyQualifiedName=Ns.Tests.Second.Run",
            arguments
        );
    }

    [Fact]
    public void TryBuild_TUnit_UsesOneTreeNodeUnionFilter()
    {
        var built = TestBatchCommandBuilder.TryBuild(
            TestRunnerDialect.TUnit,
            "/repo/tests.csproj",
            ["Tests.First.RunOne", "Tests.Second.RunTwo"],
            out var arguments,
            out var reason
        );

        Assert.True(built, reason);
        Assert.Equal("run", arguments[0]);
        Assert.Contains("--treenode-filter", arguments);
        Assert.Contains("/*/*/*/((RunOne)|(RunTwo))&(*)", arguments);
    }

    [Fact]
    public void TryBuild_MtpWholeProject_UsesDotnetRunProjectInvocation()
    {
        var built = TestBatchCommandBuilder.TryBuild(
            TestRunnerDialect.MTP,
            "/repo/tests.csproj",
            [],
            out var arguments,
            out var reason
        );

        Assert.True(built, reason);
        Assert.Equal(new[] { "run", "--project", "/repo/tests.csproj", "--" }, arguments);
    }

    [Fact]
    public void TryBuild_RejectsUnsafeSelectorBeforeComposing()
    {
        var built = TestBatchCommandBuilder.TryBuild(
            TestRunnerDialect.VSTest,
            "/repo/tests.csproj",
            ["Ns.Tests.First.Run|FullyQualifiedName=Ns.Tests.Other.Run"],
            out var arguments,
            out var reason
        );

        Assert.False(built);
        Assert.Empty(arguments);
        Assert.Contains("filter syntax", reason, StringComparison.OrdinalIgnoreCase);
    }
}
