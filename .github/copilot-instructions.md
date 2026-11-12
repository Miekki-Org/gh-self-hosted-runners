# Copilot instructions for gh-self-hosted-runners

## Build, test, and validation

- Full suite: `dotnet test --nologo`
- Single test: `dotnet test tests/RunnerControlPlane.Tests/RunnerControlPlane.Tests.csproj --filter "<TestName>" --nologo`
- Prefer the smallest relevant HTTP API test around the behavior under change before widening scope.
- The repository uses ASP.NET Core integration tests against the public API surface; keep assertions against externally observable behavior instead of private implementation details.

## Architecture at a glance

- The app is an Azure-hosted control plane for GitHub self-hosted runners.
- `src/RunnerControlPlane/Program.cs` wires the HTTP API and authorization policies.
- `src/RunnerControlPlane/RunnerCapacity/` handles queued-job scaling, warm-pool limits, manual scale-out, and capacity audit records.
- `src/RunnerControlPlane/RunnerVms/` manages Runner VM lifecycle, health, idle cleanup, retirement, and removal audit records.
- `src/RunnerControlPlane/Images/` manages validated image inventories, build lifecycle, canary rollouts, and rollout audit records.
- `src/RunnerControlPlane/Access/` contains the runner-access website and security model.
- Configuration is split across `Configuration/` and appsettings; production values are deployment inputs rather than code assumptions.

## Key repo conventions

- Use the domain language exactly as defined in `GLOSSARY.md`:
  - `Runner VM` = the Azure VM hosting exactly one GitHub Actions Runner process
  - `Runner image` = a versioned Ubuntu LTS image with a known inventory
  - `Runner group` = the GitHub organization-level access-control group
  - `warm pool` = the configured minimum idle capacity
- Authorization is separated by read-only vs operator access via `AccessPolicies.CanView` and `AccessPolicies.CanManage`.
- API behavior is contract-driven: capacity changes, image rollouts, and Runner VM removals must expose actor, timestamp, action, and outcome.
- Treat audit records as user-visible API data, not just internal logs.
- For `.http` testing against the local API, acquire a token for the configured Entra API audience and tenant, not a GitHub token. Match the token’s `aud` and `tid` to `Entra:ApiAudience` and `Entra:TenantId`.
- Prefer v2 Entra tokens (`ver: "2.0"`) and ensure the API is configured for the same tenant and audience before troubleshooting 401s.
- Launch VS Code from the same shell/session that exported `ACCESS_TOKEN`, so the REST Client sees the environment variable.

## Entra token troubleshooting for REST client sessions

When using `RunnerControlPlane.http`:

- Set `ACCESS_TOKEN` in the shell before launching VS Code: `export ACCESS_TOKEN="$(az account get-access-token --scope "api://<client-id>/<scope>" --query accessToken -o tsv)"`
- Confirm the token’s `aud` matches `Entra:ApiAudience` and its `tid` matches `Entra:TenantId`.
- If `401 Unauthorized` appears, check the token’s `ver`, tenant, and audience before debugging the app.
- A valid token without the required reader/operator groups normally yields `403`, while a mismatched issuer/audience/tenant yields `401`.

## Expected workflow

- Start with a failing single test at the API seam when fixing a bug or implementing a behavior.
- Validate at the API boundary with fake GitHub/Azure integrations, not private classes or storage internals.
- Keep changes focused to the relevant module (`RunnerCapacity`, `RunnerVms`, `Images`, or `Access`) and preserve the existing external contracts.
