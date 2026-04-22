# Copilot Instructions

## Build and verification commands

| Purpose | Command | Notes |
| --- | --- | --- |
| Build the solution | `dotnet build` | Builds the single `NexusCounterTools` console/tool project targeting `net10.0`. |
| Run the solution's verification step | `dotnet test` | There is no test project in this repository right now, so this effectively validates restore/build only. |
| Pack the .NET tool | `dotnet pack -c Release` | Produces `bin\Release\Nexus.ComponentCounter.Tool.1.0.0.nupkg`. |
| Run the app from source | `dotnet run -- --url https://nexus.example.com/service/rest/v1` | Set `NEXUS_USERNAME` and `NEXUS_PASSWORD` first. |

There is currently no dedicated lint command or analyzer-specific script in the repository.

## High-level architecture

The repository is a single-project .NET CLI tool with nearly all application logic in `Program.cs`. `NexusComponentCounterApp.RunAsync` is the entry point for the real workflow:

1. Parse arguments with the custom `CommandLineOptions.Parse` helper instead of `System.CommandLine`.
2. Read `NEXUS_USERNAME` and `NEXUS_PASSWORD` from environment variables and fail fast if either is missing.
3. Create one shared `HttpClient` configured for HTTP Basic authentication.
4. Fetch the full repository list from the Nexus `/repositories` endpoint, then apply optional `type` and `format` filtering in-process.
5. Process repositories concurrently with a `SemaphoreSlim` limit. Each repository is paged through `/components?repository=...&continuationToken=...` until Nexus stops returning a continuation token.
6. Store counts in a `ConcurrentDictionary`, then rewrite the full JSON output after each repository completes so the results file stays continuously updated and sorted by descending component count.

The output file is an array of objects (`RepoName`, `Type`, `Format`, `Count`), not a dictionary. The actual filename defaults to `{RepositoryType or RepoTypeAll}_{RepositoryFormat or FormatAll}_components.json`, so preserve that behavior if you change naming logic.

## Key repository conventions

- Keep the tool as a self-contained single-file app unless there is a clear reason to split it. Supporting types such as DTOs, records, and option parsing currently live alongside the main workflow in `Program.cs`.
- CLI parsing is manual and intentionally accepts both `--option value` and `--option=value`. New flags should be added in `CommandLineOptions.Parse` and reflected in the `Usage` raw string.
- JSON uses `System.Text.Json` with `JsonPropertyName` attributes on the Nexus response records. Match the existing casing and avoid adding Newtonsoft.Json.
- Repository counting is concurrency-limited per repository, not per HTTP request. Preserve the existing `SemaphoreSlim` + shared `HttpClient` pattern when changing throughput behavior.
- Results are written incrementally under a separate file lock after each repository finishes. If you touch output behavior, keep the "partially complete but valid JSON file" behavior intact.
- README examples assume the compiled tool is installed under `.\tools\` and invoked as `nexus-component-counter`; keep docs and command examples aligned with the `ToolCommandName` in `NexusCounterTools.csproj`.
- The `--url` argument is expected to be the Nexus REST API base URL, typically ending in `service/rest/v1`, not the web UI root.

## Output behavior notes

- The tool accepts both `--output` (file path) and `--output-dir` (directory) for controlling JSON output.
- For the `count` command the tool prefers `--output` when supplied; otherwise it writes the default-named file into `--output-dir` (or the current directory). The default filename pattern remains `{RepositoryType or RepoTypeAll}_{RepositoryFormat or FormatAll}_components.json`.
- For the `list-components` and `list-assets` commands, if `--output` is omitted the tool will write a default-named file into `--output-dir` (or current directory):
	- `list-components` default filename: `<repository>_components.json`
	- `list-assets` default filename: `<repository>_assets.json`

Examples:

```powershell
# Count, write to folder (default name)
dotnet run -- count --url https://nexus.example.com/service/rest/v1 --output-dir .\out

# List components for a repo (writes ./maven-public_components.json)
dotnet run -- list-components --url https://nexus.example.com/service/rest/v1 --repository maven-public

# List assets and write to specific file
dotnet run -- list-assets --url https://nexus.example.com/service/rest/v1 --repository maven-public --output assets.json
```
