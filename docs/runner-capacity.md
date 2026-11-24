# Runner capacity

Runner capacity is reconciled every `RunnerCapacity:ReconcileIntervalSeconds` seconds. Eligible queued jobs are counted from repositories available to the configured GitHub Runner group and jobs requesting the configured `Runner:Label`. The target is the greater of the configured minimum and the number of busy runners plus eligible queued jobs, capped at the configured maximum. Jobs beyond that capacity remain queued and appear as unserved demand.

Readers can view capacity at `GET /api/runner-capacity` and in the control-plane website. Operators can update the minimum and maximum with `PUT /api/runner-capacity/settings` and request one-off scale-out with `POST /api/runner-capacity/scale-out`. Both operations are capped by the maximum and add an actor, UTC timestamp, action, and outcome to `GET /api/runner-capacity/audit`.

## Runner VM health recovery

Set `RunnerVm:HealthFailureThreshold` to the number of consecutive failures required for each check; it defaults to three and must be at least two. Operators report each VM-health and GitHub runner-connectivity result to `POST /api/runner-vms/{id}/health-checks` with `vmHealthSucceeded`, `runnerConnectivitySucceeded`, and optional per-check failure reasons. A success resets that check's consecutive-failure count. A VM becomes unhealthy only when both counts reach the threshold; its failure counts and reasons are visible on the Runner VM API.

An unhealthy idle Runner VM is retired and replacement capacity is requested subject to the configured maximum. An unhealthy Runner VM with an active job stops accepting new work and is retired after the job finishes; the control plane does not retry the job. Operators can use `POST /api/runner-vms/{id}/force-remove` to remove a VM before its job drains. Removal and replacement requests appear in the Runner VM and capacity audit APIs.

## Azure VM Scale Set

Live VM Scale Set operations are disabled by default. Configure the following settings and set `AzureVmScaleSet:Enabled` to `true` to enable them:

| Setting | Purpose |
| --- | --- |
| `AzureVmScaleSet:SubscriptionId` | Subscription containing the scale set |
| `AzureVmScaleSet:ResourceGroupName` | Resource group containing the scale set |
| `AzureVmScaleSet:ScaleSetName` | VM Scale Set to resize |

The control plane uses `DefaultAzureCredential`; assign its managed identity (or local developer identity) permission to read and update the VM Scale Set and delete specific instances. The VM Scale Set must be preconfigured to bootstrap GitHub Actions runners with the configured label. Runner names must match VM host names so the control plane can select only idle instances for scale-in. If it cannot match a busy runner to an instance, scale-in fails closed.

When `TableStorage:Enabled` is true, capacity settings and audit records use the provisioned Azure table via managed identity. An interrupted or uncertain capacity operation leaves its table lock in place so another process cannot repeat a potentially completed VMSS operation. Stop writers and reconcile Azure capacity, settings and audit before an authorized operator clears that lock; never clear an incomplete migration without reconciling its partial data. When table storage is disabled and `RunnerCapacity:StateFilePath` is set, state is saved as JSON at that path; leave it empty for process memory. The file store uses process-local locking. See [deployment](runner-deployment.md) for migration and slot deployment restrictions.

## GitHub permissions

The configured GitHub token must be able to read the Runner group and organization runners, list the organization's repositories when the group allows all or public repositories, and read workflow runs and jobs in repositories eligible for that group. For private repositories, the token must also have Actions read permission. Queue reads cover only queued jobs requesting the configured stable runner label.

The VM Scale Set is responsible for runner provisioning and registration. This control plane changes VM Scale Set instance capacity; it does not create VM images or install runner software.
