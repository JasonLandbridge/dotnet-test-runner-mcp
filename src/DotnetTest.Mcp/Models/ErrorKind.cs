namespace DotnetTest.Mcp.Models;

public enum ErrorKind
{
    Unknown,
    InvocationError,
    ResultFileMissing,
    ReadFailed,
    TestHostCrashed,
    BuildFailed,
    DiscoveryFailed,
    NoTestsDiscovered,
}