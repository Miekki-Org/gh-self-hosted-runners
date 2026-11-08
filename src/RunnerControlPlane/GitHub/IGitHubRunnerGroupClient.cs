using System.Net;

namespace RunnerControlPlane.GitHub;

public interface IGitHubRunnerGroupClient
{
    Task<RunnerGroupAccess> GetAccessAsync(CancellationToken cancellationToken);

    Task<RunnerGroupAccess> SetRepositoriesAsync(
        IReadOnlyList<string> repositories,
        CancellationToken cancellationToken);
}

public sealed record RunnerGroupAccess(
    string Organization,
    string Name,
    bool IsRestricted,
    IReadOnlyList<string> Repositories);

public sealed class InvalidRepositoryOrganizationException()
    : Exception("Repository is not in the configured organization.");

public sealed class GitHubRepositoryNotFoundException()
    : Exception("Repository was not found or is not accessible.");

public sealed class GitHubApiException(HttpStatusCode statusCode, string operation)
    : Exception($"GitHub API request failed while {operation}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
