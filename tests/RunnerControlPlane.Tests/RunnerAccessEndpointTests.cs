using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RunnerControlPlane.Access;
using RunnerControlPlane.GitHub;

namespace RunnerControlPlane.Tests;

public sealed class RunnerAccessEndpointTests
{
    [Fact]
    public async Task UnauthenticatedUserCannotReadRunnerAccess()
    {
        using var factory = new RunnerControlPlaneFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/api/runner-access");
        var website = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, website.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyUserCanViewWebsiteAndConfigurationButCannotUpdateIt()
    {
        using var factory = new RunnerControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "reader");

        var website = await client.GetAsync("/");
        var configuration = await client.GetFromJsonAsync<RunnerAccessResponse>("/api/runner-access");
        var update = await client.PutAsJsonAsync(
            "/api/runner-access",
            new UpdateRunnerAccessRequest(["octo/new-repo"]));

        Assert.Equal(HttpStatusCode.OK, website.StatusCode);
        Assert.Equal("Shared Linux", configuration!.RunnerGroup);
        Assert.Equal("linux-dotnet", configuration.RunnerLabel);
        Assert.Equal(["octo/initial-repo"], configuration.Repositories);
        Assert.False(configuration.CanManage);
        Assert.False(configuration.IsRestricted);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(["octo/initial-repo"], factory.GitHubApi.Repositories);
    }

    [Fact]
    public async Task UserOutsideConfiguredGroupsCannotViewWebsiteOrConfiguration()
    {
        using var factory = new RunnerControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "outsider");

        var website = await client.GetAsync("/");
        var configuration = await client.GetAsync("/api/runner-access");

        Assert.Equal(HttpStatusCode.Forbidden, website.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, configuration.StatusCode);
    }

    [Fact]
    public async Task OperatorCanSetAnExplicitRepositoryAllowlist()
    {
        using var factory = new RunnerControlPlaneFactory(groupAllowsAllRepositories: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await client.PutAsJsonAsync(
            "/api/runner-access",
            new UpdateRunnerAccessRequest(["octo/build-a", "octo/build-b"]));
        var configuration = await response.Content.ReadFromJsonAsync<RunnerAccessResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(configuration!.CanManage);
        Assert.True(configuration.IsRestricted);
        Assert.Equal(["octo/build-a", "octo/build-b"], configuration.Repositories);
        Assert.True(factory.GitHubApi.IsRestricted);
        Assert.False(factory.GitHubApi.AllowsPublicRepositories);
        Assert.Equal(["octo/build-a", "octo/build-b"], factory.GitHubApi.Repositories);
    }

    [Fact]
    public async Task OperatorCannotAllowARepositoryOutsideTheConfiguredOrganization()
    {
        using var factory = new RunnerControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await client.PutAsJsonAsync(
            "/api/runner-access",
            new UpdateRunnerAccessRequest(["another-org/repo"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["octo/initial-repo"], factory.GitHubApi.Repositories);
        Assert.False(factory.GitHubApi.IsRestricted);
    }

    [Fact]
    public async Task OperatorCannotAllowARepositoryThatGitHubCannotResolve()
    {
        using var factory = new RunnerControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");

        var response = await client.PutAsJsonAsync(
            "/api/runner-access",
            new UpdateRunnerAccessRequest(["octo/missing-repo"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["octo/initial-repo"], factory.GitHubApi.Repositories);
        Assert.False(factory.GitHubApi.IsRestricted);
    }

    [Fact]
    public async Task FailedRepositoryUpdateDoesNotApplyRunnerGroupSettings()
    {
        using var factory = new RunnerControlPlaneFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        factory.GitHubApi.FailNextRepositoryUpdate = true;

        var response = await client.PutAsJsonAsync(
            "/api/runner-access",
            new UpdateRunnerAccessRequest(["octo/build-a"]));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(["octo/initial-repo"], factory.GitHubApi.Repositories);
        Assert.False(factory.GitHubApi.IsRestricted);
        Assert.True(factory.GitHubApi.AllowsPublicRepositories);
    }

    private sealed class RunnerControlPlaneFactory : WebApplicationFactory<Program>
    {
        public RunnerControlPlaneFactory(bool groupAllowsAllRepositories = false)
        {
            GitHubApi = new FakeGitHubApiHandler(groupAllowsAllRepositories);
        }

        public FakeGitHubApiHandler GitHubApi { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Entra:TenantId"] = "tenant-id",
                    ["Entra:ClientId"] = "client-id",
                    ["Entra:ClientSecret"] = "client-secret",
                    ["Entra:ReadOnlyGroupId"] = "reader-group",
                    ["Entra:OperatorGroupId"] = "operator-group",
                    ["GitHub:Organization"] = "octo",
                    ["GitHub:Token"] = "test-token",
                    ["GitHub:RunnerGroupId"] = "42",
                    ["Runner:Label"] = "linux-dotnet"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IGitHubRunnerGroupClient>();
                services.AddHttpClient<IGitHubRunnerGroupClient, GitHubRunnerGroupClient>()
                    .ConfigureHttpClient(client => client.BaseAddress = new Uri("https://api.github.test/"))
                    .ConfigurePrimaryHttpMessageHandler(() => GitHubApi);
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.TestSchemeName,
                    _ => { });
                services.PostConfigure<AuthenticationOptions>(options =>
                {
                    options.DefaultScheme = TestAuthenticationHandler.TestSchemeName;
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.TestSchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.TestSchemeName;
                    options.DefaultForbidScheme = TestAuthenticationHandler.TestSchemeName;
                });
            });
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string TestSchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (string.IsNullOrEmpty(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = role switch
            {
                "reader" => new[] { new Claim("groups", "reader-group") },
                "operator" => new[] { new Claim("groups", "operator-group") },
                _ => new[] { new Claim("groups", "unconfigured-group") }
            };
            var identity = new ClaimsIdentity(claims, TestSchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, TestSchemeName);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class FakeGitHubApiHandler : HttpMessageHandler
    {
        public FakeGitHubApiHandler(bool groupAllowsAllRepositories)
        {
            _visibility = groupAllowsAllRepositories ? "all" : "selected";
        }

        private readonly Dictionary<long, (string FullName, string Name)> _knownRepositories = new()
        {
            [1] = ("octo/build-a", "build-a"),
            [2] = ("octo/build-b", "build-b"),
            [7] = ("octo/initial-repo", "initial-repo")
        };
        private List<long> _selectedRepositoryIds = [7];
        private string _visibility;

        public IReadOnlyList<string> Repositories => _selectedRepositoryIds
            .Select(id => _knownRepositories[id].FullName)
            .ToArray();

        public bool IsRestricted => _visibility == "selected" && !AllowsPublicRepositories;

        public bool AllowsPublicRepositories { get; private set; } = true;

        public bool FailNextRepositoryUpdate { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/');
            if (request.Method == HttpMethod.Get && path == "orgs/octo/actions/runner-groups/42")
            {
                return Json(new
                {
                    name = "Shared Linux",
                    visibility = _visibility,
                    allows_public_repositories = AllowsPublicRepositories
                });
            }

            if (request.Method == HttpMethod.Get
                && path == "orgs/octo/actions/runner-groups/42/repositories")
            {
                return Json(new
                {
                    total_count = _selectedRepositoryIds.Count,
                    repositories = _selectedRepositoryIds.Select(id => new
                    {
                        id,
                        full_name = _knownRepositories[id].FullName
                    })
                });
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("repos/octo/", StringComparison.Ordinal))
            {
                var name = path["repos/octo/".Length..];
                var entry = _knownRepositories.FirstOrDefault(candidate =>
                    candidate.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                return entry.Equals(default(KeyValuePair<long, (string FullName, string Name)>))
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : Json(new { id = entry.Key, full_name = entry.Value.FullName });
            }

            if (request.Method.Method == "PATCH" && path == "orgs/octo/actions/runner-groups/42")
            {
                using var body = await JsonDocument.ParseAsync(
                    await request.Content!.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                _visibility = body.RootElement.GetProperty("visibility").GetString()!;
                AllowsPublicRepositories = body.RootElement
                    .GetProperty("allows_public_repositories")
                    .GetBoolean();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method == HttpMethod.Put
                && path == "orgs/octo/actions/runner-groups/42/repositories")
            {
                if (FailNextRepositoryUpdate)
                {
                    FailNextRepositoryUpdate = false;
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                using var body = await JsonDocument.ParseAsync(
                    await request.Content!.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
                _selectedRepositoryIds = body.RootElement
                    .GetProperty("selected_repository_ids")
                    .EnumerateArray()
                    .Select(value => value.GetInt64())
                    .ToList();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json<T>(T value)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(value)
            };
        }
    }
}
