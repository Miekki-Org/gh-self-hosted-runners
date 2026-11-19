namespace RunnerImageManifestResolver;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        return ResolverCommand.RunAsync(args, Console.Out, Console.Error);
    }
}
