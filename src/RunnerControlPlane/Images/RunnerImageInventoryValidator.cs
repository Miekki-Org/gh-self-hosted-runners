namespace RunnerControlPlane.Images;

public static class RunnerImageInventoryValidator
{
    public static string? Validate(RunnerImageBuildRequest request, RunnerImageInventory inventory)
    {
        if (request.DotNetSdks is null
            || request.Packages is null
            || inventory.DotNetSdks is null
            || inventory.Packages is null
            || string.IsNullOrWhiteSpace(inventory.UbuntuRelease)
            || string.IsNullOrWhiteSpace(inventory.RunnerVersion)
            || !inventory.UbuntuRelease.Equals(request.UbuntuRelease, StringComparison.OrdinalIgnoreCase)
            || !inventory.RunnerVersion.Equals(request.RunnerVersion, StringComparison.OrdinalIgnoreCase))
        {
            return "The built image did not match the pinned Ubuntu or GitHub Actions Runner version.";
        }

        var requestedSdks = new HashSet<string>(request.DotNetSdks, StringComparer.OrdinalIgnoreCase);
        var installedSdks = new HashSet<string>(inventory.DotNetSdks, StringComparer.OrdinalIgnoreCase);
        if (!requestedSdks.SetEquals(installedSdks))
        {
            return "The built image did not contain exactly the pinned .NET SDK versions.";
        }

        if (request.Packages.Count != inventory.Packages.Count)
        {
            return "The built image did not contain exactly the pinned package set.";
        }

        foreach (var package in request.Packages)
        {
            if (!inventory.Packages.TryGetValue(package.Key, out var installedVersion)
                || !installedVersion.Equals(package.Value, StringComparison.OrdinalIgnoreCase))
            {
                return $"The built image did not contain the pinned version of package '{package.Key}'.";
            }
        }

        return null;
    }
}
