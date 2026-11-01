# AGENTS.md

This repository is a minimal scaffold for a console-hosted MCP server using the
Model Context Protocol C# SDK.

Use this document as the operating guide for agentic coding assistants working
in this repo.

## Repository Layout

- `src/DotnetTest.Mcp/` - MCP server console app (top-level `Program.cs`).
- `tests/DotnetTest.Mcp.Tests/` - xUnit tests.
- `DotnetTest.Mcp.slnx` - solution file (XML-based `.slnx`).
- `Taskfile.yml` - currently a placeholder task (prints greeting).

## Tooling / Rules Files

Checked for agent instruction files:

- Cursor rules: no `.cursor/rules/` and no `.cursorrules` found.
- Copilot rules: no `.github/copilot-instructions.md` found.

If any of those files are added later, they override this document where they
conflict.

## Build / Run / Test

This repo targets `net10.0` and uses `LangVersion=latest`.

### Restore

```bash
dotnet restore DotnetTest.Mcp.slnx
```

### Build (acts as lint)

Both projects set `TreatWarningsAsErrors=true`, so a normal build is the main
"lint" gate.

```bash
dotnet build DotnetTest.Mcp.slnx -c Release
```

### Run the MCP server

```bash
dotnet run --project src/DotnetTest.Mcp -c Release
```

To pass args to the app:

```bash
dotnet run --project src/DotnetTest.Mcp -- <args>
```

### Test (all)

```bash
dotnet test DotnetTest.Mcp.slnx -c Release
```

Or test just the test project:

```bash
dotnet test tests/DotnetTest.Mcp.Tests/DotnetTest.Mcp.Tests.csproj -c Release
```

### Run a single test / subset

This repo uses xUnit; use `dotnet test --filter`.

Run one test by fully qualified name:

```bash
dotnet test tests/DotnetTest.Mcp.Tests/DotnetTest.Mcp.Tests.csproj \
  -c Release \
  --filter "FullyQualifiedName=DotnetTest.Mcp.Tests.EchoToolTests.Echo_ReturnsGreeting"
```

Run all tests in a class (substring match):

```bash
dotnet test tests/DotnetTest.Mcp.Tests/DotnetTest.Mcp.Tests.csproj \
  -c Release \
  --filter "FullyQualifiedName~EchoToolTests"
```

Run tests by method name substring:

```bash
dotnet test tests/DotnetTest.Mcp.Tests/DotnetTest.Mcp.Tests.csproj \
  -c Release \
  --filter "FullyQualifiedName~Echo_ReturnsGreeting"
```

More verbose test output:

```bash
dotnet test DotnetTest.Mcp.slnx -c Release -v normal
```

### Formatting

No `.editorconfig` and no `dotnet format` tool manifest is present.

If you have `dotnet format` available locally, you may use it, but keep diffs
focused (avoid repo-wide reformat unless explicitly requested):

```bash
dotnet format DotnetTest.Mcp.slnx
```

## Code Style Guidelines (C#)

Follow existing patterns in `src/DotnetTest.Mcp/Program.cs`,
`src/DotnetTest.Mcp/EchoTool.cs`, and `tests/DotnetTest.Mcp.Tests/`.

### Imports / Namespaces

- Prefer implicit usings (enabled). Only add explicit `using` directives when
  needed.
- Keep `using` directives at the top of the file.
- Group and order usings consistently:
  - `System.*` first
  - then third-party
  - then project namespaces (e.g., `DotnetTest.Mcp`)
  - one blank line between groups
  - alphabetize within each group
- Prefer file-scoped namespaces (`namespace X;`) for non-top-level files.
- Top-level statements are acceptable for app entrypoints (`Program.cs`).

### Formatting / Layout

- Indentation: 4 spaces.
- Braces: K&R style.
- Keep blank lines tight (generally 0-1 consecutive blank lines). The bundled
  JetBrains settings (`DotnetTest.Mcp.DotSettings`) reflect this preference.
- Prefer expression-bodied members only when it improves readability (small,
  obvious methods/properties).
- Wrap long argument lists and chained calls rather than exceeding typical line
  lengths.

### Types / Nullability

- Nullable reference types are enabled: do not introduce new nullable flows
  without intent.
- Prefer non-null defaults; validate inputs early.
- Use framework helpers where appropriate:
  - `ArgumentNullException.ThrowIfNull(arg)`
  - `ArgumentException.ThrowIfNullOrEmpty(str)` (or explicit checks for older
    APIs)
- Prefer `readonly` where possible; prefer immutable data for tool input/output.

### Naming

- Public types/members: `PascalCase`.
- Locals/parameters: `camelCase`.
- Constants: `PascalCase` (for public) or `camelCase` (private) following .NET
  conventions; avoid ALL_CAPS.
- Test names: `Method_Scenario_Expected` (matches existing
  `Echo_ReturnsGreeting`).
- Async methods end with `Async`.

### Error Handling

- Do not swallow exceptions silently.
- Prefer returning useful errors at the boundary (tool method) and let the host
  handle fatal conditions.
- Use structured logging via `ILogger` when adding non-trivial behavior.
- Prefer `try/catch` only around code where you can add context or recover.
- Keep thrown exception types specific (`ArgumentException`,
  `InvalidOperationException`, etc.).

### Dependency Injection / Hosting

The server is hosted via `Microsoft.Extensions.Hosting`.

- Keep host setup in `Program.cs` simple; prefer extension methods for larger
  registrations.
- Use DI for services rather than static state.
- If adding background services, respect cancellation tokens and keep shutdown
  responsive.

## MCP Tool Conventions

Tools are discovered from the server assembly.

- Tool classes:
  - mark tool container types with `[McpServerToolType]`
  - mark tool methods with `[McpServerTool]`
  - add `[Description("...")]` so tool intent is visible to clients
- Keep tool methods deterministic and side-effect aware.
- Prefer small, composable tools over one monolithic tool.

## Testing Guidelines (xUnit)

- Keep tests small and descriptive.
- Avoid over-mocking for simple logic (current tests directly instantiate).
- Prefer `Assert.Equal`/`Assert.NotNull` etc. over custom assertions.
- If introducing shared setup, prefer fixtures sparingly; keep readability high.

## Working Agreements for Agents

- Minimize diff size: change only what is required for the task.
- Keep builds green: `dotnet build` and `dotnet test` should pass.
- When adding new files, place production code under `src/` and tests under
  `tests/`.

## Docs

- MTP Dotnet Test Arguments: ./docs/DotnetTestCommands.md