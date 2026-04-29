using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

return await NexusComponentCounterApp.RunAsync(args);

internal static class NexusComponentCounterApp
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        var parseResult = CommandLineOptions.Parse(args);

        if (parseResult.ShowHelp)
        {
            Console.WriteLine(CommandLineOptions.Usage);
            return parseResult.ExitCode;
        }

        if (parseResult.ShowVersion)
        {
            var version =
                typeof(NexusComponentCounterApp).Assembly.GetName().Version?.ToString() ?? "1.0.0";
            Console.WriteLine(version);
            return 0;
        }

        if (parseResult.ErrorMessage is not null)
        {
            Console.Error.WriteLine(parseResult.ErrorMessage);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLineOptions.Usage);
            return 1;
        }

        var options = parseResult.Options!;
        var username = Environment.GetEnvironmentVariable("NEXUS_USERNAME");
        var password = Environment.GetEnvironmentVariable("NEXUS_PASSWORD");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine(
                "NEXUS_USERNAME and NEXUS_PASSWORD environment variables must be set."
            );
            return 1;
        }

        var baseUrl = options.Url.TrimEnd('/');

        using var httpClient = CreateHttpClient(username, password);

        return options.Command switch
        {
            CommandType.Count => await RunCountAsync(baseUrl, httpClient, options, CancellationToken.None),
            CommandType.ListComponents => await RunListComponentsAsync(
                baseUrl,
                httpClient,
                options,
                CancellationToken.None
            ),
            CommandType.ListAssets => await RunListAssetsAsync(
                baseUrl,
                httpClient,
                options,
                CancellationToken.None
            ),
            CommandType.DeleteComponents => await RunDeleteComponentsAsync(
                baseUrl,
                httpClient,
                options,
                CancellationToken.None
            ),
            _ => throw new InvalidOperationException($"Unsupported command '{options.Command}'.")
        };
    }

    private static async Task<int> RunDeleteComponentsAsync(
        string baseUrl,
        HttpClient httpClient,
        CommandLineOptions options,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(options.InputPath) || !File.Exists(options.InputPath))
        {
            Console.Error.WriteLine($"Input file not found: {options.InputPath}");
            return 1;
        }

        List<ListComponentResult>? componentsToDelete;
        try
        {
            var json = await File.ReadAllTextAsync(options.InputPath, cancellationToken);
            componentsToDelete = JsonSerializer.Deserialize<List<ListComponentResult>>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to parse input file: {ex.Message}");
            return 1;
        }

        if (componentsToDelete == null || componentsToDelete.Count == 0)
        {
            Console.WriteLine("No components found in input file to delete.");
            return 0;
        }

        if (!options.Force)
        {
            Console.WriteLine("--- DRY RUN MODE --- (Use --force to actually delete)");
            foreach (var component in componentsToDelete)
            {
                Console.WriteLine($"[Dry-Run] Would delete: {component.Repository} | {component.Group ?? "no-group"} | {component.Name} | {component.Version} (ID: {component.Id})");
            }
            Console.WriteLine($"--- Total components to delete: {componentsToDelete.Count} ---");
            Console.WriteLine("Reminder: Add --force flag to execute deletion.");
            return 0;
        }

        Console.WriteLine($"Starting bulk deletion of {componentsToDelete.Count} components with concurrency {options.Concurrency}...");

        var concurrencyLimiter = new SemaphoreSlim(options.Concurrency);
        var successCount = 0;
        var failureCount = 0;
        var consecutiveErrors = 0;
        var total = componentsToDelete.Count;

        var tasks = componentsToDelete.Select(async (component, index) =>
        {
            await concurrencyLimiter.WaitAsync(cancellationToken);
            try
            {
                if (Volatile.Read(ref consecutiveErrors) >= 3)
                {
                    return;
                }

                var deleteUrl = $"{baseUrl}/components/{component.Id}";
                using var response = await httpClient.DeleteAsync(deleteUrl, cancellationToken);

                var currentCount = Interlocked.Increment(ref successCount) + failureCount;

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[{currentCount}/{total}] [Success] Deleted: {component.Name} ({component.Version})");
                    Volatile.Write(ref consecutiveErrors, 0);
                }
                else
                {
                    Interlocked.Decrement(ref successCount);
                    Interlocked.Increment(ref failureCount);
                    Console.Error.WriteLine($"[{currentCount}/{total}] [Failed]  {component.Name} ({component.Version}) - Status: {response.StatusCode}");

                    if ((int)response.StatusCode >= 500)
                    {
                        if (Interlocked.Increment(ref consecutiveErrors) >= 3)
                        {
                            Console.Error.WriteLine("Too many consecutive server errors. Aborting for safety.");
                        }
                    }
                    else
                    {
                        Volatile.Write(ref consecutiveErrors, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failureCount);
                Console.Error.WriteLine($"[Error] Unexpected error for {component.Name}: {ex.Message}");
            }
            finally
            {
                concurrencyLimiter.Release();
            }
        });

        await Task.WhenAll(tasks);

        Console.WriteLine("\n--- Deletion Summary ---");
        Console.WriteLine($"Total Attempted: {total}");
        Console.WriteLine($"Successfully Deleted: {successCount}");
        Console.WriteLine($"Failed: {failureCount}");

        return failureCount == 0 ? 0 : 1;
    }

    private static async Task<int> RunCountAsync(
        string baseUrl,
        HttpClient httpClient,
        CommandLineOptions options,
        CancellationToken cancellationToken
    )
    {
        string outputPath;

        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            var outputDir = Path.GetDirectoryName(options.OutputPath);
            if (!string.IsNullOrWhiteSpace(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            outputPath = options.OutputPath;
        }
        else
        {
            var dir = options.OutputDirectory ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(dir);

            var outputFileName =
                $"{options.RepositoryType ?? "RepoTypeAll"}_{options.RepositoryFormat ?? "FormatAll"}_components.json";
            outputPath = Path.Combine(dir, outputFileName);
        }

        var repositories = await GetRepositoriesAsync(
            baseUrl,
            httpClient,
            options.RepositoryType,
            options.RepositoryFormat,
            cancellationToken
        );

        Console.WriteLine($"Total repositories fetched: {repositories.Count}");

        if (repositories.Count == 0)
        {
            await WriteRepositoryCountResultsFileAsync(
                outputPath,
                new Dictionary<string, RepositoryCountResult>(StringComparer.OrdinalIgnoreCase),
                cancellationToken
            );

            return 0;
        }

        var concurrencyLimiter = new SemaphoreSlim(options.Concurrency);
        var fileLock = new SemaphoreSlim(1, 1);
        var results = new ConcurrentDictionary<string, RepositoryCountResult>(
            StringComparer.OrdinalIgnoreCase
        );
        var completedRepositories = 0;

        var tasks = repositories.Select(
            repository =>
                CountRepositoryComponentsAsync(
                    baseUrl,
                    repository,
                    httpClient,
                    concurrencyLimiter,
                    fileLock,
                    results,
                    outputPath,
                    repositories.Count,
                    () => Volatile.Read(ref completedRepositories),
                    () => Interlocked.Increment(ref completedRepositories),
                    cancellationToken
                )
        );

        await Task.WhenAll(tasks);
        return 0;
    }

    private static async Task<int> RunListComponentsAsync(
        string baseUrl,
        HttpClient httpClient,
        CommandLineOptions options,
        CancellationToken cancellationToken
    )
    {
        var components = await GetAllPagesAsync<ComponentResponse>(
            continuationToken => BuildComponentsUrl(baseUrl, options.Repository!, continuationToken),
            httpClient,
            cancellationToken
        );

        IEnumerable<ComponentResponse> filteredComponents = components;

        if (!string.IsNullOrWhiteSpace(options.NamePattern))
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(
                    options.NamePattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled
                );
                filteredComponents = components.Where(c => regex.IsMatch(c.Name));
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Invalid name regex pattern: {ex.Message}");
                return 1;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.VersionPattern))
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(
                    options.VersionPattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled
                );
                filteredComponents = filteredComponents.Where(c => !string.IsNullOrEmpty(c.Version) && regex.IsMatch(c.Version));
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"Invalid version regex pattern: {ex.Message}");
                return 1;
            }
        }

        var results = filteredComponents
            .Select(CreateListComponentResult)
            .ToList();

        var orderedResults = SortComponentResults(results, options.SortBy, options.SortOrder);
        var limitedResults = ApplyLimit(orderedResults, options.Limit);

        var listOutputPath = options.OutputPath;
        if (string.IsNullOrWhiteSpace(listOutputPath))
        {
            var dir = options.OutputDirectory ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(dir);
            var fileName = $"{options.Repository}_components.json";
            listOutputPath = Path.Combine(dir, fileName);
        }

        await WriteJsonOutputAsync(listOutputPath, limitedResults, cancellationToken);
        return 0;
    }

    private static async Task<int> RunListAssetsAsync(
        string baseUrl,
        HttpClient httpClient,
        CommandLineOptions options,
        CancellationToken cancellationToken
    )
    {
        var assets = await GetAllPagesAsync<NexusAssetResponse>(
            continuationToken => BuildAssetsUrl(baseUrl, options.Repository!, continuationToken),
            httpClient,
            cancellationToken
        );

        var results = assets
            .Select(CreateListAssetResult)
            .ToList();

        var orderedResults = SortAssetResults(results, options.SortBy, options.SortOrder);
        var limitedResults = ApplyLimit(orderedResults, options.Limit);

        var listOutputPath = options.OutputPath;
        if (string.IsNullOrWhiteSpace(listOutputPath))
        {
            var dir = options.OutputDirectory ?? Directory.GetCurrentDirectory();
            Directory.CreateDirectory(dir);
            var fileName = $"{options.Repository}_assets.json";
            listOutputPath = Path.Combine(dir, fileName);
        }

        await WriteJsonOutputAsync(listOutputPath, limitedResults, cancellationToken);
        return 0;
    }

    private static HttpClient CreateHttpClient(string username, string password)
    {
        var client = new HttpClient();
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            credentials
        );
        return client;
    }

    private static async Task<IReadOnlyList<RepositoryInfo>> GetRepositoriesAsync(
        string baseUrl,
        HttpClient httpClient,
        string? repositoryType,
        string? repositoryFormat,
        CancellationToken cancellationToken
    )
    {
        var repositories = await GetFromJsonAsync<List<RepositoryResponse>>(
            httpClient,
            $"{baseUrl}/repositories",
            cancellationToken
        );

        return repositories
            .Where(
                repository =>
                    (
                        repositoryType is null
                        || string.Equals(
                            repository.Type,
                            repositoryType,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    && (
                        repositoryFormat is null
                        || string.Equals(
                            repository.Format,
                            repositoryFormat,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
            )
            .Select(
                repository =>
                    new RepositoryInfo(repository.Name, repository.Type, repository.Format)
            )
            .ToArray();
    }

    private static async Task CountRepositoryComponentsAsync(
        string baseUrl,
        RepositoryInfo repository,
        HttpClient httpClient,
        SemaphoreSlim concurrencyLimiter,
        SemaphoreSlim fileLock,
        ConcurrentDictionary<string, RepositoryCountResult> results,
        string outputPath,
        int totalRepositories,
        Func<int> getCompletedCount,
        Func<int> incrementCompletedCount,
        CancellationToken cancellationToken
    )
    {
        Console.WriteLine($"Starting processing repository {repository.Name}");

        var componentCount = 0;
        string? continuationToken = null;

        await concurrencyLimiter.WaitAsync(cancellationToken);

        try
        {
            do
            {
                var requestUrl = BuildComponentsUrl(baseUrl, repository.Name, continuationToken);
                var response = await GetFromJsonAsync<PagedResponse<ComponentResponse>>(
                    httpClient,
                    requestUrl,
                    cancellationToken
                );

                componentCount += response.Items.Length;
                Console.WriteLine(
                    $"Found {componentCount} components ... in {repository.Name}     (Completed repos: {getCompletedCount()}/{totalRepositories})"
                );

                continuationToken = string.IsNullOrWhiteSpace(response.ContinuationToken)
                    ? null
                    : response.ContinuationToken;
            } while (continuationToken is not null);

            results[repository.Name] = new RepositoryCountResult(
                repository.Type,
                repository.Format,
                componentCount
            );
        }
        finally
        {
            concurrencyLimiter.Release();
        }

        await fileLock.WaitAsync(cancellationToken);

        try
        {
            var orderedResults = results
                .OrderByDescending(entry => entry.Value.Count)
                .ToDictionary(
                    entry => entry.Key,
                    entry => entry.Value,
                    StringComparer.OrdinalIgnoreCase
                );

            await WriteRepositoryCountResultsFileAsync(
                outputPath,
                orderedResults,
                cancellationToken
            );
        }
        finally
        {
            fileLock.Release();
        }

        var completed = incrementCompletedCount();
        Console.WriteLine($"Completed {completed}/{totalRepositories}: {repository.Name}");
    }

    private static async Task<IReadOnlyList<TItem>> GetAllPagesAsync<TItem>(
        Func<string?, string> buildUrl,
        HttpClient httpClient,
        CancellationToken cancellationToken
    )
    {
        var results = new List<TItem>();
        string? continuationToken = null;

        do
        {
            var response = await GetFromJsonAsync<PagedResponse<TItem>>(
                httpClient,
                buildUrl(continuationToken),
                cancellationToken
            );

            results.AddRange(response.Items);
            continuationToken = string.IsNullOrWhiteSpace(response.ContinuationToken)
                ? null
                : response.ContinuationToken;
        } while (continuationToken is not null);

        return results;
    }

    private static async Task<T> GetFromJsonAsync<T>(
        HttpClient httpClient,
        string requestUrl,
        CancellationToken cancellationToken
    )
    {
        using var response = await httpClient.GetAsync(requestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var content = await JsonSerializer.DeserializeAsync<T>(
            contentStream,
            JsonOptions,
            cancellationToken
        );

        return content
            ?? throw new InvalidOperationException(
                $"Received an empty JSON response from {requestUrl}."
            );
    }

    private static ListComponentResult CreateListComponentResult(ComponentResponse component)
    {
        var assets = component.Assets ?? Array.Empty<NexusAssetResponse>();
        var createdCandidates = assets
            .Select(GetAssetAgeTimestamp)
            .Where(timestamp => timestamp.HasValue)
            .Select(timestamp => timestamp!.Value)
            .ToArray();
        var modifiedCandidates = assets
            .Where(asset => asset.LastModified.HasValue)
            .Select(asset => asset.LastModified!.Value)
            .ToArray();
        var downloadedCandidates = assets
            .Where(asset => asset.LastDownloaded.HasValue)
            .Select(asset => asset.LastDownloaded!.Value)
            .ToArray();

        return new ListComponentResult(
            component.Repository,
            component.Format,
            component.Group,
            component.Name,
            component.Version,
            component.Id,
            assets.Length,
            createdCandidates.Length == 0 ? null : createdCandidates.Min(),
            modifiedCandidates.Length == 0 ? null : modifiedCandidates.Max(),
            downloadedCandidates.Length == 0 ? null : downloadedCandidates.Max()
        );
    }

    private static ListAssetResult CreateListAssetResult(NexusAssetResponse asset) =>
        new(
            asset.Repository,
            asset.Format,
            asset.Path,
            asset.DownloadUrl,
            asset.Id,
            asset.FileSize,
            GetAssetAgeTimestamp(asset),
            asset.LastModified,
            asset.LastDownloaded
        );

    private static DateTimeOffset? GetAssetAgeTimestamp(NexusAssetResponse asset) =>
        asset.BlobCreated ?? asset.LastModified;

    private static IReadOnlyList<ListComponentResult> SortComponentResults(
        IEnumerable<ListComponentResult> results,
        SortField sortBy,
        SortDirection sortOrder
    ) =>
        sortBy switch
        {
            SortField.Age => sortOrder == SortDirection.Desc
                ? results
                    .OrderBy(result => result.AgeTimestamp is null)
                    .ThenBy(result => result.AgeTimestamp)
                    .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : results
                    .OrderBy(result => result.AgeTimestamp is null)
                    .ThenByDescending(result => result.AgeTimestamp)
                    .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            SortField.LastDownloaded => ApplySort(
                results,
                result => result.LastDownloaded,
                sortOrder
            ),
            SortField.LastModified => ApplySort(
                results,
                result => result.LastModified,
                sortOrder
            ),
            SortField.Version => ApplySort(
                results,
                result => result.Version ?? string.Empty,
                sortOrder
            ),
            _ => ApplySort(results, result => result.Name ?? string.Empty, sortOrder)
        };

    private static IReadOnlyList<ListAssetResult> SortAssetResults(
        IEnumerable<ListAssetResult> results,
        SortField sortBy,
        SortDirection sortOrder
    ) =>
        sortBy switch
        {
            SortField.Age => sortOrder == SortDirection.Desc
                ? results
                    .OrderBy(result => result.AgeTimestamp is null)
                    .ThenBy(result => result.AgeTimestamp)
                    .ThenBy(result => result.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : results
                    .OrderBy(result => result.AgeTimestamp is null)
                    .ThenByDescending(result => result.AgeTimestamp)
                    .ThenBy(result => result.Path, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            SortField.LastDownloaded => ApplySort(
                results,
                result => result.LastDownloaded,
                sortOrder
            ),
            SortField.LastModified => ApplySort(
                results,
                result => result.LastModified,
                sortOrder
            ),
            SortField.Version => ApplySort(results, result => result.Path, sortOrder),
            _ => ApplySort(results, result => result.Path, sortOrder)
        };

    private static IReadOnlyList<T> ApplySort<T, TKey>(
        IEnumerable<T> results,
        Func<T, TKey> selector,
        SortDirection sortOrder
    )
        where TKey : notnull =>
        sortOrder == SortDirection.Asc
            ? results.OrderBy(selector).ToArray()
            : results.OrderByDescending(selector).ToArray();

    private static IReadOnlyList<T> ApplyLimit<T>(IReadOnlyList<T> results, int? limit) =>
        limit is null ? results : results.Take(limit.Value).ToArray();

    private static async Task WriteRepositoryCountResultsFileAsync(
        string outputPath,
        IDictionary<string, RepositoryCountResult> results,
        CancellationToken cancellationToken
    )
    {
        var arrayResults = results
            .Select(
                kvp =>
                    new
                    {
                        RepoName = kvp.Key,
                        Type = kvp.Value.Type,
                        Format = kvp.Value.Format,
                        Count = kvp.Value.Count
                    }
            )
            .ToArray();

        await using var outputStream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(
            outputStream,
            arrayResults,
            JsonOptions,
            cancellationToken
        );
    }

    private static async Task WriteJsonOutputAsync<T>(
        string? outputPath,
        IReadOnlyList<T> results,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.WriteLine(JsonSerializer.Serialize(results, JsonOptions));
            return;
        }

        var directory = Path.GetDirectoryName(outputPath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var outputStream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(
            outputStream,
            results,
            JsonOptions,
            cancellationToken
        );
    }

    private static string BuildComponentsUrl(
        string baseUrl,
        string repositoryName,
        string? continuationToken
    )
    {
        var builder = new StringBuilder(
            $"{baseUrl}/components?repository={Uri.EscapeDataString(repositoryName)}"
        );

        if (!string.IsNullOrWhiteSpace(continuationToken))
        {
            builder.Append("&continuationToken=");
            builder.Append(Uri.EscapeDataString(continuationToken));
        }

        return builder.ToString();
    }

    private static string BuildAssetsUrl(
        string baseUrl,
        string repositoryName,
        string? continuationToken
    )
    {
        var builder = new StringBuilder(
            $"{baseUrl}/assets?repository={Uri.EscapeDataString(repositoryName)}"
        );

        if (!string.IsNullOrWhiteSpace(continuationToken))
        {
            builder.Append("&continuationToken=");
            builder.Append(Uri.EscapeDataString(continuationToken));
        }

        return builder.ToString();
    }
}

internal enum CommandType
{
    Count,
    ListComponents,
    ListAssets,
    DeleteComponents
}

internal enum SortField
{
    Name,
    Version,
    Age,
    LastModified,
    LastDownloaded
}

internal enum SortDirection
{
    Asc,
    Desc
}

internal sealed record RepositoryInfo(string Name, string Type, string Format);

internal sealed record RepositoryCountResult(string Type, string Format, int Count);

internal sealed record ListComponentResult(
    string Repository,
    string Format,
    string? Group,
    string Name,
    string? Version,
    string Id,
    int AssetCount,
    DateTimeOffset? AgeTimestamp,
    DateTimeOffset? LastModified,
    DateTimeOffset? LastDownloaded
);

internal sealed record ListAssetResult(
    string Repository,
    string Format,
    string Path,
    string DownloadUrl,
    string Id,
    long? FileSize,
    DateTimeOffset? AgeTimestamp,
    DateTimeOffset? LastModified,
    DateTimeOffset? LastDownloaded
);

internal sealed record RepositoryResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("format")] string Format
);

internal sealed record PagedResponse<T>(
    [property: JsonPropertyName("items")] T[] Items,
    [property: JsonPropertyName("continuationToken")] string? ContinuationToken
);

internal sealed record ComponentResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("assets")] NexusAssetResponse[]? Assets
);

internal sealed record NexusAssetResponse(
    [property: JsonPropertyName("downloadUrl")] string DownloadUrl,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("fileSize")] long? FileSize,
    [property: JsonPropertyName("lastModified")] DateTimeOffset? LastModified,
    [property: JsonPropertyName("lastDownloaded")] DateTimeOffset? LastDownloaded,
    [property: JsonPropertyName("blobCreated")] DateTimeOffset? BlobCreated
);

internal sealed record ParsedOptions(
    CommandLineOptions? Options,
    bool ShowHelp,
    bool ShowVersion,
    string? ErrorMessage,
    int ExitCode = 0
);

internal sealed class CommandLineOptions
{
    public static string Usage =>
        """
        Usage:
          nexus-component-counter --url <nexus-api-url> [--type <repo-type>] [--format <repo-format>] [--concurrency <count>] [--output-dir <directory>]
          nexus-component-counter count --url <nexus-api-url> [--type <repo-type>] [--format <repo-format>] [--concurrency <count>] [--output-dir <directory>]
          nexus-component-counter list-components --url <nexus-api-url> --repository <repo-name> [--sort-by <name|version|age|last-modified|last-downloaded>] [--order <asc|desc>] [--limit <count>] [--output <file>] [--name-pattern <regex>] [--version-pattern <regex>]
          nexus-component-counter list-assets --url <nexus-api-url> --repository <repo-name> [--sort-by <name|version|age|last-modified|last-downloaded>] [--order <asc|desc>] [--limit <count>] [--output <file>]
          nexus-component-counter delete-components --url <nexus-api-url> --input <file> [--force] [--concurrency <count>]

        Commands:
          count             Count components per repository. The bare command without a subcommand behaves the same way.
          list-components   List components in a repository and sort the results client-side.
          list-assets       List assets in a repository and sort the results client-side.
          delete-components Bulk delete components from a JSON input file generated by list-components.

        Common options:
          --url             Required. Base URL for the Nexus REST API.
          -h, --help        Show this help message.
          -v, --version     Show version information.

        Count options:
          --type            Optional. Filter repositories by type.
          --format          Optional. Filter repositories by format.
          --concurrency     Optional. Maximum number of repositories processed concurrently. Default: 10.
          --output-dir      Optional. Directory for the JSON results file. Default: current directory.

        List options:
          --repository      Required. Repository name to inspect.
          --sort-by         Optional. Sort key. Default: name.
          --order           Optional. Sort order. Default: desc.
          --limit           Optional. Maximum number of rows to return after sorting.
          --output          Optional. File path for JSON output. If omitted, JSON is written to stdout.
          --name-pattern    Optional. Regex pattern to filter component names client-side.
          --version-pattern Optional. Regex pattern to filter component versions client-side.

        Delete options:
          --input           Required for delete-components. Path to the JSON file containing components to delete.
          --force           Optional. Actually execute deletion. If omitted, performs a dry run.
          --concurrency     Optional for delete-components. Default: 2.

        Notes:
          age sorting uses blobCreated when available and falls back to lastModified.
          list-components derives age from the earliest asset timestamp and last-downloaded/last-modified from the most recent asset timestamp.

        Environment:
          NEXUS_USERNAME    Nexus username for HTTP basic auth.
          NEXUS_PASSWORD    Nexus password for HTTP basic auth.
        """;

    public required CommandType Command { get; init; }

    public required string Url { get; init; }

    public string? RepositoryType { get; init; }

    public string? RepositoryFormat { get; init; }

    public int Concurrency { get; init; } = 10;

    public string OutputDirectory { get; init; } = Directory.GetCurrentDirectory();

    public string? Repository { get; init; }

    public SortField SortBy { get; init; } = SortField.Name;

    public SortDirection SortOrder { get; init; } = SortDirection.Desc;

    public int? Limit { get; init; }

    public string? OutputPath { get; init; }

    public string? InputPath { get; init; }

    public bool Force { get; init; }


    public string? VersionPattern { get; init; }
    public string? NamePattern { get; init; }

    public static ParsedOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new ParsedOptions(null, true, false, null);
        }

        var command = CommandType.Count;
        var startIndex = 0;

        if (!args[0].StartsWith("-", StringComparison.Ordinal))
        {
            switch (args[0])
            {
                case "count":
                    command = CommandType.Count;
                    startIndex = 1;
                    break;
                case "list-components":
                    command = CommandType.ListComponents;
                    startIndex = 1;
                    break;
                case "list-assets":
                    command = CommandType.ListAssets;
                    startIndex = 1;
                    break;
                case "delete-components":
                    command = CommandType.DeleteComponents;
                    startIndex = 1;
                    break;
                default:
                    return new ParsedOptions(null, false, false, $"Unrecognized command '{args[0]}'.");
            }
        }

        string? url = null;
        string? repositoryType = null;
        string? repositoryFormat = null;
        var concurrency = 10;
        string? outputDirectory = null;
        string? repository = null;
        string? inputPath = null;
        var force = false;
        string? versionPattern = null;
        int? customConcurrency = null;
        string? namePattern = null;
        var sortBy = SortField.Name;
        var sortOrder = SortDirection.Desc;
        int? limit = null;
        string? outputPath = null;

        for (var index = startIndex; index < args.Length; index++)
        {
            var argument = args[index];

            if (argument is "-h" or "--help")
            {
                return new ParsedOptions(null, true, false, null);
            }

            if (argument is "-v" or "--version")
            {
                return new ParsedOptions(null, false, true, null);
            }

            if (!TrySplitArgument(argument, out var optionName, out var inlineValue))
            {
                return new ParsedOptions(null, false, false, $"Unrecognized argument '{argument}'.");
            }

            string? optionValue = inlineValue;

            if (optionValue is null && optionName != "--force")
            {
                if (index + 1 >= args.Length)
                {
                    return new ParsedOptions(null, false, false, $"Missing value for '{optionName}'.");
                }

                optionValue = args[++index];
            }

            switch (optionName)
            {
                case "--url":
                    url = optionValue;
                    break;
                case "--type":
                    repositoryType = optionValue;
                    break;
                case "--format":
                    repositoryFormat = optionValue;
                    break;
                case "--concurrency":
                    if (!int.TryParse(optionValue, out var parsedConcurrency) || parsedConcurrency <= 0)
                    {
                        return new ParsedOptions(
                            null,
                            false,
                            false,
                            "Concurrency must be a positive integer."
                        );
                    }
                    customConcurrency = parsedConcurrency;
                    break;
                case "--output-dir":
                    outputDirectory = optionValue;
                    break;
                case "--repository":
                    repository = optionValue;
                    break;
                case "--sort-by":
                    if (!TryParseSortField(optionValue, out sortBy))
                    {
                        return new ParsedOptions(
                            null,
                            false,
                            false,
                            $"Unsupported sort field '{optionValue}'."
                        );
                    }

                    break;
                case "--order":
                    if (!TryParseSortDirection(optionValue, out sortOrder))
                    {
                        return new ParsedOptions(
                            null,
                            false,
                            false,
                            $"Unsupported sort order '{optionValue}'."
                        );
                    }

                    break;
                case "--limit":
                    if (!int.TryParse(optionValue, out var parsedLimit) || parsedLimit <= 0)
                    {
                        return new ParsedOptions(
                            null,
                            false,
                            false,
                            "Limit must be a positive integer."
                        );
                    }

                    limit = parsedLimit;
                    break;
                case "--output":
                    outputPath = optionValue;
                    break;
                case "--version-pattern":
                    versionPattern = optionValue;
                    break;
                case "--name-pattern":
                    namePattern = optionValue;
                    break;
                case "--input":
                    inputPath = optionValue;
                    break;
                case "--force":
                    force = true;
                    // --force is a flag, so we don't consume the next argument unless it's inline
                    if (inlineValue is null)
                    {
                        // Roll back the index increment if we didn't use an inline value
                        // Wait, common way: if it's a flag, we don't increment index if it was't inline.
                        // The current loop structure always increments if optionValue was null.
                        // Let's adjust the logic slightly for flags.
                    }
                    else if (bool.TryParse(inlineValue, out var parsedForce))
                    {
                        force = parsedForce;
                    }
                    break;
                default:
                    return new ParsedOptions(
                        null,
                        false,
                        false,
                        $"Unrecognized argument '{optionName}'."
                    );
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return new ParsedOptions(null, false, false, "The --url option is required.");
        }

        if (
            command is CommandType.DeleteComponents
            && string.IsNullOrWhiteSpace(inputPath)
        )
        {
            return new ParsedOptions(null, false, false, "The --input option is required for delete-components.");
        }

        var finalConcurrency = customConcurrency ?? (command == CommandType.DeleteComponents ? 2 : 10);

        return new ParsedOptions(
            new CommandLineOptions
            {
                Command = command,
                Url = url,
                RepositoryType = string.IsNullOrWhiteSpace(repositoryType) ? null : repositoryType,
                RepositoryFormat = string.IsNullOrWhiteSpace(repositoryFormat)
                    ? null
                    : repositoryFormat,
                Concurrency = finalConcurrency,
                OutputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
                    ? Directory.GetCurrentDirectory()
                    : outputDirectory,
                Repository = string.IsNullOrWhiteSpace(repository) ? null : repository,
                SortBy = sortBy,
                SortOrder = sortOrder,
                Limit = limit,
                OutputPath = string.IsNullOrWhiteSpace(outputPath) ? null : outputPath,
                InputPath = inputPath,
                Force = force,
                NamePattern = namePattern,
                VersionPattern = versionPattern
            },
            false,
            false,
            null
        );
    }

    private static bool TryParseSortField(string value, out SortField sortField)
    {
        sortField = value.ToLowerInvariant() switch
        {
            "name" => SortField.Name,
            "version" => SortField.Version,
            "age" => SortField.Age,
            "last-modified" => SortField.LastModified,
            "last-downloaded" => SortField.LastDownloaded,
            _ => default
        };

        return value is "name" or "version" or "age" or "last-modified" or "last-downloaded";
    }

    private static bool TryParseSortDirection(string value, out SortDirection direction)
    {
        direction = value.ToLowerInvariant() switch
        {
            "asc" => SortDirection.Asc,
            "desc" => SortDirection.Desc,
            _ => default
        };

        return value is "asc" or "desc";
    }

    private static bool TrySplitArgument(
        string argument,
        out string optionName,
        out string? optionValue
    )
    {
        optionName = argument;
        optionValue = null;

        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        var separatorIndex = argument.IndexOf('=');

        if (separatorIndex >= 0)
        {
            optionName = argument[..separatorIndex];
            optionValue = argument[(separatorIndex + 1)..];
        }

        return true;
    }
}
