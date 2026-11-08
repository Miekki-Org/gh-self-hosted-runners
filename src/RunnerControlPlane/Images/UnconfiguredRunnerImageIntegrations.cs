namespace RunnerControlPlane.Images;

public sealed class UnconfiguredRunnerImageBuilder : IRunnerImageBuilder
{
    public Task<RunnerImageArtifact> BuildAsync(
        string version,
        RunnerImageBuildRequest request,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Azure runner image building is not configured.");
    }
}

public sealed class UnconfiguredRunnerImageValidator : IRunnerImageValidator
{
    public Task<RunnerImageValidationResult> ValidateAsync(
        RunnerImageArtifact image,
        CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("GitHub runner image validation is not configured.");
    }
}
