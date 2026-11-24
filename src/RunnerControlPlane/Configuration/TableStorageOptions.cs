namespace RunnerControlPlane.Configuration;

public sealed class TableStorageOptions
{
    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = "";
}
