using System.Diagnostics;
using System.Text.Json.Nodes;

namespace RunnerControlPlane.Tests;

public sealed class RunnerImageWorkflowTests
{
    private const string Subscription = "11111111-2222-3333-4444-555555555555";
    private static readonly string Digest = new('a', 64);
    private static readonly string Root = FindRoot();
    private static readonly string Workflow = File.ReadAllText(Path.Combine(Root, ".github/workflows/runner-image-build.yml"));

    private const string Mocks = """
        az() {
          if [[ "$1 $2" == "webapp show" ]]; then
            [[ "${MOCK_AZ_FAILURE:-}" != yes ]] || return 1
            printf '%s\n' "$MOCK_APP"
          elif [[ "$1 $2" == "account get-access-token" ]]; then
            echo TOKEN_REQUESTED >&2
            echo test-token
          else
            return 1
          fi
        }
        jq() {
          if [[ "${*: -1}" == "$RUNNER_TEMP/runner-image-resolved.json" ]]; then
            if [[ "$*" == *".request"* ]]; then echo '{}'; else echo "$MOCK_EXPECTED_DIGEST"; fi
          else
            command jq "$@"
          fi
        }
        curl() {
          [[ "$*" == *"--proto =https"* && "$*" != *"--location"* ]] || return 1
          printf 'CURL_URL=%s\n' "${*: -1}" >&2
          if [[ "$*" == *"--request POST"* ]]; then
            [[ "$*" == *"--header Authorization:"* && "$*" == *"Bearer "* && "$*" == *"test-token"* ]] || return 1
            printf '{"buildId":"test-build","manifestDigest":"%s"}\n%s' "$MOCK_DIGEST" "$MOCK_POST_STATUS"
          else
            [[ "$*" == *"--oauth2-bearer test-token"* ]] || return 1
            printf '%s' "$MOCK_GET_STATUS"
          fi
        }
        """;

    [Fact]
    public void WorkflowIsRestrictedToMainAndProduction()
    {
        Assert.Contains("if: github.ref == 'refs/heads/main'", Workflow);
        Assert.Contains("environment: production", Workflow);
        Assert.DoesNotContain("RUNNER_IMAGE_API_URL", Workflow);
    }

    [Theory]
    [InlineData("200")]
    [InlineData("403")]
    public async Task ValidResourceUsesOnlyItsNormalizedDefaultHostname(string readinessStatus)
    {
        var result = await RunAsync(overrides: new() { ["MOCK_GET_STATUS"] = readinessStatus });
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Equal(2, Count(result.Error, "CURL_URL=https://myapp-123.westeurope-01.azurewebsites.net/api/runner-images"));
        Assert.Equal(1, Count(result.Error, "TOKEN_REQUESTED"));
    }

    [Theory]
    [InlineData("id", "/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/example/providers/Microsoft.Web/sites/myapp/slots/staging")]
    [InlineData("id", "/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/example/providers/Microsoft.Web/sites/other")]
    [InlineData("type", "Microsoft.Web/sites/slots")]
    [InlineData("state", "Stopped")]
    [InlineData("defaultHostName", "")]
    [InlineData("defaultHostName", "evil.example")]
    [InlineData("defaultHostName", "myapp.azurewebsites.net.evil.example")]
    [InlineData("defaultHostName", "myapp.azurewebsites.net@evil.example")]
    [InlineData("defaultHostName", "https://myapp.azurewebsites.net")]
    [InlineData("defaultHostName", "myapp.azurewebsites.net/path")]
    [InlineData("defaultHostName", "myapp.azurewebsites.net:443")]
    public async Task InvalidResourceNeverReceivesToken(string property, string value)
    {
        var app = Resource();
        app[property] = value;
        var result = await RunAsync(app);
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("TOKEN_REQUESTED", result.Error);
        Assert.DoesNotContain("CURL_URL=", result.Error);
    }

    [Theory]
    [InlineData("AZURE_RESOURCE_GROUP", "")]
    [InlineData("AZURE_WEBAPP_NAME", "../other")]
    [InlineData("AZURE_SUBSCRIPTION_ID", "invalid")]
    [InlineData("RUNNER_IMAGE_API_SCOPE", "")]
    [InlineData("MOCK_AZ_FAILURE", "yes")]
    public async Task InvalidSettingsOrAzureFailureNeverRequestsToken(string name, string value)
    {
        var result = await RunAsync(overrides: new() { [name] = value });
        Assert.NotEqual(0, result.ExitCode);
        Assert.DoesNotContain("TOKEN_REQUESTED", result.Error);
        Assert.DoesNotContain("CURL_URL=", result.Error);
    }

    [Theory]
    [InlineData("302")]
    [InlineData("401")]
    [InlineData("500")]
    public async Task ReadinessFailureDoesNotRegister(string status)
    {
        var result = await RunAsync(overrides: new() { ["MOCK_GET_STATUS"] = status });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(1, Count(result.Error, "CURL_URL="));
    }

    [Theory]
    [InlineData("MOCK_POST_STATUS", "302")]
    [InlineData("MOCK_DIGEST", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task RegistrationRejectsRedirectOrMismatchedDigest(string name, string value)
    {
        var result = await RunAsync(overrides: new() { [name] = value });
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(2, Count(result.Error, "CURL_URL="));
    }

    private static JsonObject Resource() => new()
    {
        ["id"] = $"/subscriptions/{Subscription}/resourceGroups/example/providers/Microsoft.Web/sites/myapp",
        ["type"] = "Microsoft.Web/sites",
        ["state"] = "Running",
        ["defaultHostName"] = "MyApp-123.WestEurope-01.AzureWebsites.Net"
    };

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        JsonObject? app = null, Dictionary<string, string>? overrides = null)
    {
        var script = Workflow.Split("        run: |\n", StringSplitOptions.None)[^1];
        script = string.Join('\n', script.Split('\n').Select(line =>
            line.StartsWith("          ", StringComparison.Ordinal) ? line[10..] : line));
        var start = new ProcessStartInfo("bash")
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(Mocks + "\n" + script);
        var settings = new Dictionary<string, string>
        {
            ["AZURE_RESOURCE_GROUP"] = "example",
            ["AZURE_WEBAPP_NAME"] = "myapp",
            ["AZURE_SUBSCRIPTION_ID"] = Subscription,
            ["RUNNER_IMAGE_API_SCOPE"] = "api://example/.default",
            ["RUNNER_TEMP"] = Root,
            ["GITHUB_OUTPUT"] = "/dev/null",
            ["MOCK_APP"] = (app ?? Resource()).ToJsonString(),
            ["MOCK_EXPECTED_DIGEST"] = Digest,
            ["MOCK_DIGEST"] = Digest,
            ["MOCK_GET_STATUS"] = "403",
            ["MOCK_POST_STATUS"] = "201"
        };
        foreach (var (name, value) in settings)
            start.Environment[name] = value;
        if (overrides is not null)
            foreach (var (name, value) in overrides)
                start.Environment[name] = value;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdout, await stderr);
    }

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RunnerControlPlane.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
