using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using RunnerControlPlane.Access;
using RunnerControlPlane.Configuration;
using RunnerControlPlane.GitHub;
using RunnerControlPlane.Images;
using RunnerControlPlane.RunnerCapacity;
using RunnerControlPlane.RunnerVms;
using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<EntraOptions>()
    .BindConfiguration("Entra")
    .Validate(options => !string.IsNullOrWhiteSpace(options.TenantId), "Entra:TenantId is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ClientId), "Entra:ClientId is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ClientSecret), "Entra:ClientSecret is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ReadOnlyGroupId), "Entra:ReadOnlyGroupId is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.OperatorGroupId), "Entra:OperatorGroupId is required.")
    .Validate(
        options => string.IsNullOrWhiteSpace(options.ReadOnlyGroupId)
            || string.IsNullOrWhiteSpace(options.OperatorGroupId)
            || !options.ReadOnlyGroupId.Equals(options.OperatorGroupId, StringComparison.OrdinalIgnoreCase),
        "Entra:ReadOnlyGroupId and Entra:OperatorGroupId must be different group object IDs.")
    .ValidateOnStart();

builder.Services
    .AddOptions<GitHubOptions>()
    .BindConfiguration("GitHub")
    .Validate(options =>
            !string.IsNullOrWhiteSpace(options.Organization)
            && !string.IsNullOrWhiteSpace(options.Token)
            && options.RunnerGroupId > 0
            && Uri.TryCreate(options.ApiBaseUrl, UriKind.Absolute, out var apiBaseUri)
            && apiBaseUri.Scheme == Uri.UriSchemeHttps,
        "A GitHub organization, token, positive runner group ID, and valid API URL must be configured.")
    .ValidateOnStart();

builder.Services
    .AddOptions<RunnerOptions>()
    .BindConfiguration("Runner")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Label), "A stable runner label must be configured.")
    .ValidateOnStart();

builder.Services
    .AddOptions<RunnerCapacityOptions>()
    .BindConfiguration("RunnerCapacity")
    .Validate(options => options.MinimumCapacity >= 0, "RunnerCapacity:MinimumCapacity must be at least zero.")
    .Validate(options => options.MaximumCapacity > 0, "RunnerCapacity:MaximumCapacity must be greater than zero.")
    .Validate(
        options => options.MinimumCapacity <= options.MaximumCapacity,
        "RunnerCapacity:MinimumCapacity must not exceed RunnerCapacity:MaximumCapacity.")
    .Validate(
        options => options.ReconcileIntervalSeconds > 0,
        "RunnerCapacity:ReconcileIntervalSeconds must be greater than zero.")
    .Validate(
        options => options.IdleGracePeriodSeconds >= 0,
        "RunnerCapacity:IdleGracePeriodSeconds must be zero or greater.")
    .ValidateOnStart();

builder.Services
    .AddOptions<RunnerVmOptions>()
    .BindConfiguration("RunnerVm")
    .Validate(
        options => options.HealthFailureThreshold >= 2,
        "RunnerVm:HealthFailureThreshold must be at least two.")
    .ValidateOnStart();

builder.Services
    .AddOptions<AzureVmScaleSetOptions>()
    .BindConfiguration("AzureVmScaleSet")
    .Validate(
        options => !options.Enabled
            || (Guid.TryParse(options.SubscriptionId, out _)
                && !string.IsNullOrWhiteSpace(options.ResourceGroupName)
                && !string.IsNullOrWhiteSpace(options.ScaleSetName)),
        "Azure VM Scale Set subscription, resource group, and scale set names are required when enabled.")
    .ValidateOnStart();

builder.Services
    .AddOptions<RunnerImageBuildOptions>()
    .BindConfiguration("RunnerImageBuild");

builder.Services.AddHttpClient<IGitHubRunnerGroupClient, GitHubRunnerGroupClient>((services, client) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubOptions>>().Value;
    client.BaseAddress = new Uri(options.ApiBaseUrl.EndsWith('/') ? options.ApiBaseUrl : $"{options.ApiBaseUrl}/");
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("gh-self-hosted-runners-control-plane");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
});

builder.Services.AddHttpClient<IGitHubRunnerCapacityClient, GitHubRunnerCapacityClient>((services, client) =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<GitHubOptions>>().Value;
    client.BaseAddress = new Uri(options.ApiBaseUrl.EndsWith('/') ? options.ApiBaseUrl : $"{options.ApiBaseUrl}/");
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("gh-self-hosted-runners-control-plane");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
});

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IRunnerImageStore, InMemoryRunnerImageStore>();
builder.Services.AddSingleton<IRunnerVmStore, InMemoryRunnerVmStore>();
builder.Services.AddSingleton<IRunnerVmRecoveryService, RunnerVmRecoveryService>();
builder.Services.AddSingleton<IRunnerVmCleanupService, RunnerVmCleanupService>();
builder.Services.AddHostedService<RunnerVmCleanupWorker>();
if (builder.Configuration.GetValue<bool>("AzureVmScaleSet:Enabled"))
{
    builder.Services.AddSingleton<IAzureVmScaleSetClient, AzureVmScaleSetClient>();
}
else
{
    builder.Services.AddSingleton<IAzureVmScaleSetClient, InMemoryAzureVmScaleSetClient>();
}
builder.Services.AddSingleton<IRunnerCapacityStateStore, JsonRunnerCapacityStateStore>();
builder.Services.AddSingleton<IRunnerCapacityController, RunnerCapacityController>();
builder.Services.AddHostedService<RunnerCapacityReconcileWorker>();
builder.Services.AddSingleton<IRunnerImageBuilder, UnconfiguredRunnerImageBuilder>();
builder.Services.AddSingleton<IRunnerImageValidator, UnconfiguredRunnerImageValidator>();
builder.Services.AddSingleton<RunnerImageBuildQueue>();
builder.Services.AddSingleton<IRunnerImageBuildQueue>(services =>
    services.GetRequiredService<RunnerImageBuildQueue>());
builder.Services.AddHostedService(services => services.GetRequiredService<RunnerImageBuildQueue>());
builder.Services.AddHostedService<RunnerImageScheduleWorker>();

const string requestAuthenticationScheme = "request";
const string entraAuthenticationScheme = "entra";

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = requestAuthenticationScheme;
        options.DefaultAuthenticateScheme = requestAuthenticationScheme;
        options.DefaultChallengeScheme = requestAuthenticationScheme;
        options.DefaultForbidScheme = requestAuthenticationScheme;
    })
    .AddPolicyScheme(requestAuthenticationScheme, "Browser cookie or API bearer token", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api")
                && AuthenticationHeaderValue.TryParse(
                    context.Request.Headers.Authorization,
                    out var authorization)
                && authorization.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                return JwtBearerDefaults.AuthenticationScheme;
            }

            return CookieAuthenticationDefaults.AuthenticationScheme;
        };
    })
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    })
    .AddOpenIdConnect(entraAuthenticationScheme, _ => { })
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, _ => { });

builder.Services
    .AddOptions<OpenIdConnectOptions>(entraAuthenticationScheme)
    .Configure<Microsoft.Extensions.Options.IOptions<EntraOptions>>((options, entraOptions) =>
    {
        var entra = entraOptions.Value;
        options.Authority = $"https://login.microsoftonline.com/{entra.TenantId}/v2.0";
        options.ClientId = entra.ClientId;
        options.ClientSecret = entra.ClientSecret;
        options.ResponseType = "code";
        options.CallbackPath = "/signin-oidc";
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.TokenValidationParameters.NameClaimType = "name";
    });

builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<Microsoft.Extensions.Options.IOptions<EntraOptions>>((options, entraOptions) =>
    {
        var entra = entraOptions.Value;
        options.Authority = $"https://login.microsoftonline.com/{entra.TenantId}/v2.0";
        options.Audience = string.IsNullOrWhiteSpace(entra.ApiAudience)
            ? entra.ClientId
            : entra.ApiAudience;
        options.MapInboundClaims = false;
        options.TokenValidationParameters.NameClaimType = "name";
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AccessPolicies.CanView, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            if (context.Resource is not HttpContext httpContext)
            {
                return false;
            }

            var entraOptions = httpContext.RequestServices
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<EntraOptions>>().Value;
            return HasGroup(context.User, entraOptions.ReadOnlyGroupId)
                || HasGroup(context.User, entraOptions.OperatorGroupId);
        });
    });
    options.AddPolicy(AccessPolicies.CanManage, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            if (context.Resource is not HttpContext httpContext)
            {
                return false;
            }

            var entraOptions = httpContext.RequestServices
                .GetRequiredService<Microsoft.Extensions.Options.IOptions<EntraOptions>>().Value;
            return HasGroup(context.User, entraOptions.OperatorGroupId);
        });
    });
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Content(RunnerAccessPage.Html, "text/html; charset=utf-8"))
    .RequireAuthorization(AccessPolicies.CanView);

app.MapGet("/login", () =>
{
    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/" },
        [entraAuthenticationScheme]);
}).AllowAnonymous();

app.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignOutAsync(
        entraAuthenticationScheme,
        new AuthenticationProperties { RedirectUri = "/" });
}).AllowAnonymous();

app.MapGet("/access-denied", () =>
        Results.Content("<h1>Access denied</h1>", "text/html; charset=utf-8", statusCode: StatusCodes.Status403Forbidden))
    .AllowAnonymous();

app.MapGet("/api/runner-access", async (
        ClaimsPrincipal user,
        IGitHubRunnerGroupClient github,
        Microsoft.Extensions.Options.IOptions<EntraOptions> entraOptions,
        Microsoft.Extensions.Options.IOptions<RunnerOptions> runnerOptions,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var group = await github.GetAccessAsync(cancellationToken);
            return Results.Ok(ToResponse(user, group, entraOptions.Value, runnerOptions.Value));
        }
        catch (GitHubApiException)
        {
            return GitHubFailure();
        }
        catch (HttpRequestException)
        {
            return GitHubFailure();
        }
    })
    .RequireAuthorization(AccessPolicies.CanView);

app.MapPut("/api/runner-access", async (
        UpdateRunnerAccessRequest? request,
        ClaimsPrincipal user,
        IGitHubRunnerGroupClient github,
        Microsoft.Extensions.Options.IOptions<GitHubOptions> githubOptions,
        Microsoft.Extensions.Options.IOptions<EntraOptions> entraOptions,
        Microsoft.Extensions.Options.IOptions<RunnerOptions> runnerOptions,
        CancellationToken cancellationToken) =>
    {
        if (request?.Repositories is null)
        {
            return Results.BadRequest(new { error = "Provide a repositories array." });
        }

        var validationError = ValidateRepositories(request.Repositories, githubOptions.Value.Organization);
        if (validationError is not null)
        {
            return Results.BadRequest(new { error = validationError });
        }

        try
        {
            var group = await github.SetRepositoriesAsync(request.Repositories, cancellationToken);
            return Results.Ok(ToResponse(user, group, entraOptions.Value, runnerOptions.Value));
        }
        catch (InvalidRepositoryOrganizationException)
        {
            return Results.BadRequest(new { error = "Every repository must belong to the configured organization." });
        }
        catch (GitHubRepositoryNotFoundException)
        {
            return Results.BadRequest(new { error = "Every repository must exist in the configured organization and be accessible to GitHub." });
        }
        catch (GitHubApiException)
        {
            return GitHubFailure();
        }
        catch (HttpRequestException)
        {
            return GitHubFailure();
        }
    })
    .RequireAuthorization(AccessPolicies.CanManage);

app.MapGet("/api/runner-images/available", (IRunnerImageStore images) =>
        Results.Ok(images.ListAvailable()))
    .RequireAuthorization(AccessPolicies.CanView);

app.MapGet("/api/runner-capacity", async (
        IRunnerCapacityController capacity,
        CancellationToken cancellationToken) =>
    {
        try
        {
            return Results.Ok(await capacity.GetSnapshotAsync(cancellationToken));
        }
        catch (RunnerCapacityIntegrationException)
        {
            return RunnerCapacityFailure();
        }
    })
    .RequireAuthorization(AccessPolicies.CanView);

app.MapGet("/api/runner-capacity/audit", (IRunnerCapacityController capacity) =>
        Results.Ok(capacity.GetAuditTrail()))
    .RequireAuthorization(AccessPolicies.CanView);

app.MapPut("/api/runner-capacity/settings", async (
        UpdateCapacitySettingsRequest? request,
        ClaimsPrincipal user,
        IRunnerCapacityController capacity,
        CancellationToken cancellationToken) =>
    {
        if (request is null
            || request.MinimumCapacity < 0
            || request.MaximumCapacity < 1
            || request.MinimumCapacity > request.MaximumCapacity)
        {
            return Results.BadRequest(new { error = "Provide a minimum of zero or greater and a maximum greater than or equal to the minimum." });
        }

        try
        {
            var updated = await capacity.UpdateSettingsAsync(
                request.MinimumCapacity,
                request.MaximumCapacity,
                GetActor(user),
                cancellationToken);
            return Results.Ok(updated);
        }
        catch (RunnerCapacityIntegrationException)
        {
            return RunnerCapacityFailure();
        }
    })
    .RequireAuthorization(AccessPolicies.CanManage);

app.MapPost("/api/runner-capacity/scale-out", async (
        ScaleOutRequest? request,
        ClaimsPrincipal user,
        IRunnerCapacityController capacity,
        CancellationToken cancellationToken) =>
    {
        if (request is null || request.Count < 1)
        {
            return Results.BadRequest(new { error = "Request at least one Runner VM." });
        }

        try
        {
            return Results.Ok(await capacity.RequestScaleOutAsync(
                request.Count,
                GetActor(user),
                cancellationToken));
        }
        catch (RunnerCapacityIntegrationException)
        {
            return RunnerCapacityFailure();
        }
    })
    .RequireAuthorization(AccessPolicies.CanManage);

app.MapGet("/api/runner-images", (IRunnerImageStore images) =>
        Results.Ok(images.List()))
    .RequireAuthorization(AccessPolicies.CanView);

app.MapGet("/api/runner-images/{version}", (string version, IRunnerImageStore images) =>
    {
        var image = images.Get(version);
        return image is null ? Results.NotFound() : Results.Ok(image);
    })
    .RequireAuthorization(AccessPolicies.CanView);

app.MapPost("/api/runner-images", (
        RunnerImageBuildRequest? request,
        IRunnerImageBuildQueue queue) =>
    {
        var validationError = RunnerImageRequestValidator.Validate(request);
        if (validationError is not null)
        {
            return Results.BadRequest(new { error = validationError });
        }

        var image = queue.Queue(request!);
        return Results.Accepted($"/api/runner-images/{image.Version}", image);
    })
    .RequireAuthorization(AccessPolicies.CanManage);

app.MapGet("/api/runner-vm", (IRunnerVmStore runnerVms) => Results.Ok(runnerVms.List()))
    .RequireAuthorization(AccessPolicies.CanView);
app.MapGet("/api/runner-vms", (IRunnerVmStore runnerVms) => Results.Ok(runnerVms.List()))
    .RequireAuthorization(AccessPolicies.CanView);
app.MapGet("/api/runner-vm/{id}", (string id, IRunnerVmStore runnerVms) =>
    {
        var vm = runnerVms.Get(id);
        return vm is null ? Results.NotFound() : Results.Ok(vm);
    })
    .RequireAuthorization(AccessPolicies.CanView);
app.MapGet("/api/runner-vm/audit", (IRunnerVmStore runnerVms) => Results.Ok(runnerVms.GetAuditTrail()))
    .RequireAuthorization(AccessPolicies.CanView);
app.MapGet("/api/runner-vms/audit", (IRunnerVmStore runnerVms) => Results.Ok(runnerVms.GetAuditTrail()))
    .RequireAuthorization(AccessPolicies.CanView);
app.MapGet("/api/runner-vms/{id}", (string id, IRunnerVmStore runnerVms) =>
    {
        var vm = runnerVms.Get(id);
        return vm is null ? Results.NotFound() : Results.Ok(vm);
    })
    .RequireAuthorization(AccessPolicies.CanView);

app.MapPost("/api/runner-vms/{id}/health-checks", async (
        string id,
        RunnerVmHealthCheckRequest? request,
        IRunnerVmRecoveryService recovery,
        CancellationToken cancellationToken) =>
    {
        if (request is null)
        {
            return Results.BadRequest(new { error = "Provide VM-health and runner-connectivity results." });
        }

        try
        {
            var vm = await recovery.RecordHealthCheckAsync(id, request, cancellationToken);
            return vm is null ? Results.NotFound() : Results.Ok(vm);
        }
        catch (RunnerCapacityIntegrationException)
        {
            return RunnerCapacityFailure();
        }
    })
    .RequireAuthorization(AccessPolicies.CanManage);

app.MapPost("/api/runner-vm", (CreateRunnerVmRequest? request, IRunnerVmStore runnerVms, IRunnerImageStore images, IOptions<RunnerOptions> runnerOptions) =>
    {
        var selectedImage = GetRequestedImageVersion(request, images);
        if (selectedImage is null)
        {
            return Results.BadRequest(new { error = "No validated runner image is available." });
        }

        var vm = runnerVms.Create(selectedImage, runnerOptions.Value.Label, "Shared Linux");
        return Results.Accepted($"/api/runner-vms/{vm.Id}", vm);
    })
    .RequireAuthorization(AccessPolicies.CanManage);

app.MapPost("/api/runner-vms", (CreateRunnerVmRequest? request, IRunnerVmStore runnerVms, IRunnerImageStore images, IOptions<RunnerOptions> runnerOptions) =>
    {
        var selectedImage = GetRequestedImageVersion(request, images);
        if (selectedImage is null)
        {
            return Results.BadRequest(new { error = "No validated runner image is available." });
        }

        var vm = runnerVms.Create(selectedImage, runnerOptions.Value.Label, "Shared Linux");
        return Results.Accepted($"/api/runner-vms/{vm.Id}", vm);
    })
    .RequireAuthorization(AccessPolicies.CanManage);

app.MapPatch("/api/runner-vm/{id}", UpdateRunnerVmAsync)
    .RequireAuthorization(AccessPolicies.CanManage);
app.MapPatch("/api/runner-vms/{id}", UpdateRunnerVmAsync)
    .RequireAuthorization(AccessPolicies.CanManage);

app.MapPatch("/api/runner-vm/{id}/status", UpdateRunnerVmStatusAsync)
    .RequireAuthorization(AccessPolicies.CanManage);
app.MapPatch("/api/runner-vms/{id}/status", UpdateRunnerVmStatusAsync)
    .RequireAuthorization(AccessPolicies.CanManage);

app.MapPost("/api/runner-vm/{id}/retire", (string id, IRunnerVmStore runnerVms, string? outcome = "Success", string? error = null, string? actor = null, ClaimsPrincipal user = null!) =>
    {
        var vm = runnerVms.Get(id);
        if (vm is null)
        {
            return Results.NotFound();
        }

        if (vm.Status != RunnerVmStatus.Idle)
        {
            return Results.Conflict(new { error = "Only idle Runner VMs can be retired manually." });
        }

        var completed = runnerVms.Retire(id, outcome ?? "Success", string.IsNullOrWhiteSpace(actor) ? GetActor(user) : actor, error, "manual-retire");
        return Results.Ok(completed);
    })
    .RequireAuthorization(AccessPolicies.CanManage);

app.MapPost("/api/runner-vms/{id}/retire", (string id, IRunnerVmStore runnerVms, string? outcome = "Success", string? error = null, string? actor = null, ClaimsPrincipal user = null!) =>
    {
        var vm = runnerVms.Get(id);
        if (vm is null)
        {
            return Results.NotFound();
        }

        if (vm.Status != RunnerVmStatus.Idle)
        {
            return Results.Conflict(new { error = "Only idle Runner VMs can be retired manually." });
        }

        var completed = runnerVms.Retire(id, outcome ?? "Success", string.IsNullOrWhiteSpace(actor) ? GetActor(user) : actor, error, "manual-retire");
        return Results.Ok(completed);
    })
    .RequireAuthorization(AccessPolicies.CanManage);

app.MapPost("/api/runner-vms/{id}/force-remove", async (
        string id,
        ClaimsPrincipal user,
        IRunnerVmRecoveryService recovery,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var vm = await recovery.ForceRemoveAsync(id, GetActor(user), cancellationToken);
            return vm is null ? Results.NotFound() : Results.Ok(vm);
        }
        catch (RunnerCapacityIntegrationException)
        {
            return RunnerCapacityFailure();
        }
    })
    .RequireAuthorization(AccessPolicies.CanManage);

app.Run();

static async Task<IResult> UpdateRunnerVmAsync(
    string id,
    UpdateRunnerVmStatusRequest? request,
    IRunnerVmRecoveryService recovery,
    CancellationToken cancellationToken)
{
    if (request is null)
    {
        return Results.BadRequest(new { error = "Provide a status update." });
    }

    if (request.IsRetired is not null)
    {
        return Results.BadRequest(new { error = "Use a Runner VM retirement action to change retirement state." });
    }

    try
    {
        var updated = await recovery.UpdateAsync(id, request, cancellationToken);
        return updated is null ? Results.NotFound() : Results.Ok(updated);
    }
    catch (RunnerVmNotAcceptingJobsException)
    {
        return Results.Conflict(new { error = "The Runner VM is not accepting new work." });
    }
    catch (RunnerCapacityIntegrationException)
    {
        return RunnerCapacityFailure();
    }
}

static Task<IResult> UpdateRunnerVmStatusAsync(
    string id,
    RunnerVmStatus status,
    IRunnerVmRecoveryService recovery,
    CancellationToken cancellationToken)
{
    return UpdateRunnerVmAsync(
        id,
        new UpdateRunnerVmStatusRequest(status),
        recovery,
        cancellationToken);
}

static string? GetRequestedImageVersion(CreateRunnerVmRequest? request, IRunnerImageStore images)
{
    if (request is not null && !string.IsNullOrWhiteSpace(request.ImageVersion))
    {
        return request.ImageVersion;
    }

    return images.ListAvailable().FirstOrDefault()?.Version;
}

static string GetActor(ClaimsPrincipal user)
{
    return user.Identity?.Name
        ?? user.FindFirst("name")?.Value
        ?? user.FindFirst("preferred_username")?.Value
        ?? user.FindFirst("oid")?.Value
        ?? user.FindFirst("sub")?.Value
        ?? "unknown";
}

static bool HasGroup(ClaimsPrincipal user, string groupId)
{
    return !string.IsNullOrWhiteSpace(groupId)
        && user.FindAll("groups").Any(claim => claim.Value.Equals(groupId, StringComparison.OrdinalIgnoreCase));
}

static RunnerAccessResponse ToResponse(
    ClaimsPrincipal user,
    RunnerGroupAccess group,
    EntraOptions entraOptions,
    RunnerOptions runnerOptions)
{
    return new RunnerAccessResponse(
        group.Organization,
        group.Name,
        runnerOptions.Label,
        group.IsRestricted,
        group.Repositories,
        HasGroup(user, entraOptions.OperatorGroupId));
}

static string? ValidateRepositories(IReadOnlyList<string> repositories, string organization)
{
    if (repositories.Count > 1000)
    {
        return "At most 1000 repositories can be selected.";
    }

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var repository in repositories)
    {
        var parts = repository?.Split('/');
        if (parts is not { Length: 2 }
            || !parts[0].Equals(organization, StringComparison.OrdinalIgnoreCase))
        {
            return "Every repository must use the configured organization in the form organization/repository.";
        }

        var name = parts[1];
        if (name.Length is 0 or > 100
            || name is "." or ".."
            || name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            return "Repository names may contain only letters, numbers, periods, hyphens, and underscores.";
        }

        if (!seen.Add($"{parts[0]}/{name}"))
        {
            return "The repository allowlist cannot contain duplicates.";
        }
    }

    return null;
}

static IResult GitHubFailure()
{
    return Results.Problem(
        statusCode: StatusCodes.Status502BadGateway,
        title: "Unable to update or read the GitHub runner group.");
}

static IResult RunnerCapacityFailure()
{
    return Results.Problem(
        statusCode: StatusCodes.Status502BadGateway,
        title: "Unable to read or change runner capacity.");
}

public partial class Program;
