# dotnet-test-runner-mcp

**Run .NET tests from your AI agent without filling its context with terminal logs.**

An [MCP](https://modelcontextprotocol.io/) executable for Linux, Windows, and macOS. Your agent can discover tests, run a method, class, project, or solution, and receive structured results.

<!-- mcp-name: io.github.JasonLandbridge/dotnet-test-runner-mcp -->

## Why not just run `dotnet test`?

This server still uses the .NET CLI underneath. It does **not** make tests faster—it makes their results easier for an agent to consume.

| Direct CLI output | Through this MCP server |
| --- | --- |
| The agent reads build logs, runner banners, and test output. | Normal results return an outcome, counts, and duration where available. |
| Listing tests can flood the context with names. | Discovery returns counts by default; names are opt-in and capped at 200. |
| Large failures can dominate the conversation. | With CTRF reports, suite/project/class results include at most 20 failing names and details for 3 failures. |
| Stack traces add noise on routine runs. | Structured failure stack traces are omitted unless `includeStackTrace=true`. |
| The agent has to assemble shell commands and parse their output. | Named tools expose test selection and return structured JSON. |

**How this saves tokens:** it selects useful result fields instead of forwarding the normal terminal transcript. Use project/method/class selection to run less, prefix filtering to narrow discovery, and request names or stack traces only when needed.

There is no fixed token budget or measured savings guarantee. Failure messages are not length-capped, and build/invocation errors can include raw diagnostic output. Runners without CTRF provide summary results rather than the same per-test failure details.

## Install

Requires the **.NET 10 SDK** on the machine running the server.

Once the package is published to NuGet, use the same command on Linux, Windows, and macOS:

```bash
dotnet tool install --global dotnet-test-runner-mcp
```

The executable is `dotnet-test-runner-mcp` (`dotnet-test-runner-mcp.exe` on Windows). Make sure your agent can find it on `PATH`, or use its absolute path:

- **Linux/macOS:** `$HOME/.dotnet/tools/dotnet-test-runner-mcp`
- **Windows:** `%USERPROFILE%\.dotnet\tools\dotnet-test-runner-mcp.exe`

**Not published yet?** Clone this repository, then run these commands from its root:

```bash
dotnet pack src/DotnetTest.Mcp -c Release -o ./artifacts/package
```

```bash
dotnet tool install --global dotnet-test-runner-mcp --prerelease --add-source ./artifacts/package
```

## Connect your agent

Add this to your MCP client's configuration and point it at the repository you want to test:

```json
{
  "mcpServers": {
    "dotnet-test-runner-mcp": {
      "command": "dotnet-test-runner-mcp",
      "env": {
        "WORKING_DIRECTORY": "/absolute/path/to/your-repository"
      }
    }
  }
}
```

The client starts the executable and communicates over stdio—there is no service or port to configure. On Windows, escape backslashes in JSON paths (`C:\\Repos\\MyProject`).

Then ask your agent: **“Find the tests for CalculatorTests, run them, and summarize any failures.”**

## Available tools

| Tool | What it does |
| --- | --- |
| `list_test_projects` | Find test projects in the solution. |
| `list_tests_summary` | Get counts; optionally include names or filter by project/prefix. |
| `run_single_test` | Run a test method. |
| `run_all_tests_in_class` | Run tests matching a class. |
| `run_all_tests_for_project` | Run one test project. |
| `run_all_tests` | Run the solution's tests. |

Pass a project path for targeted runs: without one, method/class tools select the first discovered test project. Every tool accepts `workingDirectory` to override the configured repository per call.

## Compatibility and settings

Supports VSTest projects and TUnit-specific execution paths, plus a default MTP path. Runner detection reads direct project package references, so imported/central configuration and other MTP runners may need extra care. CTRF failure details require a compatible reporting extension; not every runner provides them. TUnit method filters use short names and can match multiple tests.

Solution discovery needs a `.sln` or `.slnx` and looks under `tests` by default. Test discovery scans at most 8 projects unless you specify one explicitly.

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `WORKING_DIRECTORY` | Current directory | Repository to test. |
| `TESTS_DIRECTORY_NAME` | `tests` | Test-project directory within the solution. |
| `DISABLE_CTRF` | `false` | Disable CTRF switches when unsupported. |
| `TEST_RUN_TIMEOUT_SECONDS` | `180` | Execution timeout; nonpositive values use 180 seconds. |

Only run against trusted repositories: building and testing executes their code with your account's permissions. This is not a sandbox.

## License

[MIT](LICENSE).
