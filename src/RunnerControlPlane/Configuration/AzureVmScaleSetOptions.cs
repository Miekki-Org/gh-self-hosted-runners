namespace RunnerControlPlane.Configuration;

public sealed class AzureVmScaleSetOptions
{
    public bool Enabled { get; set; }

    public string SubscriptionId { get; set; } = "";

    public string ResourceGroupName { get; set; } = "";

    public string ScaleSetName { get; set; } = "";
}
