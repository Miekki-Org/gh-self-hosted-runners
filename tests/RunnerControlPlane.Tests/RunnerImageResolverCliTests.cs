using System.Text.Json;
using RunnerImageManifestResolver;

namespace RunnerControlPlane.Tests;

public sealed class RunnerImageResolverCliTests
{
    private static readonly string Fixtures = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "RunnerImageResolver");

    [Fact]
    public async Task ResolvesApprovedPoliciesToAPinnedBuildRequestAndDigest()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ResolverCommand.RunAsync(
            [
                "--manifest", Path.Combine(Fixtures, "manifest.json"),
                "--metadata-dir", Path.Combine(Fixtures, "metadata")
            ],
            output,
            error);
        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        using var resolved = JsonDocument.Parse(output.ToString());
        var request = resolved.RootElement.GetProperty("request");
        Assert.Equal("24.04", request.GetProperty("ubuntuRelease").GetString());
        Assert.Equal(
            ["10.0.101"],
            request.GetProperty("dotNetSdks").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("8.5.0-2ubuntu10.7", request.GetProperty("packages").GetProperty("curl").GetString());
        Assert.Equal("2.331.0", request.GetProperty("runnerVersion").GetString());
        Assert.Equal(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            request.GetProperty("runnerSha256").GetString());
        Assert.Equal(
            "434cf311fd065b45fc73eab677a957556d661af1588060a139033e97d693180a",
            resolved.RootElement.GetProperty("manifestDigest").GetString());
        Assert.Equal(15, resolved.RootElement.GetProperty("validation").GetProperty("healthWindowMinutes").GetInt32());
        Assert.Equal(2, resolved.RootElement.GetProperty("retention").GetProperty("previousValidated").GetInt32());
    }

    [Fact]
    public async Task RepeatedResolutionProducesTheSameRequestAndDigest()
    {
        var first = await ResolveAsync();
        var second = await ResolveAsync();

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task PackageWithUnknownApprovedRepositoryFailsClearly()
    {
        var contents = (await File.ReadAllTextAsync(Path.Combine(Fixtures, "manifest.json"))).Replace(
            "\"curl\": \"ubuntu-noble-main\"",
            "\"curl\": \"unapproved-repository\"",
            StringComparison.Ordinal);
        var temporaryManifest = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(temporaryManifest, contents);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await ResolverCommand.RunAsync(
                ["--manifest", temporaryManifest, "--metadata-dir", Path.Combine(Fixtures, "metadata")],
                output,
                error);

            Assert.Equal(1, exitCode);
            Assert.Contains("unapproved-repository", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(temporaryManifest);
        }
    }

    [Fact]
    public async Task MissingDotNetChannelFailsClearly()
    {
        var metadataDirectory = CopyMetadata();
        await File.WriteAllTextAsync(
            Path.Combine(metadataDirectory, "dotnet.json"),
            """{"channels":{}}""");
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await ResolverCommand.RunAsync(
                [
                    "--manifest", Path.Combine(Fixtures, "manifest.json"),
                    "--metadata-dir", metadataDirectory
                ],
                output,
                error);

            Assert.Equal(1, exitCode);
            Assert.Contains("Unable to resolve a pinned .NET SDK for channel '10.0'", error.ToString());
        }
        finally
        {
            Directory.Delete(metadataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingPackageVersionInApprovedRepositoryFailsClearly()
    {
        var metadataDirectory = CopyMetadata();
        await File.WriteAllTextAsync(
            Path.Combine(metadataDirectory, "packages.json"),
            """{"packages":{"curl":[]}}""");
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exitCode = await ResolverCommand.RunAsync(
                [
                    "--manifest", Path.Combine(Fixtures, "manifest.json"),
                    "--metadata-dir", metadataDirectory
                ],
                output,
                error);

            Assert.Equal(1, exitCode);
            Assert.Contains(
                "Unable to resolve package 'curl' from approved repository 'ubuntu-noble-main'",
                error.ToString());
        }
        finally
        {
            Directory.Delete(metadataDirectory, recursive: true);
        }
    }

    private static async Task<string> ResolveAsync()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ResolverCommand.RunAsync(
            [
                "--manifest", Path.Combine(Fixtures, "manifest.json"),
                "--metadata-dir", Path.Combine(Fixtures, "metadata")
            ],
            output,
            error);
        Assert.Equal(0, exitCode);
        return output.ToString();
    }

    private static string CopyMetadata()
    {
        var target = Directory.CreateTempSubdirectory("runner-image-resolver-").FullName;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Fixtures, "metadata")))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return target;
    }
}
