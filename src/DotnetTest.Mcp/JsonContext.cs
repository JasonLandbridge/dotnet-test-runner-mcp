using System.Text.Json.Serialization;
using DotnetTest.Mcp.Models;
using DotnetTest.Mcp.Tools;

namespace DotnetTest.Mcp;

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(
    typeof(GetWorkingDirectoryTool.Result),
    TypeInfoPropertyName = "GetWorkingDirectoryToolResult")]
[JsonSerializable(
    typeof(ListTestProjectsTool.Result),
    TypeInfoPropertyName = "ListTestProjectsToolResult")]
[JsonSerializable(
    typeof(ListTestsSummaryTool.Result),
    TypeInfoPropertyName = "ListTestsSummaryToolResult")]
[JsonSerializable(
    typeof(RunSingleTestTool.Result),
    TypeInfoPropertyName = "RunSingleTestToolResult")]
[JsonSerializable(typeof(RunAllTestsTool.Result), TypeInfoPropertyName = "RunAllTestsToolResult")]
[JsonSerializable(
    typeof(RunAllTestsForProjectTool.Result),
    TypeInfoPropertyName = "RunAllTestsForProjectToolResult")]
[JsonSerializable(
    typeof(RunAllTestsInClassTool.Result),
    TypeInfoPropertyName = "RunAllTestsInClassToolResult")]
[JsonSerializable(typeof(ErrorKind), TypeInfoPropertyName = "ErrorKind")]
[JsonSerializable(typeof(ErrorInfo), TypeInfoPropertyName = "ErrorInfo")]
[JsonSerializable(typeof(TestFailure), TypeInfoPropertyName = "TestFailure")]
[JsonSerializable(typeof(ProjectDiscoveryResult), TypeInfoPropertyName = "ProjectDiscoveryResult")]
[JsonSerializable(typeof(TruncatedText), TypeInfoPropertyName = "TruncatedText")]
[JsonSerializable(typeof(CtrfReport), TypeInfoPropertyName = "CtrfReport")]
public partial class JsonContext : JsonSerializerContext;