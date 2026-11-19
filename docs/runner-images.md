# Runner image builds

The control plane stores image-build registrations, callback results, inventories, and audit records. Read-only and operator Entra groups can inspect images; only operators can register builds. Result callbacks accept either an operator or the configured image-build workload identity.

## API

- `POST /api/runner-images` registers an external build and returns `202 Accepted` with its immutable `buildId` and SHA-256 `manifestDigest`. The body must pin `ubuntuRelease`, one or more `dotNetSdks`, package versions, and `runnerVersion`.
- `POST /api/runner-images/{buildId}/result` records the build outcome. Submit the registered manifest digest, Azure image resource ID, exact image inventory, all four validation checks, and `succeeded` outcome. The API rejects a mismatched digest or inventory. Successful results are available only when every validation check passes; failed results retain their failure reason and do not replace available images. An identical retry is idempotent; a conflicting retry returns `409 Conflict`.
- `GET /api/runner-images` lists build records.
- `GET /api/runner-images/{buildId}` returns build status, manifest digest, result, inventory, and audit trail.
- `GET /api/runner-images/available` lists only images that passed every validation gate.

Statuses are `Queued`, `Building`, `Validating`, `Complete`, and `Failed`. Validation must confirm image checks, VM boot, GitHub Actions Runner registration, and a sample .NET build. A failed or incomplete validation never makes an image available.

Example build request:

```json
{
  "ubuntuRelease": "24.04",
  "dotNetSdks": ["8.0.404"],
  "packages": { "curl": "8.5.0" },
  "runnerVersion": "2.331.0"
}
```

The result callback body has this shape:

```json
{
  "manifestDigest": "<digest returned by registration>",
  "azureImageId": "/subscriptions/<subscription>/resourceGroups/<group>/providers/Microsoft.Compute/images/<name>",
  "inventory": {
    "ubuntuRelease": "24.04",
    "dotNetSdks": ["8.0.404"],
    "packages": { "curl": "8.5.0" },
    "runnerVersion": "2.331.0"
  },
  "validation": {
    "imageChecksPassed": true,
    "vmBooted": true,
    "gitHubRunnerRegistered": true,
    "sampleDotNetBuildPassed": true
  },
  "succeeded": true
}
```

Set `RunnerImageBuild__WorkloadClientId` to the Entra application client ID (`azp` or `appid` claim) authorized to report results. Configure the API app registration to include the `idtyp` optional access-token claim; callbacks require `idtyp: "app"` and reject tokens with delegated `scp` claims. Operators can also report results using the normal operator group authorization.

## Weekly builds

Set `RunnerImageBuild__RoutineBuildEnabled` to `true`, and configure `RunnerImageBuild__RoutineBuildRequest` with the pinned manifest for the current approved repositories. The scheduler queues one routine build each week at `RunnerImageBuild__RoutineBuildDayUtc` and `RunnerImageBuild__RoutineBuildTimeUtc` (UTC). Routine builds are not expedited. Do not enable the schedule until the required versions and integrations are configured.

## Integration and storage

The Azure image builder and GitHub-backed validator are replaceable `IRunnerImageBuilder` and `IRunnerImageValidator` integrations. The default registrations deliberately fail builds as unconfigured; they do not simulate successful Azure or GitHub operations. A deployment must replace both registrations with implementations that build the Azure image and run all required validation steps.

Image records are persisted to `RunnerImageBuild__StateFilePath` (default: `data/runner-image-state.json`) using atomic file replacement and survive control-plane process restarts. Configure that path on persistent storage in deployments.
