using DotnetTestRunnerMcp.Models;
using DotnetTestRunnerMcp.Terminal;
using DotnetTestRunnerMcp.Tools;
using Xunit;

namespace DotnetTestRunnerMcp.Tests;

public sealed class TestCommandBuilderTests
{
    [Fact]
    public void BuildSingleTestRun_ForTUnit_UsesMicrosoftTestingPlatformTreeNodeFilter()
    {
        var arguments = TestCommandBuilder.BuildSingleTestRun(
            TestRunnerDialect.TUnit,
            "Reaparr.Application.UnitTests.AddOrUpdatePlexServerCommandUnitTests.ShouldAddServer",
            "tests/Reaparr.Application.UnitTests/Reaparr.Application.UnitTests.csproj"
        );

        string[] expected =
        [
            "run",
            "--project",
            "tests/Reaparr.Application.UnitTests/Reaparr.Application.UnitTests.csproj",
            "--",
            "--no-ansi",
            "--disable-logo",
            "--treenode-filter",
            "/*/*/*/ShouldAddServer",
        ];

        Assert.Equal(expected, arguments);
        Assert.DoesNotContain("--filter-method", arguments);
    }

    [Fact]
    public void BuildSingleTestRun_ForTUnit_MapsQualifiedMethodToNamespaceClassAndMethodTreeNodeFilter()
    {
        var arguments = TestCommandBuilder.BuildSingleTestRun(
            TestRunnerDialect.TUnit,
            "Reaparr.Application.UnitTests.AddOrUpdatePlexServerCommandUnitTests.ShouldAddServer",
            "tests/Reaparr.Application.UnitTests/Reaparr.Application.UnitTests.csproj"
        );

        Assert.Equal("--treenode-filter", arguments[^2]);
        Assert.Equal("/*/*/*/ShouldAddServer", arguments[^1]);
        Assert.DoesNotContain("--filter-method", arguments);
    }

    [Fact]
    public void BuildClassRun_ForTUnit_MapsQualifiedClassNameToMicrosoftTestingPlatformTreeNodeFilter()
    {
        var arguments = TestCommandBuilder.BuildClassRun(
            TestRunnerDialect.TUnit,
            "Reaparr.Application.UnitTests.AddOrUpdatePlexServerCommandUnitTests",
            "tests/Reaparr.Application.UnitTests/Reaparr.Application.UnitTests.csproj"
        );

        Assert.Equal("--treenode-filter", arguments[^2]);
        Assert.Equal("/*/*/AddOrUpdatePlexServerCommandUnitTests/*", arguments[^1]);
        Assert.DoesNotContain("--filter-class", arguments);
    }

    [Fact]
    public void BuildClassRun_ForTUnit_ClassNameProducesSafeTreeNodeFilter()
    {
        var arguments = TestCommandBuilder.BuildClassRun(
            TestRunnerDialect.TUnit,
            "CreatePlexAccountEndpointUnitTests",
            "tests/Reaparr.Application.UnitTests/Reaparr.Application.UnitTests.csproj"
        );

        Assert.Equal("--treenode-filter", arguments[^2]);
        Assert.Equal("/*/*/CreatePlexAccountEndpointUnitTests/*", arguments[^1]);
        Assert.DoesNotContain("**", arguments[^1]);
    }

    [Fact]
    public void BuildSingleTestRun_ForTUnit_TestNameProducesSafeTreeNodeFilter()
    {
        var arguments = TestCommandBuilder.BuildSingleTestRun(
            TestRunnerDialect.TUnit,
            "CreatePlexAccountAsync_ShouldFailedResult_WhenAccountUsernameExistenceCheckFailed",
            "tests/Reaparr.Application.UnitTests/Reaparr.Application.UnitTests.csproj"
        );

        Assert.Equal("--treenode-filter", arguments[^2]);
        Assert.Equal(
            "/*/*/*/CreatePlexAccountAsync_ShouldFailedResult_WhenAccountUsernameExistenceCheckFailed",
            arguments[^1]
        );
        Assert.DoesNotContain("**", arguments[^1]);
    }

    [Fact]
    public void BuildClassRun_ForTUnit_RejectsInvalidTreeNodeSegment()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            TestCommandBuilder.BuildClassRun(
                TestRunnerDialect.TUnit,
                "Invalid/ClassName",
                "tests/Reaparr.Application.UnitTests/Reaparr.Application.UnitTests.csproj"
            )
        );

        Assert.Contains("TUnit tree-node filter segment", exception.Message);
    }

    [Fact]
    public void BuildProjectRun_ForTUnit_UsesDotnetRunProjectWithRunnerArgumentsAfterSeparator()
    {
        var arguments = TestCommandBuilder.BuildProjectRun(
            TestRunnerDialect.TUnit,
            "tests/Reaparr.Application.UnitTests/Reaparr.Application.UnitTests.csproj"
        );

        string[] expected =
        [
            "run",
            "--project",
            "tests/Reaparr.Application.UnitTests/Reaparr.Application.UnitTests.csproj",
            "--",
            "--no-ansi",
            "--disable-logo",
        ];

        Assert.Equal(expected, arguments);
    }

    [Fact]
    public void BuildProjectRun_ForVSTest_UsesProjectAsPositionalArgument()
    {
        var arguments = TestCommandBuilder.BuildProjectRun(
            TestRunnerDialect.VSTest,
            "tests/dotnet-test-runner-mcp.Tests/dotnet-test-runner-mcp.Tests.csproj"
        );

        string[] expected = ["test", "tests/dotnet-test-runner-mcp.Tests/dotnet-test-runner-mcp.Tests.csproj"];

        Assert.Equal(expected, arguments);
        Assert.DoesNotContain("--project", arguments);
    }

    [Fact]
    public void BuildSingleTestRun_ForVSTest_UsesFullyQualifiedNameFilter()
    {
        var arguments = TestCommandBuilder.BuildSingleTestRun(
            TestRunnerDialect.VSTest,
            "DotnetTestRunnerMcp.Tests.TestCommandBuilderTests.BuildSingleTestRun_ForVSTest_UsesFullyQualifiedNameFilter",
            "tests/dotnet-test-runner-mcp.Tests/dotnet-test-runner-mcp.Tests.csproj"
        );

        string[] expected =
        [
            "test",
            "tests/dotnet-test-runner-mcp.Tests/dotnet-test-runner-mcp.Tests.csproj",
            "--filter",
            "FullyQualifiedName~DotnetTestRunnerMcp.Tests.TestCommandBuilderTests.BuildSingleTestRun_ForVSTest_UsesFullyQualifiedNameFilter",
        ];

        Assert.Equal(expected, arguments);
        Assert.DoesNotContain("--filter-method", arguments);
    }

    [Fact]
    public void BuildClassRun_ForVSTest_UsesFullyQualifiedNameFilter()
    {
        var arguments = TestCommandBuilder.BuildClassRun(
            TestRunnerDialect.VSTest,
            "DotnetTestRunnerMcp.Tests.TestCommandBuilderTests",
            "tests/dotnet-test-runner-mcp.Tests/dotnet-test-runner-mcp.Tests.csproj"
        );

        string[] expected =
        [
            "test",
            "tests/dotnet-test-runner-mcp.Tests/dotnet-test-runner-mcp.Tests.csproj",
            "--filter",
            "FullyQualifiedName~DotnetTestRunnerMcp.Tests.TestCommandBuilderTests",
        ];

        Assert.Equal(expected, arguments);
        Assert.DoesNotContain("--filter-class", arguments);
    }

    [Fact]
    public void CreateRunOptions_UsesConfiguredTimeoutAndPreservesRawOutputByDefault()
    {
        var options = new McpOptions { TestRunTimeoutSeconds = 180 };

        var runOptions = CtrfTestRun.CreateRunOptions(options);

        Assert.Equal(TimeSpan.FromSeconds(180), runOptions.Timeout);
        Assert.Null(runOptions.MaxOutputChars);
    }

    [Fact]
    public void BuildErrorInfo_WhenInvocationThrows_ReturnsDiagnosticDetails()
    {
        var commandResult = new CommandResult(-1, string.Empty, string.Empty, [], [], TimeSpan.Zero)
        {
            FileName = "dotnet",
            Arguments =
            [
                "run",
                "--project",
                "tests/Reaparr.Application.UnitTests/Reaparr.Application.UnitTests.csproj",
            ],
            WorkingDirectory = "/repo",
            ExceptionType = typeof(InvalidOperationException).FullName,
            ExceptionMessage = "boom",
            ExceptionStackTrace = "stack",
        };

        var error = CtrfTestRun.BuildErrorInfo(
            commandResult,
            null,
            ErrorKind.InvocationError,
            "run_single_test",
            "tests/Reaparr.Application.UnitTests/Reaparr.Application.UnitTests.csproj"
        );

        Assert.Equal("run_single_test", error.ToolName);
        Assert.Equal(
            "tests/Reaparr.Application.UnitTests/Reaparr.Application.UnitTests.csproj",
            error.ProjectPath
        );
        Assert.Equal("dotnet", error.Command);
        Assert.Equal(
            new[]
            {
                "run",
                "--project",
                "tests/Reaparr.Application.UnitTests/Reaparr.Application.UnitTests.csproj",
            },
            error.Arguments
        );
        Assert.Equal("/repo", error.WorkingDirectory);
        Assert.Equal(-1, error.ExitCode);
        Assert.Equal(typeof(InvalidOperationException).FullName, error.ExceptionType);
        Assert.Equal("boom", error.ExceptionMessage);
        Assert.Equal("stack", error.ExceptionStackTrace);
        Assert.Equal("boom", error.Reason);
    }

    [Fact]
    public void ClassifyErrorKind_WhenMsBuildRejectsCtrfSwitch_ReturnsInvocationError()
    {
        var commandResult = new CommandResult(
            1,
            "MSBUILD : error MSB1001: Unknown switch.\nSwitch: --report-ctrf",
            string.Empty,
            ["MSBUILD : error MSB1001: Unknown switch.", "Switch: --report-ctrf"],
            [],
            TimeSpan.Zero
        );

        var result = CtrfTestRun.ClassifyErrorKind(
            commandResult,
            reportFileFound: false,
            readErrorMessage: null
        );

        Assert.Equal(ErrorKind.InvocationError, result);
    }
}
