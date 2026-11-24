# Runner image builds

The control plane stores image-build registrations, callback results, inventories, and audit records. Read-only and operator Entra groups can inspect images; registration and result callbacks accept either an operator or the configured image-build workload identity.

## API

- `POST /api/runner-images` registers an external build and returns `202 Accepted` with its immutable `buildId` and SHA-256 `manifestDigest`. The body must pin `ubuntuRelease`, one or more `dotNetSdks`, package versions, and `runnerVersion`; scheduled builds also include `runnerSha256`, which is validated and included in the manifest digest.
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
  "runnerVersion": "2.331.0",
  "runnerSha256": "<64-character SHA-256 checksum>"
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

The deployment sets `RunnerImageBuild__WorkloadClientId` to the image workflow's `AZURE_CLIENT_ID`, the Entra application client ID (`azp` or `appid` claim) authorized to register builds and report results. There is no separate `IMAGE_WORKLOAD_CLIENT_ID` GitHub variable in this workflow. Configure the **API resource app registration's** optional access-token claims to include `idtyp`; the workload token must have `idtyp: "app"` and no delegated `scp` claim. A matching workload identity is authorized only for registration and result callbacks, not the group-protected read or operator APIs. Operators can also register builds and report results using normal operator group authorization.

## Scheduled and manual build registration

`.github/workflows/runner-image-build.yml` runs weekly at 03:00 UTC on Sunday and can also be started with `workflow_dispatch`. It reads `.github/runner-images/manifest.json`, resolves the approved Ubuntu LTS, .NET SDK channels, package versions from the declared Ubuntu repositories, and latest GitHub Actions Runner release with its SHA-256 checksum. The resolver prints the pinned request, validation and retention policies, and the same manifest digest the control plane computes. Resolver tests use fixture metadata and do not call production Azure or external metadata services.

Configure these GitHub `production` environment variables before enabling or dispatching the workflow:

- `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, and `AZURE_SUBSCRIPTION_ID` for the Azure application configured with GitHub Actions OIDC federation.
- `AZURE_RESOURCE_GROUP` and, if not using the default `mm-ghshr-uks-prod-app`, `AZURE_WEBAPP_NAME` for the deployed App Service.
- `RUNNER_IMAGE_API_SCOPE` for the control-plane API's v2 `/.default` scope.

The job runs only on `main` and uses the protected `production` environment. It queries Azure for the exact named running App Service and validates its resource ID and default `azurewebsites.net` hostname before constructing the HTTPS URL. It does not consume `RUNNER_IMAGE_API_URL` and does not deploy or rebuild the control plane. Its app-only token must satisfy the workload policy above; it need not be an operator. The authenticated readiness GET may return `403` for this narrowly scoped workload, while the registration POST is the authorization check. The registration step exposes `build_id` and `manifest_digest` as step outputs and fails if the returned digest differs from the locally resolved request. See [deployment and identity setup](runner-deployment.md) for federation, permission scopes, and isolated verification.

This workflow resolves and registers an external build; it does not provision the Azure image. The API's legacy in-process routine-build scheduler is separate and should remain disabled when this workflow is the registration source.

## Integration and storage

The Azure image builder and GitHub-backed validator are replaceable `IRunnerImageBuilder` and `IRunnerImageValidator` integrations. The default registrations deliberately fail builds as unconfigured; they do not simulate successful Azure or GitHub operations. A deployment must replace both registrations with implementations that build the Azure image and run all required validation steps.

Image, rollout, and Runner VM records use provisioned Azure tables when `TableStorage__Enabled` is true; otherwise image records use `RunnerImageBuild__StateFilePath` (default: `data/runner-image-state.json`) and VM and rollout state is in memory. Claimed builds require manual reconciliation after interruption; automatic production promotion is not ready. See [deployment and migration limitations](runner-deployment.md).
