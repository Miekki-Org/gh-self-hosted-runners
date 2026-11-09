using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using Microsoft.Extensions.Options;
using RunnerControlPlane.Configuration;

namespace RunnerControlPlane.RunnerCapacity;

public sealed class AzureVmScaleSetClient : IAzureVmScaleSetClient
{
    private readonly ArmClient _armClient;
    private readonly ResourceIdentifier _scaleSetId;

    public AzureVmScaleSetClient(IOptions<AzureVmScaleSetOptions> options)
    {
        var settings = options.Value;
        if (!Guid.TryParse(settings.SubscriptionId, out _)
            || string.IsNullOrWhiteSpace(settings.ResourceGroupName)
            || string.IsNullOrWhiteSpace(settings.ScaleSetName))
        {
            throw new InvalidOperationException(
                "AzureVmScaleSet:SubscriptionId, ResourceGroupName, and ScaleSetName must be configured.");
        }

        _armClient = new ArmClient(new DefaultAzureCredential());
        _scaleSetId = new ResourceIdentifier(
            $"/subscriptions/{settings.SubscriptionId}/resourceGroups/{Uri.EscapeDataString(settings.ResourceGroupName)}/providers/Microsoft.Compute/virtualMachineScaleSets/{Uri.EscapeDataString(settings.ScaleSetName)}");
    }

    public async Task<int> GetCapacityAsync(CancellationToken cancellationToken)
    {
        var scaleSet = _armClient.GetVirtualMachineScaleSetResource(_scaleSetId);
        try
        {
            var response = await scaleSet.GetAsync(cancellationToken: cancellationToken);
            return ToCapacity(response.Value.Data.Sku?.Capacity);
        }
        catch (RequestFailedException exception)
        {
            throw new RunnerCapacityIntegrationException(
                "Could not read Azure VM Scale Set capacity.",
                exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new RunnerCapacityIntegrationException(
                "Could not authenticate to Azure Resource Manager.",
                exception);
        }
    }

    public async Task<int> SetCapacityAsync(
        int desiredCapacity,
        IReadOnlyList<string> busyRunnerNames,
        CancellationToken cancellationToken)
    {
        if (desiredCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desiredCapacity));
        }

        var scaleSet = _armClient.GetVirtualMachineScaleSetResource(_scaleSetId);
        try
        {
            var response = await scaleSet.GetAsync(cancellationToken: cancellationToken);
            var currentCapacity = ToCapacity(response.Value.Data.Sku?.Capacity);
            if (desiredCapacity >= currentCapacity)
            {
                if (desiredCapacity == currentCapacity)
                {
                    return currentCapacity;
                }

                await scaleSet.UpdateAsync(
                    WaitUntil.Completed,
                    new VirtualMachineScaleSetPatch
                    {
                        Sku = new ComputeSku { Capacity = desiredCapacity }
                    },
                    cancellationToken);
                return desiredCapacity;
            }

            var instances = new List<(string InstanceId, string? ComputerName)>();
            await foreach (var instance in scaleSet.GetVirtualMachineScaleSetVms()
                               .GetAllAsync(cancellationToken: cancellationToken))
            {
                instances.Add((
                    instance.Data.InstanceId,
                    instance.Data.Properties?.OSProfile?.ComputerName));
            }

            if (instances.Count != currentCapacity)
            {
                return currentCapacity;
            }

            var busyNames = new HashSet<string>(busyRunnerNames, StringComparer.OrdinalIgnoreCase);
            var matchedBusyNames = instances
                .Where(instance => instance.ComputerName is not null && busyNames.Contains(instance.ComputerName))
                .Select(instance => instance.ComputerName!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!busyNames.IsSubsetOf(matchedBusyNames))
            {
                throw new RunnerCapacityIntegrationException(
                    "Scale-in was blocked because busy GitHub runners could not be matched to VM Scale Set instances.",
                    new InvalidOperationException("The runner name and VM host name must match."));
            }

            var idleInstances = instances
                .Where(instance => instance.ComputerName is null || !busyNames.Contains(instance.ComputerName))
                .Take(currentCapacity - desiredCapacity)
                .Select(instance => instance.InstanceId)
                .ToArray();
            if (idleInstances.Length == 0)
            {
                return currentCapacity;
            }

            await scaleSet.DeleteInstancesAsync(
                WaitUntil.Completed,
                new VirtualMachineScaleSetVmInstanceRequiredIds(idleInstances),
                cancellationToken: cancellationToken);
            return currentCapacity - idleInstances.Length;
        }
        catch (RequestFailedException exception)
        {
            throw new RunnerCapacityIntegrationException(
                "Could not change Azure VM Scale Set capacity.",
                exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw new RunnerCapacityIntegrationException(
                "Could not authenticate to Azure Resource Manager.",
                exception);
        }
    }

    private static int ToCapacity(long? capacity)
    {
        if (capacity is null or < 0 or > int.MaxValue)
        {
            throw new RunnerCapacityIntegrationException(
                "Azure VM Scale Set returned an invalid capacity.",
                new InvalidOperationException($"Unexpected capacity value: {capacity}."));
        }

        return (int)capacity.Value;
    }
}
