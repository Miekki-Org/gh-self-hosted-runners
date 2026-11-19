using System.Collections.ObjectModel;

namespace RunnerControlPlane.Images;

public static class RunnerImageSnapshot
{
    public static RunnerImageBuildRequest Freeze(RunnerImageBuildRequest request)
    {
        return request with
        {
            DotNetSdks = Array.AsReadOnly(request.DotNetSdks.ToArray()),
            Packages = FreezePackages(request.Packages)
        };
    }

    public static RunnerImageInventory Freeze(RunnerImageInventory inventory)
    {
        return inventory with
        {
            DotNetSdks = Array.AsReadOnly(inventory.DotNetSdks.ToArray()),
            Packages = FreezePackages(inventory.Packages)
        };
    }

    private static IReadOnlyDictionary<string, string> FreezePackages(
        IReadOnlyDictionary<string, string> packages)
    {
        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(packages, StringComparer.OrdinalIgnoreCase));
    }
}
