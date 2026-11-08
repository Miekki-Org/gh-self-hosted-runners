using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RunnerControlPlane.Configuration;

namespace RunnerControlPlane.GitHub;

public sealed class GitHubRunnerGroupClient(HttpClient httpClient, IOptions<GitHubOptions> options)
    : IGitHubRunnerGroupClient
{
    private readonly GitHubOptions _options = options.Value;
    private readonly string _organizationPath = Uri.EscapeDataString(options.Value.Organization);

    public async Task<RunnerGroupAccess> GetAccessAsync(CancellationToken cancellationToken)
    {
        var group = await GetRunnerGroupAsync(cancellationToken);
        var selected = group.Visibility.Equals("selected", StringComparison.OrdinalIgnoreCase)
            ? await GetSelectedRepositoriesAsync(cancellationToken)
            : [];
        var restricted = group.Visibility.Equals("selected", StringComparison.OrdinalIgnoreCase)
            && !group.AllowsPublicRepositories;

        return new RunnerGroupAccess(_options.Organization, group.Name, restricted, selected);
    }

    public async Task<RunnerGroupAccess> SetRepositoriesAsync(
        IReadOnlyList<string> repositories,
        CancellationToken cancellationToken)
    {
        var repositoryIds = new List<long>(repositories.Count);
        foreach (var repository in repositories)
        {
            var parts = repository.Split('/');
            if (parts.Length != 2
                || !parts[0].Equals(_options.Organization, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidRepositoryOrganizationException();
            }

            var url = $"repos/{_organizationPath}/{Uri.EscapeDataString(parts[1])}";
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new GitHubRepositoryNotFoundException();
            }

            var repositoryDetails = await ReadJsonAsync<GitHubRepository>(response, "resolving a repository", cancellationToken);
            if (!repositoryDetails.FullName.StartsWith(
                    $"{_options.Organization}/",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidRepositoryOrganizationException();
            }

            repositoryIds.Add(repositoryDetails.Id);
        }

        var runnerGroupPath = $"orgs/{_organizationPath}/actions/runner-groups/{_options.RunnerGroupId}";
        using (var response = await httpClient.PatchAsJsonAsync(
                   runnerGroupPath,
                   new RunnerGroupUpdate(),
                   cancellationToken))
        {
            EnsureSuccess(response, "restricting the runner group");
        }

        using (var response = await httpClient.PutAsJsonAsync(
                   $"{runnerGroupPath}/repositories",
                   new SelectedRepositoryUpdate(repositoryIds),
                   cancellationToken))
        {
            EnsureSuccess(response, "setting the runner group repository allowlist");
        }

        return await GetAccessAsync(cancellationToken);
    }

    private async Task<GitHubRunnerGroup> GetRunnerGroupAsync(CancellationToken cancellationToken)
    {
        var path = $"orgs/{_organizationPath}/actions/runner-groups/{_options.RunnerGroupId}";
        using var response = await httpClient.GetAsync(path, cancellationToken);
        return await ReadJsonAsync<GitHubRunnerGroup>(response, "loading the runner group", cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetSelectedRepositoriesAsync(CancellationToken cancellationToken)
    {
        var path = $"orgs/{_organizationPath}/actions/runner-groups/{_options.RunnerGroupId}/repositories";
        var repositories = new List<string>();
        var page = 1;
        var totalCount = int.MaxValue;

        while (repositories.Count < totalCount)
        {
            using var response = await httpClient.GetAsync(
                $"{path}?per_page=100&page={page}",
                cancellationToken);
            var result = await ReadJsonAsync<SelectedRepositoriesResponse>(
                response,
                "loading the runner group repositories",
                cancellationToken);
            totalCount = result.TotalCount;
            repositories.AddRange(result.Repositories.Select(repository => repository.FullName));
            if (result.Repositories.Count == 0 && repositories.Count < totalCount)
            {
                throw new GitHubApiException(
                    response.StatusCode,
                    "loading all runner group repositories");
            }

            page++;
        }

        return repositories;
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        EnsureSuccess(response, operation);
        try
        {
            var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
            return result ?? throw new GitHubApiException(response.StatusCode, operation);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new GitHubApiException(response.StatusCode, operation);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubApiException(response.StatusCode, operation);
        }
    }

    private sealed class GitHubRunnerGroup
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("visibility")]
        public string Visibility { get; set; } = "";

        [JsonPropertyName("allows_public_repositories")]
        public bool AllowsPublicRepositories { get; set; }
    }

    private sealed class GitHubRepository
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("full_name")]
        public string FullName { get; set; } = "";
    }

    private sealed class SelectedRepositoriesResponse
    {
        [JsonPropertyName("total_count")]
        public int TotalCount { get; set; }

        [JsonPropertyName("repositories")]
        public List<GitHubRepository> Repositories { get; set; } = [];
    }

    private sealed class RunnerGroupUpdate
    {
        [JsonPropertyName("visibility")]
        public string Visibility { get; } = "selected";

        [JsonPropertyName("allows_public_repositories")]
        public bool AllowsPublicRepositories { get; } = false;
    }

    private sealed class SelectedRepositoryUpdate(List<long> repositoryIds)
    {
        [JsonPropertyName("selected_repository_ids")]
        public IReadOnlyList<long> RepositoryIds { get; } = repositoryIds;
    }
}
