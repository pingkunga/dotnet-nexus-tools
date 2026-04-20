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
        Directory.CreateDirectory(options.OutputDirectory);

        var outputFileName =
            $"{options.RepositoryType ?? "RepoTypeAll"}_{options.RepositoryFormat ?? "FormatAll"}_components.json";
        var outputPath = Path.Combine(options.OutputDirectory, outputFileName);

        using var httpClient = CreateHttpClient(username, password);

        var repositories = await GetRepositoriesAsync(
            baseUrl,
            httpClient,
            options.RepositoryType,
            options.RepositoryFormat,
            CancellationToken.None
        );

        Console.WriteLine($"Total repositories fetched: {repositories.Count}");

        if (repositories.Count == 0)
        {
            await WriteResultsFileAsync(
                outputPath,
                new Dictionary<string, RepositoryCountResult>(StringComparer.OrdinalIgnoreCase),
                CancellationToken.None
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
                    CancellationToken.None
                )
        );

        await Task.WhenAll(tasks);
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
                var response = await GetFromJsonAsync<ComponentPageResponse>(
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

            await WriteResultsFileAsync(outputPath, orderedResults, cancellationToken);
        }
        finally
        {
            fileLock.Release();
        }

        var completed = incrementCompletedCount();
        Console.WriteLine($"Completed {completed}/{totalRepositories}: {repository.Name}");
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

    private static async Task WriteResultsFileAsync(
        string outputPath,
        IDictionary<string, RepositoryCountResult> results,
        CancellationToken cancellationToken
    )
    {
        // Convert dictionary to an array of objects with RepoName property
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
}

internal sealed record RepositoryInfo(string Name, string Type, string Format);

internal sealed record RepositoryCountResult(string Type, string Format, int Count);

internal sealed record RepositoryResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("format")] string Format
);

internal sealed record ComponentPageResponse(
    [property: JsonPropertyName("items")] JsonElement[] Items,
    [property: JsonPropertyName("continuationToken")] string? ContinuationToken
);

internal sealed record ParsedOptions(
    CommandLineOptions? Options,
    bool ShowHelp,
    string? ErrorMessage,
    int ExitCode = 0
);

internal sealed class CommandLineOptions
{
    public static string Usage =>
        """
        Usage:
          nexus-component-counter --url <nexus-api-url> [--type <repo-type>] [--format <repo-format>] [--concurrency <count>] [--output-dir <directory>]

        Options:
          --url            Required. Base URL for the Nexus REST API.
          --type           Optional. Filter repositories by type.
          --format         Optional. Filter repositories by format.
          --concurrency    Optional. Maximum number of repositories processed concurrently. Default: 10.
          --output-dir     Optional. Directory for the JSON results file. Default: current directory.
          -h, --help       Show this help message.

        Environment:
          NEXUS_USERNAME   Nexus username for HTTP basic auth.
          NEXUS_PASSWORD   Nexus password for HTTP basic auth.
        """;

    public required string Url { get; init; }

    public string? RepositoryType { get; init; }

    public string? RepositoryFormat { get; init; }

    public int Concurrency { get; init; } = 10;

    public string OutputDirectory { get; init; } = Directory.GetCurrentDirectory();

    public static ParsedOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new ParsedOptions(null, true, null);
        }

        string? url = null;
        string? repositoryType = null;
        string? repositoryFormat = null;
        var concurrency = 10;
        string? outputDirectory = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (argument is "-h" or "--help")
            {
                return new ParsedOptions(null, true, null);
            }

            if (!TrySplitArgument(argument, out var optionName, out var inlineValue))
            {
                return new ParsedOptions(null, false, $"Unrecognized argument '{argument}'.");
            }

            string? optionValue = inlineValue;

            if (optionValue is null)
            {
                if (index + 1 >= args.Length)
                {
                    return new ParsedOptions(null, false, $"Missing value for '{optionName}'.");
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
                    if (!int.TryParse(optionValue, out concurrency) || concurrency <= 0)
                    {
                        return new ParsedOptions(
                            null,
                            false,
                            "Concurrency must be a positive integer."
                        );
                    }

                    break;
                case "--output-dir":
                    outputDirectory = optionValue;
                    break;
                default:
                    return new ParsedOptions(null, false, $"Unrecognized argument '{optionName}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return new ParsedOptions(null, false, "The --url option is required.");
        }

        return new ParsedOptions(
            new CommandLineOptions
            {
                Url = url,
                RepositoryType = string.IsNullOrWhiteSpace(repositoryType) ? null : repositoryType,
                RepositoryFormat = string.IsNullOrWhiteSpace(repositoryFormat)
                    ? null
                    : repositoryFormat,
                Concurrency = concurrency,
                OutputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
                    ? Directory.GetCurrentDirectory()
                    : outputDirectory
            },
            false,
            null
        );
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
