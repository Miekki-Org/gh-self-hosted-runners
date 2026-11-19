using System.Text.Json;

namespace RunnerImageManifestResolver;

public static class ResolverCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        var options = ParseArguments(args);
        if (options.Error is not null)
        {
            await error.WriteLineAsync(options.Error);
            return 2;
        }

        try
        {
            var resolved = await ManifestResolver.ResolveAsync(
                options.ManifestPath!,
                options.MetadataDirectory,
                cancellationToken);
            await output.WriteLineAsync(JsonSerializer.Serialize(resolved, ManifestResolver.JsonOptions));
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or InvalidDataException
                or HttpRequestException
                or TaskCanceledException
                or InvalidOperationException
                or KeyNotFoundException
                or ArgumentException)
        {
            await error.WriteLineAsync($"Runner image resolution failed: {exception.Message}");
            return 1;
        }
    }

    private static CommandOptions ParseArguments(IReadOnlyList<string> args)
    {
        string? manifestPath = null;
        string? metadataDirectory = null;
        for (var index = 0; index < args.Count; index++)
        {
            if (index + 1 >= args.Count)
            {
                return new(null, null, $"Missing a value for argument '{args[index]}'.");
            }

            var value = args[++index];
            switch (args[index - 1])
            {
                case "--manifest":
                    manifestPath = value;
                    break;
                case "--metadata-dir":
                    metadataDirectory = value;
                    break;
                default:
                    return new(null, null, $"Unknown argument '{args[index - 1]}'.");
            }
        }

        return string.IsNullOrWhiteSpace(manifestPath)
            ? new(null, null, "Usage: RunnerImageManifestResolver --manifest <path> [--metadata-dir <path>].")
            : new(manifestPath, metadataDirectory, null);
    }

    private sealed record CommandOptions(string? ManifestPath, string? MetadataDirectory, string? Error);
}
