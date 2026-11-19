using System.Security.Cryptography;
using System.Text.Json;

namespace RunnerControlPlane.Images;

public static class RunnerImageManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ComputeDigest(RunnerImageBuildRequest request)
    {
        var manifest = new
        {
            request.UbuntuRelease,
            DotNetSdks = request.DotNetSdks.Order(StringComparer.Ordinal).ToArray(),
            Packages = request.Packages
                .OrderBy(package => package.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(package => package.Key, package => package.Value, StringComparer.OrdinalIgnoreCase),
            request.RunnerVersion,
            request.RunnerSha256
        };
        return Convert.ToHexStringLower(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions)));
    }
}
