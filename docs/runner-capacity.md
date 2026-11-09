# Runner capacity

Runner capacity is reconciled every `RunnerCapacity:ReconcileIntervalSeconds` seconds. Eligible queued jobs are counted from repositories available to the configured GitHub Runner group and jobs requesting the configured `Runner:Label`. The target is the greater of the configured minimum and the number of busy runners plus eligible queued jobs, capped at the configured maximum. Jobs beyond that capacity remain queued and appear as unserved demand.

Readers can view capacity at `GET /api/runner-capacity` and in the control-plane website. Operators can update the minimum and maximum with `PUT /api/runner-capacity/settings` and request one-off scale-out with `POST /api/runner-capacity/scale-out`. Both operations are capped by the maximum and add an actor, UTC timestamp, action, and outcome to `GET /api/runner-capacity/audit`.

## Azure VM Scale Set

Live VM Scale Set operations are disabled by default. Configure the following settings and set `AzureVmScaleSet:Enabled` to `true` to enable them:

| Setting | Purpose |
| --- | --- |
| `AzureVmScaleSet:SubscriptionId` | Subscription containing the scale set |
| `AzureVmScaleSet:ResourceGroupName` | Resource group containing the scale set |
| `AzureVmScaleSet:ScaleSetName` | VM Scale Set to resize |

The control plane uses `DefaultAzureCredential`; assign its managed identity (or local developer identity) permission to read and update the VM Scale Set and delete specific instances. The VM Scale Set must be preconfigured to bootstrap GitHub Actions runners with the configured label. Runner names must match VM host names so the control plane can select only idle instances for scale-in. If it cannot match a busy runner to an instance, scale-in fails closed.

When `RunnerCapacity:StateFilePath` is set, updated capacity settings and their audit records are saved as JSON at that path. The default is `data/runner-capacity-state.json`; use durable storage for this path in production. Leave it empty to keep capacity settings and audit records in process memory. The file store uses process-local locking, so run one control-plane replica when using it.

## GitHub permissions

The configured GitHub token must be able to read the Runner group and organization runners, list the organization's repositories when the group allows all or public repositories, and read workflow runs and jobs in repositories eligible for that group. For private repositories, the token must also have Actions read permission. Queue reads cover only queued jobs requesting the configured stable runner label.

The VM Scale Set is responsible for runner provisioning and registration. This control plane changes VM Scale Set instance capacity; it does not create VM images or install runner software.
