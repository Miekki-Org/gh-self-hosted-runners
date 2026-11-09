using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RunnerControlPlane.Configuration;
using RunnerControlPlane.RunnerCapacity;

namespace RunnerControlPlane.GitHub;

public sealed class GitHubRunnerCapacityClient(
    HttpClient httpClient,
    IOptions<GitHubOptions> githubOptions,
    IOptions<RunnerOptions> runnerOptions,
    IGitHubRunnerGroupClient runnerGroups)
    : IGitHubRunnerCapacityClient
{
    private readonly GitHubOptions _github = githubOptions.Value;
    private readonly string _organizationPath = Uri.EscapeDataString(githubOptions.Value.Organization);
    private readonly string _runnerLabel = runnerOptions.Value.Label;

    public async Task<RunnerDemand> GetDemandAsync(CancellationToken cancellationToken)
    {
        try
        {
            var group = await runnerGroups.GetAccessAsync(cancellationToken);
            IReadOnlyList<string> repositories;
            if (group.AllowsAllRepositories)
            {
                repositories = await GetOrganizationRepositoriesAsync(publicOnly: false, cancellationToken);
            }
            else if (group.AllowsPublicRepositories)
            {
                repositories = group.Repositories
                    .Concat(await GetOrganizationRepositoriesAsync(publicOnly: true, cancellationToken))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            else
            {
                repositories = group.Repositories;
            }

            var queuedJobs = 0;
            foreach (var repository in repositories)
            {
                queuedJobs += await GetQueuedJobsAsync(repository, cancellationToken);
            }

            var busyRunnerNames = await GetBusyRunnerNamesAsync(cancellationToken);
            return new RunnerDemand(queuedJobs, busyRunnerNames.Count)
            {
                BusyRunnerNames = busyRunnerNames
            };
        }
        catch (GitHubApiException exception)
        {
            throw new RunnerCapacityIntegrationException("Could not read GitHub runner demand.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new RunnerCapacityIntegrationException("Could not connect to GitHub to read runner demand.", exception);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new RunnerCapacityIntegrationException("GitHub returned an invalid runner demand response.", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RunnerCapacityIntegrationException("Reading GitHub runner demand timed out.", exception);
        }
    }

    private async Task<int> GetQueuedJobsAsync(string repository, CancellationToken cancellationToken)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || !parts[0].Equals(_github.Organization, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var path = $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/actions/runs";
        var queued = 0;
        foreach (var run in await GetPagedAsync<WorkflowRun>(
                     $"{path}?status=queued",
                     response => response.WorkflowRuns,
                     response => response.TotalCount,
                     cancellationToken))
        {
            var jobsPath = $"{path}/{run.Id}/jobs";
            foreach (var job in await GetPagedAsync<WorkflowJob>(
                         jobsPath,
                         response => response.Jobs,
                         response => response.TotalCount,
                         cancellationToken))
            {
                if (job.Status.Equals("queued", StringComparison.OrdinalIgnoreCase)
                    && (job.RunnerGroupId is null || job.RunnerGroupId == _github.RunnerGroupId)
                    && job.Labels.Contains(_runnerLabel, StringComparer.OrdinalIgnoreCase))
                {
                    queued++;
                }
            }
        }

        return queued;
    }

    private async Task<IReadOnlyList<string>> GetBusyRunnerNamesAsync(CancellationToken cancellationToken)
    {
        var path = $"orgs/{_organizationPath}/actions/runners";
        var runners = await GetPagedAsync<GitHubRunner>(
            path,
            response => response.Runners,
            response => response.TotalCount,
            cancellationToken);
        return runners
            .Where(runner =>
                runner.RunnerGroupId == _github.RunnerGroupId
                && runner.Busy
                && runner.Labels.Any(label =>
                    label.Name.Equals(_runnerLabel, StringComparison.OrdinalIgnoreCase)))
            .Select(runner => runner.Name)
            .ToArray();
    }

    private async Task<IReadOnlyList<string>> GetOrganizationRepositoriesAsync(
        bool publicOnly,
        CancellationToken cancellationToken)
    {
        var repositories = await GetPagedAsync<GitHubRepository>(
            $"orgs/{_organizationPath}/repos",
            cancellationToken);
        return repositories
            .Where(repository => !publicOnly
                || repository.Visibility.Equals("public", StringComparison.OrdinalIgnoreCase))
            .Select(repository => repository.FullName)
            .ToArray();
    }

    private async Task<IReadOnlyList<TItem>> GetPagedAsync<TItem>(
        string path,
        CancellationToken cancellationToken)
    {
        var all = new List<TItem>();
        var page = 1;
        int pageCount;
        do
        {
            using var response = await httpClient.GetAsync(
                $"{path}?per_page=100&page={page}",
                cancellationToken);
            EnsureSuccess(response, path);
            var pageItems = await response.Content.ReadFromJsonAsync<List<TItem>>(
                cancellationToken: cancellationToken)
                ?? throw new GitHubApiException(response.StatusCode, $"reading {path}");
            pageCount = pageItems.Count;
            all.AddRange(pageItems);
            page++;
        }
        while (pageCount == 100);

        return all;
    }

    private async Task<IReadOnlyList<TItem>> GetPagedAsync<TItem, TResponse>(
        string path,
        Func<TResponse, List<TItem>> items,
        Func<TResponse, int> totalCount,
        CancellationToken cancellationToken)
    {
        var all = new List<TItem>();
        var page = 1;
        var total = int.MaxValue;
        while (all.Count < total)
        {
            using var response = await httpClient.GetAsync(
                $"{path}{(path.Contains('?') ? '&' : '?')}per_page=100&page={page}",
                cancellationToken);
            EnsureSuccess(response, path);
            var result = await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken)
                ?? throw new GitHubApiException(response.StatusCode, $"reading {path}");
            var pageItems = items(result);
            total = totalCount(result);
            all.AddRange(pageItems);
            if (pageItems.Count == 0 && all.Count < total)
            {
                throw new GitHubApiException(response.StatusCode, $"reading all results from {path}");
            }

            page++;
        }

        return all;
    }

    private async Task<IReadOnlyList<TItem>> GetPagedAsync<TItem>(
        string path,
        Func<GitHubPage<TItem>, List<TItem>> items,
        Func<GitHubPage<TItem>, int> totalCount,
        CancellationToken cancellationToken)
    {
        return await GetPagedAsync<TItem, GitHubPage<TItem>>(
            path,
            items,
            totalCount,
            cancellationToken);
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubApiException(response.StatusCode, $"reading {operation}");
        }
    }

    private sealed class GitHubPage<TItem>
    {
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }

        [JsonPropertyName("workflow_runs")]
        public List<TItem> WorkflowRuns { get; set; } = [];

        [JsonPropertyName("jobs")]
        public List<TItem> Jobs { get; set; } = [];

        [JsonPropertyName("runners")]
        public List<TItem> Runners { get; set; } = [];

        [JsonPropertyName("repositories")]
        public List<TItem> Repositories { get; set; } = [];
    }

    private sealed class WorkflowRun
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }
    }

    private sealed class WorkflowJob
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "";

        [JsonPropertyName("runner_group_id")]
        public long? RunnerGroupId { get; set; }

        [JsonPropertyName("labels")]
        public List<string> Labels { get; set; } = [];
    }

    private sealed class GitHubRunner
    {
        [JsonPropertyName("runner_group_id")]
        public long? RunnerGroupId { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("busy")]
        public bool Busy { get; set; }

        [JsonPropertyName("labels")]
        public List<GitHubRunnerLabel> Labels { get; set; } = [];
    }

    private sealed class GitHubRunnerLabel
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }

    private sealed class GitHubRepository
    {
        [JsonPropertyName("full_name")]
        public string FullName { get; set; } = "";

        [JsonPropertyName("visibility")]
        public string Visibility { get; set; } = "";
    }
}
