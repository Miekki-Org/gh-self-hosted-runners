# Runner image builds

The control plane exposes image-build status and inventories through the API. Read-only and operator Entra groups can inspect images; only operators can request builds.

## API

- `POST /api/runner-images` queues a build and returns `202 Accepted` with its immutable version. The body must pin `ubuntuRelease`, one or more `dotNetSdks`, package versions, and `runnerVersion`. Set `expedited` to `true` to prioritize the build ahead of queued routine builds.
- `GET /api/runner-images` lists build records.
- `GET /api/runner-images/{version}` returns build status, failure reason, and inventory when available.
- `GET /api/runner-images/available` lists only images that passed every validation gate.

Statuses are `Queued`, `Building`, `Validating`, `Complete`, and `Failed`. Validation must confirm image checks, VM boot, GitHub Actions Runner registration, and a sample .NET build. A failed or incomplete validation never makes an image available.

Example build request:

```json
{
  "ubuntuRelease": "24.04",
  "dotNetSdks": ["8.0.404"],
  "packages": { "curl": "8.5.0" },
  "runnerVersion": "2.331.0",
  "expedited": true
}
```

## Weekly builds

Set `RunnerImageBuild__RoutineBuildEnabled` to `true`, and configure `RunnerImageBuild__RoutineBuildRequest` with the pinned manifest for the current approved repositories. The scheduler queues one routine build each week at `RunnerImageBuild__RoutineBuildDayUtc` and `RunnerImageBuild__RoutineBuildTimeUtc` (UTC). Routine builds are not expedited. Do not enable the schedule until the required versions and integrations are configured.

## Integration and storage status

The Azure image builder and GitHub-backed validator are replaceable `IRunnerImageBuilder` and `IRunnerImageValidator` integrations. The default registrations deliberately fail builds as unconfigured; they do not simulate successful Azure or GitHub operations. A deployment must replace both registrations with implementations that build the Azure image and run all required validation steps.

Image records are currently held in process memory for the MVP. They are lost when the control plane restarts, so this storage is not suitable for production rollout history.
