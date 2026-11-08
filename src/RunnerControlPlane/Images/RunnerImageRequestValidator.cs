namespace RunnerControlPlane.Images;

public static class RunnerImageRequestValidator
{
    public static string? Validate(RunnerImageBuildRequest? request)
    {
        if (request is null)
        {
            return "A runner image build request is required.";
        }

        if (!IsUbuntuLtsRelease(request.UbuntuRelease))
        {
            return "Provide a pinned Ubuntu LTS release such as 24.04.";
        }

        if (!IsPinnedVersion(request.RunnerVersion))
        {
            return "Provide a pinned GitHub Actions Runner version.";
        }

        if (request.DotNetSdks is null
            || request.DotNetSdks.Count == 0
            || request.DotNetSdks.Any(version => !IsPinnedVersion(version))
            || request.DotNetSdks.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.DotNetSdks.Count)
        {
            return "Provide at least one pinned .NET SDK version.";
        }

        if (request.Packages is null
            || request.Packages.Count == 0
            || request.Packages.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Packages.Count
            || request.Packages.Any(package =>
                string.IsNullOrWhiteSpace(package.Key)
                || package.Key.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '-'))
                || !IsPinnedVersion(package.Value)))
        {
            return "Provide package names with pinned versions.";
        }

        return null;
    }

    private static bool IsPinnedVersion(string? version)
    {
        return !string.IsNullOrWhiteSpace(version)
            && !version.Equals("latest", StringComparison.OrdinalIgnoreCase)
            && !version.Equals("stable", StringComparison.OrdinalIgnoreCase)
            && !version.Equals("current", StringComparison.OrdinalIgnoreCase)
            && version[0] != '-'
            && version.Any(char.IsAsciiDigit)
            && version.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '-' or '_' or ':' or '~');
    }

    private static bool IsUbuntuLtsRelease(string? release)
    {
        if (release is null)
        {
            return false;
        }

        var parts = release.Split('.');
        return parts.Length is 2 or 3
            && parts[0].Length == 2
            && int.TryParse(parts[0], out var year)
            && year % 2 == 0
            && parts[1] == "04"
            && (parts.Length == 2 || int.TryParse(parts[2], out _));
    }
}
