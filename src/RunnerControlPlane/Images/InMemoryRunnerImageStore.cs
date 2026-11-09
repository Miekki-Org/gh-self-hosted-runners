using System.Collections.Concurrent;

namespace RunnerControlPlane.Images;

public sealed class InMemoryRunnerImageStore : IRunnerImageStore
{
    private readonly ConcurrentDictionary<string, RunnerImage> _images = new(StringComparer.Ordinal);

    public IReadOnlyList<RunnerImage> List()
    {
        return _images.Values
            .OrderByDescending(image => image.RequestedAt)
            .ToArray();
    }

    public IReadOnlyList<RunnerImage> ListAvailable()
    {
        return List()
            .Where(image => image.Status == RunnerImageBuildStatus.Complete)
            .ToArray();
    }

    public RunnerImage? Get(string version)
    {
        return _images.GetValueOrDefault(version);
    }

    public RunnerImage AddQueued(string version, RunnerImageBuildRequest request)
    {
        var image = new RunnerImage(
            version,
            DateTimeOffset.UtcNow,
            request.Expedited,
            request,
            RunnerImageBuildStatus.Queued,
            null,
            null);
        if (!_images.TryAdd(version, image))
        {
            throw new InvalidOperationException($"Runner image version '{version}' already exists.");
        }

        return image;
    }

    public RunnerImage Add(RunnerImage image)
    {
        if (!_images.TryAdd(image.Version, image))
        {
            throw new InvalidOperationException($"Runner image version '{image.Version}' already exists.");
        }

        return image;
    }

    public void Update(string version, Func<RunnerImage, RunnerImage> update)
    {
        _images.AddOrUpdate(
            version,
            _ => throw new KeyNotFoundException($"Runner image version '{version}' was not found."),
            (_, image) => update(image));
    }
}
