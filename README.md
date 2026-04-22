# Nexus Component Counter Tool

A .NET 10 command-line tool that counts components in Sonatype Nexus repositories through the Nexus REST API and writes the results to a JSON file.

## Features

- Filters repositories by `type` and `format`
- Uses HTTP Basic authentication from environment variables
- Processes repositories concurrently
- Handles Nexus component pagination with `continuationToken`
- Continuously writes sorted JSON output as repositories complete

## Requirements

- .NET 10 SDK to build and pack the tool
- Access to a Nexus instance with the REST API enabled
- `NEXUS_USERNAME` and `NEXUS_PASSWORD` environment variables

## Build

```powershell
dotnet build
```

## Pack as a .NET Tool

```powershell
dotnet pack -c Release
```

This produces:

```text
bin\Release\Nexus.ComponentCounter.Tool.1.0.0.nupkg
```

## Install the Tool

Install from the local package output:

```powershell
dotnet tool install --tool-path .\tools Nexus.ComponentCounter.Tool --add-source .\bin\Release --version 1.0.0
```

After installation, the command name is:

```text
nexus-component-counter
```

## Configuration

Set Nexus credentials before running the tool:

```powershell
$env:NEXUS_USERNAME = "your-username"
$env:NEXUS_PASSWORD = "your-password"
```

## Usage

```text
nexus-component-counter --url <nexus-api-url> [--type <repo-type>] [--format <repo-format>] [--concurrency <count>] [--output-dir <directory>]
```

### Options

| Option | Required | Description |
| --- | --- | --- |
| `--url` | Yes | Base URL for the Nexus REST API, for example `https://nexus.example.com/service/rest/v1` |
| `--type` | No | Filter repositories by type |
| `--format` | No | Filter repositories by format |
| `--concurrency` | No | Maximum number of repositories processed concurrently. Default: `10` |
| `--output-dir` | No | Directory for the JSON results file. Default: current directory |
| `-h`, `--help` | No | Show help |

## Examples

Count all repositories:

```powershell
.\tools\nexus-component-counter.exe --url https://nexus.example.com/service/rest/v1
```

Count only hosted NuGet repositories:

```powershell
.\tools\nexus-component-counter.exe --url https://nexus.example.com/service/rest/v1 --type hosted --format nuget
```

Write output to a specific folder and limit concurrency:

```powershell
.\tools\nexus-component-counter.exe --url https://nexus.example.com/service/rest/v1 --concurrency 5 --output-dir .\out
```

### List components

List components in a single repository (default: writes `\<repository\>_components.json` to current directory):

```powershell
dotnet run -- list-components --url https://nexus.example.com/service/rest/v1 --repository maven-public
# writes ./maven-public_components.json
```

Write list output to a specific folder:

```powershell
dotnet run -- list-components --url https://nexus.example.com/service/rest/v1 --repository maven-public --output-dir .\out
# writes .\out\maven-public_components.json
```

Write list output to a specific file:

```powershell
dotnet run -- list-components --url https://nexus.example.com/service/rest/v1 --repository maven-public --output maven_components.json
# writes ./maven_components.json
```

### List assets

List assets in a single repository (default: writes `\<repository\>_assets.json` to current directory):

```powershell
dotnet run -- list-assets --url https://nexus.example.com/service/rest/v1 --repository maven-public
# writes ./maven-public_assets.json
```

Write list assets to a specific file:

```powershell
dotnet run -- list-assets --url https://nexus.example.com/service/rest/v1 --repository maven-public --output assets.json
# writes ./assets.json
```

## Output

The output file name follows this pattern:

```text
{type-or-all}_{format-or-all}_components.json
```

Examples:

```text
[RepoType]_[RepoFormat]_components.json
hosted_nuget_components.json
```

The JSON output is sorted by component count in descending order:

```json
[
  {
    "RepoName": "maven-public",
    "Type": "group",
    "Format": "maven2",
    "Count": 28194
  },
  {
    "RepoName": "maven-central",
    "Type": "proxy",
    "Format": "maven2",
    "Count": 15823
  },
  {
    "RepoName": "nuget-group",
    "Type": "group",
    "Format": "nuget",
    "Count": 10213
  },
  {
    "RepoName": "nuget-hosted",
    "Type": "hosted",
    "Format": "nuget",
    "Count": 7967
  },
  ....
]
```

## Notes

- The tool fails fast if `NEXUS_USERNAME` or `NEXUS_PASSWORD` is not set.
- The `--url` value should point to the Nexus REST API base, typically ending with `service/rest/v1`.
- The original Python script is kept in the repository as `nexus_component_counter.py` for reference during the migration.
