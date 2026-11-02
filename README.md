# dotnet-test-runner-mcp

**Run .NET tests from your AI agent without filling its context with terminal logs.**

An [MCP](https://modelcontextprotocol.io/) executable for Linux, Windows, and macOS. Your agent can discover tests, run a method, class, project, or solution, and receive structured results.

<!-- mcp-name: io.github.JasonLandbridge/dotnet-test-runner-mcp -->

## Why not just run `dotnet test`?

This server still uses the .NET CLI underneath. It does **not** make tests faster—it makes their results easier for an agent to consume.

| Direct CLI output | Through this MCP server |
| --- | --- |
| The agent reads build logs, runner banners, and test output. | Normal results return an outcome, counts, and duration where available. |
| Listing tests can flood the context with names. | Summaries return counts by default; name lists are capped or paginated. |
| Large failures can dominate the conversation. | Batch runs return compact results; detailed failures and output are retrieved on demand. |
| Stack traces add noise on routine runs. | Structured failure stack traces are omitted unless `includeStackTrace=true`. |
| The agent has to assemble shell commands and parse their output. | Named tools expose test selection and return structured JSON. |

**How this saves tokens:** it selects useful result fields instead of forwarding the normal terminal transcript. Use project/method/class selection to run less, prefix filtering to narrow discovery, and request names or stack traces only when needed.

There is no measured token-savings guarantee. The original run tools can include large failure messages or raw build diagnostics. The batch workflow below provides bounded, on-demand detail retrieval. Runners without CTRF provide summary results and captured output, not invented per-test failure details.

## Install

Requires the **.NET 10 SDK** on the machine running the server. `run_changed_tests` also requires Git and a repository with a comparison commit.

Use the same command on Linux, Windows, and macOS:

```bash
dotnet tool install --global dotnet-test-runner-mcp
```

The executable is `dotnet-test-runner-mcp` (`dotnet-test-runner-mcp.exe` on Windows). Make sure your agent can find it on `PATH`, or use its absolute path:

- **Linux/macOS:** `$HOME/.dotnet/tools/dotnet-test-runner-mcp`
- **Windows:** `%USERPROFILE%\.dotnet\tools\dotnet-test-runner-mcp.exe`

**Already installed?** Run `dotnet tool update --global dotnet-test-runner-mcp`, then restart your MCP client to reload its tool list. NuGet only contains published changes.

**Try the current source:** clone this repository and build from its root:

```bash
dotnet build src/dotnet-test-runner-mcp -c Release
```

Configure your client's command as `dotnet` with `args` pointing to the absolute path of `src/dotnet-test-runner-mcp/bin/Release/net10.0/dotnet-test-runner-mcp.dll`. This uses your local changes without replacing the installed NuGet tool.

## Connect your agent

Add this minimal configuration to your MCP client—no environment variables are required:

```json
{
  "mcpServers": {
    "dotnet-test-runner-mcp": {
      "command": "dotnet-test-runner-mcp"
    }
  }
}
```

The server uses the directory it is launched from as its working directory. Launch it from the repository you want to test, or have your MCP client launch it there.

**Optional override:** set `WORKING_DIRECTORY` only if you want to target a different directory. Individual tool calls can also supply `workingDirectory`.

The client starts the executable and communicates over stdio—there is no service or port to configure.

Then ask your agent: **“Find the tests for CalculatorTests, run them, and summarize any failures.”**

## Available tools

### Recommended agent tools

| Tool | Use it for |
| --- | --- |
| `inspect_test_environment` | Check the resolved directory, SDK, runner, and selection/reporting limitations without restoring, building, or running tests. |
| `find_tests` | Search by name across projects; get complete display names, method selectors, ambiguity warnings, and paginated results. |
| `run_tests` | Validate a batch, combine selected methods into one native run per project, and return a retained `RunId`. |
| `get_failure_details` | Retrieve failure messages, optional stack traces, and captured output from a retained run. |
| `run_changed_tests` | Preview or execute affected test projects, with bounded changed-file → owner → dependent-test explanation chains. |

### Focused shortcuts

| Tool | Use it for |
| --- | --- |
| `list_test_projects` | Find eligible test projects in the solution. |
| `list_tests_summary` | Get counts, optionally filtered by project/prefix; include up to 200 names. |
| `run_single_test` | Run a method. |
| `run_all_tests_in_class` | Run tests matching a class. |
| `run_all_tests_for_project` | Run one test project. |
| `run_all_tests` | Run the solution's tests. |

Pass a project path to the shortcut method/class tools; otherwise they select the first eligible project. Only `run_tests` and `run_changed_tests` produce run IDs for `get_failure_details`. Discovery, inspection, and execution tools accept an optional `workingDirectory` override.

## Agent workflow

### 1. Inspect and find

Start with `inspect_test_environment` when runner behavior is unclear. Then call `find_tests`, scoping to a project when known:

```json
{
  "query": "Calculator",
  "projectPath": "tests/My.Tests/My.Tests.csproj",
  "pageSize": 20
}
```

Search is case-insensitive. Use `NextCursor` as the next call's `cursor`, keeping the other arguments unchanged. Pages reuse a fixed snapshot for up to 10 minutes without rediscovering tests. Start without a cursor for fresh results; restart if a snapshot expires or is evicted.

### 2. Run explicit selections

Use each match's `ProjectPath` and `Selector` in `run_tests`. Check `SelectorSupported` and `IsAmbiguous` first:

```json
{
  "selections": [
    {
      "projectPath": "tests/My.Tests/My.Tests.csproj",
      "testName": "My.Tests.CalculatorTests.Adds"
    },
    {
      "projectPath": "tests/Other.Tests/Other.Tests.csproj"
    }
  ]
}
```

Omitting `testName` runs the whole project. The entire batch is validated before execution, including successful discovery; validation errors run nothing and appear in `UnmatchedSelections`. Duplicate selections are deduplicated; overlapping whole-project/method selections are rejected. Projects run sequentially, with distinct method selections combined into one native filter.

TUnit short selectors cannot identify a unique case/class. Set `allowMultipleMatches=true` only when running all matching cases is intended.

Use aggregate counts and `ProjectExecutions` as the authoritative native-run results. When a summary-only runner executes several methods together, individual `Selections` have `CountsKnown=false` and null counts rather than copied or invented per-method totals.

### 3. Fetch only the diagnostics needed

Call `get_failure_details` with the batch's `RunId`, optionally choosing an entry from `FailureIds`:

```json
{
  "runId": "<RunId>",
  "includeStackTrace": true,
  "includeOutput": true,
  "page": 0,
  "pageSize": 3,
  "maxCharacters": 8000
}
```

Traces and output are opt-in. Use `diagnosticField: "trace"` or `"output"` to retrieve that field independently when messages are large; `"message"` retrieves messages only. Without CTRF, `FailureDetailsAvailable` can be false even when tests failed; request output to inspect `RunOutput` instead. Retained ID lists are opt-in with `includeAvailableFailureIds=true`.

Failure pages are zero-based. `HasMore` means another retrievable page exists; it becomes false on the last page even when `FailuresTruncated=true`. Dropped artifacts cannot be fetched.

### 4. Preview changed-project impact

Call `run_changed_tests`:

```json
{ "baseRef": "HEAD", "dryRun": true }
```

Review `SelectedTestProjects`, `Reasons`, `ImpactUnknown`, and the bounded per-project `ImpactExplanations`. A dry run executes nothing and returns `Outcome=Skipped`. Set `dryRun=false` to execute the selection.

Tracked and untracked changes are included. Selection uses evaluated project references and compile items, not method-level test coverage. Shared build changes, missing historical ownership for deleted/renamed files, or other unknown impact select all eligible test projects conservatively.

### Output and retention limits

| Limit | Default / maximum |
| --- | --- |
| Search results per page | 50 / 200 |
| Discovery snapshots | Up to 10 minutes; 128 snapshots / 4,000,000 characters total |
| Failure details per page | 20 / 100 |
| Returned diagnostic text | 8,000 / 100,000 characters total |
| Retained run lifetime | 30 minutes |
| Retained runs / failures | 32 runs; 200 failure records per run |
| Retained diagnostic storage | 1,000,000 characters across runs |
| Live command output | 64 KiB per stdout/stderr stream by default, retaining head/tail diagnostics |
| Structured metadata and discovery | 8 MiB per stream; truncated catalogs are rejected, not treated as complete |

Truncation and eviction are explicit. Run IDs belong to the current server process; restarting it clears all retained results.

The response text budget covers messages, traces, and captured output—not test names, IDs, or JSON metadata. Live capture and incremental batch retention have separate bounds; truncation is reported rather than silently returning complete-looking diagnostics.

## Compatibility and settings

Supports evaluated VSTest and TUnit projects, including imported and conditional package references. Generic Microsoft.Testing.Platform executable projects support whole-project runs; method filtering requires a supported framework selector. Inspection, discovery, and execution share evaluated MSBuild metadata without restoring or building. CTRF failure details require a compatible reporting extension; other runners provide summary counts and captured output. TUnit method filters use short names and can match multiple tests.

Solution discovery needs a `.sln` or `.slnx` and looks under `tests` by default. It scans all matching projects, excluding projects explicitly marked `IsTestProject=false`. An explicit `projectPath` does not require a solution.

All environment variables below are optional.

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `WORKING_DIRECTORY` | Launch directory | Optional override for the repository to test. |
| `TESTS_DIRECTORY_NAME` | `tests` | Test-project directory within the solution. |
| `DISABLE_CTRF` | `false` | Disable CTRF switches when unsupported. |
| `TEST_RUN_TIMEOUT_SECONDS` | `180` | Execution timeout; nonpositive values use 180 seconds. |

Only run against trusted repositories: building and testing executes their code with your account's permissions. This is not a sandbox.

## License

[MIT](LICENSE).
