using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using RunnerControlPlane.Images;

namespace RunnerImageManifestResolver;

public sealed record ResolvedManifest(
    RunnerImageBuildRequest Request,
    string ManifestDigest,
    JsonElement Validation,
    JsonElement Retention);

public static partial class ManifestResolver
{
    private const string UbuntuReleasesUrl = "https://changelogs.ubuntu.com/meta-release-lts";
    private const string DotNetReleaseIndexUrl =
        "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json";
    private const string RunnerReleaseUrl = "https://api.github.com/repos/actions/runner/releases/latest";

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    public static async Task<ResolvedManifest> ResolveAsync(
        string manifestPath,
        string? metadataDirectory = null,
        CancellationToken cancellationToken = default)
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        var root = manifest.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new InvalidDataException("The manifest must declare schemaVersion 1.");
        }

        var validation = root.GetProperty("validation").Clone();
        var retention = root.GetProperty("retention").Clone();
        using var client = CreateHttpClient();
        var ubuntuMetadata = await ReadUbuntuMetadataAsync(client, metadataDirectory, cancellationToken);
        var dotNetMetadata = await ReadJsonMetadataAsync(
            client,
            metadataDirectory,
            "dotnet",
            DotNetReleaseIndexUrl,
            cancellationToken);
        var (ubuntuRelease, ubuntuCodename) = ResolveUbuntu(root.GetProperty("ubuntu"), ubuntuMetadata);
        var dotNetSdks = ResolveDotNetSdks(root.GetProperty("dotNetSdks"), dotNetMetadata);
        var packages = await ResolvePackagesAsync(
            root,
            ubuntuCodename,
            metadataDirectory,
            cancellationToken);
        var (runnerVersion, runnerSha256) = await ResolveRunnerAsync(
            client,
            root.GetProperty("runner"),
            metadataDirectory,
            cancellationToken);
        var request = new RunnerImageBuildRequest(
            ubuntuRelease,
            dotNetSdks,
            packages,
            runnerVersion,
            RunnerSha256: runnerSha256);

        var validationError = RunnerImageRequestValidator.Validate(request);
        if (validationError is not null)
        {
            throw new InvalidDataException($"The resolved build request is invalid: {validationError}");
        }

        return new ResolvedManifest(
            request,
            RunnerImageManifest.ComputeDigest(request),
            validation,
            retention);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("gh-self-hosted-runners-image-resolver", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static async Task<JsonDocument> ReadUbuntuMetadataAsync(
        HttpClient client,
        string? metadataDirectory,
        CancellationToken cancellationToken)
    {
        if (metadataDirectory is not null)
        {
            return JsonDocument.Parse(await ReadFixtureAsync(metadataDirectory, "ubuntu", cancellationToken));
        }

        var content = await GetStringAsync(client, UbuntuReleasesUrl, cancellationToken);
        var releases = new List<object>();
        foreach (var block in content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = block.Split('\n')
                .Select(line => line.Split(": ", 2, StringSplitOptions.None))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);
            var versionMatch = UbuntuVersionPattern().Match(fields.GetValueOrDefault("Version", ""));
            if (fields.GetValueOrDefault("Supported") == "1" && versionMatch.Success)
            {
                releases.Add(new
                {
                    version = versionMatch.Groups[1].Value,
                    lts = true,
                    supported = true,
                    codename = fields.GetValueOrDefault("Dist", "")
                });
            }
        }

        return JsonDocument.Parse(JsonSerializer.Serialize(new { releases }));
    }

    private static async Task<JsonDocument> ReadJsonMetadataAsync(
        HttpClient client,
        string? metadataDirectory,
        string name,
        string url,
        CancellationToken cancellationToken)
    {
        var content = metadataDirectory is null
            ? await GetStringAsync(client, url, cancellationToken)
            : await ReadFixtureAsync(metadataDirectory, name, cancellationToken);
        return JsonDocument.Parse(content);
    }

    private static (string Version, string Codename) ResolveUbuntu(JsonElement policy, JsonDocument metadata)
    {
        if (policy.GetProperty("channel").GetString() != "latest-lts")
        {
            throw new InvalidDataException("Unsupported Ubuntu channel; expected 'latest-lts'.");
        }

        var releases = metadata.RootElement.GetProperty("releases").EnumerateArray()
            .Where(release =>
                release.GetProperty("supported").GetBoolean()
                && release.GetProperty("lts").GetBoolean()
                && UbuntuVersionPattern().IsMatch(release.GetProperty("version").GetString() ?? ""))
            .Select(release => (
                Version: release.GetProperty("version").GetString()!,
                Codename: release.GetProperty("codename").GetString() ?? ""))
            .ToArray();
        if (releases.Length == 0)
        {
            throw new InvalidDataException("No supported Ubuntu LTS release could be resolved.");
        }

        var latest = releases.MaxBy(release => release.Version, StringComparer.Ordinal)!;
        if (string.IsNullOrWhiteSpace(latest.Codename))
        {
            throw new InvalidDataException("The selected Ubuntu LTS release has no codename.");
        }

        return latest;
    }

    private static string[] ResolveDotNetSdks(JsonElement channels, JsonDocument metadata)
    {
        var result = new List<string>();
        foreach (var channelElement in channels.EnumerateArray())
        {
            var channel = channelElement.GetString()
                ?? throw new InvalidDataException("A .NET SDK channel must be a string.");
            string? version;
            if (metadata.RootElement.TryGetProperty("channels", out var fixtureChannels))
            {
                version = fixtureChannels.TryGetProperty(channel, out var fixtureRelease)
                    ? fixtureRelease.GetProperty("latest-sdk").GetString()
                    : null;
            }
            else
            {
                var release = metadata.RootElement.GetProperty("releases-index").EnumerateArray()
                    .FirstOrDefault(candidate =>
                        candidate.GetProperty("channel-version").GetString() == channel);
                version = release.ValueKind == JsonValueKind.Undefined
                    ? null
                    : release.GetProperty("latest-sdk").GetString();
            }

            if (version is null || !PinnedVersionPattern().IsMatch(version))
            {
                throw new InvalidDataException(
                    $"Unable to resolve a pinned .NET SDK for channel '{channel}'.");
            }

            result.Add(version);
        }

        if (result.Count == 0 || result.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
        {
            throw new InvalidDataException("The manifest must resolve to unique .NET SDK versions.");
        }

        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<IReadOnlyDictionary<string, string>> ResolvePackagesAsync(
        JsonElement manifest,
        string ubuntuCodename,
        string? metadataDirectory,
        CancellationToken cancellationToken)
    {
        var repositories = manifest.GetProperty("repositories").EnumerateArray()
            .ToDictionary(
                repository => repository.GetProperty("id").GetString()!,
                repository => repository,
                StringComparer.Ordinal);
        using var packageMetadata = metadataDirectory is null
            ? null
            : JsonDocument.Parse(await ReadFixtureAsync(metadataDirectory, "packages", cancellationToken));
        var packages = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in manifest.GetProperty("packages").EnumerateObject())
        {
            if (!PackageNamePattern().IsMatch(package.Name))
            {
                throw new InvalidDataException($"Package '{package.Name}' has an invalid package name.");
            }

            var repositoryId = package.Value.GetString()
                ?? throw new InvalidDataException($"Package '{package.Name}' must name an approved repository.");
            if (!repositories.TryGetValue(repositoryId, out var repository))
            {
                throw new InvalidDataException(
                    $"Package '{package.Name}' references unapproved repository '{repositoryId}'.");
            }

            var versions = packageMetadata is null
                ? await QueryAptVersionsAsync(package.Name, repository, ubuntuCodename, cancellationToken)
                : ReadFixturePackageVersions(packageMetadata.RootElement, package.Name, repositoryId);
            var version = versions.Where(candidate => PinnedVersionPattern().IsMatch(candidate))
                .OrderByDescending(candidate => candidate, DebianVersionComparer.Instance)
                .FirstOrDefault();
            if (version is null)
            {
                throw new InvalidDataException(
                    $"Unable to resolve package '{package.Name}' from approved repository '{repositoryId}'.");
            }

            packages.Add(package.Name, version);
        }

        if (packages.Count == 0)
        {
            throw new InvalidDataException("The manifest must approve at least one package.");
        }

        return packages;
    }

    private static IReadOnlyList<string> ReadFixturePackageVersions(
        JsonElement metadata,
        string package,
        string repositoryId)
    {
        if (!metadata.GetProperty("packages").TryGetProperty(package, out var versions))
        {
            return [];
        }

        return versions.EnumerateArray()
            .Where(version => version.GetProperty("repository").GetString() == repositoryId)
            .Select(version => version.GetProperty("version").GetString()!)
            .ToArray();
    }

    private static async Task<IReadOnlyList<string>> QueryAptVersionsAsync(
        string package,
        JsonElement repository,
        string ubuntuCodename,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("apt-cache", $"policy {package}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start apt-cache for package '{package}'.");
        var standardOutput = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to query approved package '{package}': {standardError.Trim()}");
        }

        var uris = repository.TryGetProperty("uris", out var uriArray)
            ? uriArray.EnumerateArray().Select(value => value.GetString()!.TrimEnd('/')).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(
                [repository.GetProperty("uri").GetString()!.TrimEnd('/')],
                StringComparer.Ordinal);
        var suites = repository.GetProperty("suites").EnumerateArray()
            .Select(value => value.GetString()!.Replace("{ubuntuCodename}", ubuntuCodename, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var components = repository.GetProperty("components").EnumerateArray()
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.Ordinal);
        var versions = new List<string>();
        string? currentVersion = null;
        foreach (var line in standardOutput.Split('\n'))
        {
            var versionMatch = AptVersionLinePattern().Match(line);
            if (versionMatch.Success)
            {
                currentVersion = versionMatch.Groups[1].Value;
                continue;
            }

            var source = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (source.Length < 5
                || !int.TryParse(source[0], out _)
                || currentVersion is null
                || !Uri.TryCreate(source[1], UriKind.Absolute, out var sourceUri)
                || !uris.Contains(sourceUri.GetLeftPart(UriPartial.Path).TrimEnd('/')))
            {
                continue;
            }

            var suiteAndComponent = source[2].Split('/', 2);
            if (suiteAndComponent.Length == 2
                && suites.Contains(suiteAndComponent[0])
                && components.Contains(suiteAndComponent[1]))
            {
                versions.Add(currentVersion);
            }
        }

        return versions;
    }

    private static async Task<(string Version, string Sha256)> ResolveRunnerAsync(
        HttpClient client,
        JsonElement policy,
        string? metadataDirectory,
        CancellationToken cancellationToken)
    {
        if (policy.GetProperty("channel").GetString() != "latest"
            || policy.GetProperty("repository").GetString() != "actions/runner")
        {
            throw new InvalidDataException("The Runner policy must use actions/runner's latest release.");
        }

        using var release = await ReadJsonMetadataAsync(
            client,
            metadataDirectory,
            "runner",
            RunnerReleaseUrl,
            cancellationToken);
        var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
        var version = tag.StartsWith('v') ? tag[1..] : tag;
        if (!PinnedVersionPattern().IsMatch(version))
        {
            throw new InvalidDataException("The GitHub Actions Runner latest release has no pinned version.");
        }

        var archiveName = $"actions-runner-linux-x64-{version}.tar.gz";
        var assets = release.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        var archive = assets.FirstOrDefault(asset => asset.GetProperty("name").GetString() == archiveName);
        if (archive.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidDataException($"The GitHub Actions Runner release is missing {archiveName}.");
        }

        var checksum = archive.TryGetProperty("digest", out var digestElement)
            ? digestElement.GetString()
            : null;
        if (checksum?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
        {
            checksum = checksum["sha256:".Length..];
        }
        else
        {
            var checksumAsset = assets.FirstOrDefault(asset =>
                asset.GetProperty("name").GetString() == $"{archiveName}.sha256");
            if (checksumAsset.ValueKind == JsonValueKind.Undefined || metadataDirectory is not null)
            {
                throw new InvalidDataException(
                    $"The GitHub Actions Runner release is missing a SHA-256 checksum for {archiveName}.");
            }

            var checksumUrl = checksumAsset.GetProperty("browser_download_url").GetString()
                ?? throw new InvalidDataException("The Runner checksum asset has no download URL.");
            var checksumContent = await GetStringAsync(client, checksumUrl, cancellationToken);
            var checksumMatch = RunnerChecksumLinePattern(archiveName).Match(checksumContent);
            checksum = checksumMatch.Success
                ? checksumMatch.Groups[1].Value
                : Sha256Pattern().IsMatch(checksumContent.Trim())
                    ? checksumContent.Trim()
                    : null;
        }

        if (checksum is null || !Sha256Pattern().IsMatch(checksum))
        {
            throw new InvalidDataException("The GitHub Actions Runner release contains an invalid SHA-256 checksum.");
        }

        return (version, checksum.ToLowerInvariant());
    }

    private static async Task<string> ReadFixtureAsync(
        string directory,
        string name,
        CancellationToken cancellationToken)
    {
        return await File.ReadAllTextAsync(Path.Combine(directory, $"{name}.json"), cancellationToken);
    }

    private static async Task<string> GetStringAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Metadata request to {url} returned HTTP {(int)response.StatusCode}.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    [GeneratedRegex(@"^(\d{2}\.04)(?:\.\d+)?(?: LTS)?$", RegexOptions.CultureInvariant)]
    private static partial Regex UbuntuVersionPattern();

    [GeneratedRegex(@"^[0-9][A-Za-z0-9.+:~_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PinnedVersionPattern();

    [GeneratedRegex(@"^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageNamePattern();

    [GeneratedRegex(@"^\s+(?:\*\*\*\s+)?(\S+)\s+\d+\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex AptVersionLinePattern();

    private static Regex RunnerChecksumLinePattern(string archiveName)
    {
        return new Regex(
            $@"(?m)^([a-fA-F0-9]{{64}})\s+\*?{Regex.Escape(archiveName)}\s*$",
            RegexOptions.CultureInvariant);
    }
}

internal sealed class DebianVersionComparer : IComparer<string>
{
    public static DebianVersionComparer Instance { get; } = new();

    public int Compare(string? first, string? second)
    {
        if (ReferenceEquals(first, second))
        {
            return 0;
        }

        if (first is null)
        {
            return -1;
        }

        if (second is null)
        {
            return 1;
        }

        var left = Split(first);
        var right = Split(second);
        var comparison = ComparePart(left.Epoch, right.Epoch);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = ComparePart(left.Upstream, right.Upstream);
        return comparison != 0 ? comparison : ComparePart(left.Revision, right.Revision);
    }

    private static (string Epoch, string Upstream, string Revision) Split(string version)
    {
        var colon = version.IndexOf(':');
        var epoch = colon < 0 ? "0" : version[..colon];
        var remainder = colon < 0 ? version : version[(colon + 1)..];
        var hyphen = remainder.LastIndexOf('-');
        return hyphen < 0
            ? (epoch, remainder, "0")
            : (epoch, remainder[..hyphen], remainder[(hyphen + 1)..]);
    }

    private static int ComparePart(string first, string second)
    {
        var firstIndex = 0;
        var secondIndex = 0;
        while (firstIndex < first.Length || secondIndex < second.Length)
        {
            while ((firstIndex < first.Length && !char.IsAsciiDigit(first[firstIndex]))
                || (secondIndex < second.Length && !char.IsAsciiDigit(second[secondIndex])))
            {
                var firstOrder = CharacterOrder(firstIndex < first.Length ? first[firstIndex] : null);
                var secondOrder = CharacterOrder(secondIndex < second.Length ? second[secondIndex] : null);
                if (firstOrder != secondOrder)
                {
                    return firstOrder.CompareTo(secondOrder);
                }

                if (firstIndex < first.Length)
                {
                    firstIndex++;
                }

                if (secondIndex < second.Length)
                {
                    secondIndex++;
                }
            }

            while (firstIndex < first.Length && first[firstIndex] == '0')
            {
                firstIndex++;
            }

            while (secondIndex < second.Length && second[secondIndex] == '0')
            {
                secondIndex++;
            }

            var firstStart = firstIndex;
            var secondStart = secondIndex;
            while (firstIndex < first.Length && char.IsAsciiDigit(first[firstIndex]))
            {
                firstIndex++;
            }

            while (secondIndex < second.Length && char.IsAsciiDigit(second[secondIndex]))
            {
                secondIndex++;
            }

            var lengthComparison = (firstIndex - firstStart).CompareTo(secondIndex - secondStart);
            if (lengthComparison != 0)
            {
                return lengthComparison;
            }

            var digitComparison = first.AsSpan(firstStart, firstIndex - firstStart)
                .SequenceCompareTo(second.AsSpan(secondStart, secondIndex - secondStart));
            if (digitComparison != 0)
            {
                return digitComparison;
            }
        }

        return 0;
    }

    private static int CharacterOrder(char? character)
    {
        if (character == '~')
        {
            return -1;
        }

        if (character is null)
        {
            return 0;
        }

        if (char.IsAsciiDigit(character.Value))
        {
            return 0;
        }

        if (char.IsAsciiDigit(character.Value))
        {
            return 0;
        }

        return char.IsAsciiLetter(character.Value)
            ? character.Value
            : character.Value + 256;
    }
}
