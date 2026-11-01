namespace DotnetTestRunnerMcp.Models;

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
