# Runner access control

The control plane exposes the configured organization's runner group and selected repository allowlist. It uses Microsoft Entra ID for website sign-in and API bearer-token validation. Membership in the configured read-only or operator Entra group permits viewing; only the operator group can update the GitHub runner-group allowlist.

## Local VS Code setup

The project has an HTTPS launch profile at `https://localhost:7142` and a committed .NET User Secrets ID. User Secrets are loaded automatically when the app runs in Development, so local credentials do not belong in `appsettings.json` or `launchSettings.json`.

### 1. Create the Entra application

In the Microsoft Entra admin center:

1. Register a single-tenant application for this project.
2. Under **Authentication**, add a **Web** redirect URI of `https://localhost:7142/signin-oidc`.
3. Under **Certificates & secrets**, create a client secret and copy its **Value**. The app needs the value, not the secret's ID.
4. Create two distinct security groups and add your account to the appropriate group: one read-only group and one operator group. Copy each group's **Object ID**.
5. In **Token configuration**, add a `groups` claim containing the user's security-group object IDs. The application uses these IDs to make its reader/operator authorization decision.

### 2. Store local configuration

From the repository root, set the Entra settings in the local User Secrets store:

```sh
dotnet user-secrets set "Entra:TenantId" "<tenant-id>" --project src/RunnerControlPlane
dotnet user-secrets set "Entra:ClientId" "<application-client-id>" --project src/RunnerControlPlane
dotnet user-secrets set "Entra:ClientSecret" "<client-secret-value>" --project src/RunnerControlPlane
dotnet user-secrets set "Entra:ReadOnlyGroupId" "<reader-group-object-id>" --project src/RunnerControlPlane
dotnet user-secrets set "Entra:OperatorGroupId" "<operator-group-object-id>" --project src/RunnerControlPlane
```

Keep the real client-secret value out of shell history where possible. User Secrets are local development convenience storage, not encrypted production secret storage.

The app also validates GitHub settings at startup. Set the organization, token, and numeric runner-group ID in User Secrets:

```sh
dotnet user-secrets set "GitHub:Organization" "<organization>" --project src/RunnerControlPlane
dotnet user-secrets set "GitHub:Token" "<github-token>" --project src/RunnerControlPlane
dotnet user-secrets set "GitHub:RunnerGroupId" "<runner-group-id>" --project src/RunnerControlPlane
```

The GitHub token must be able to read the selected repositories and modify the organization's self-hosted runner groups. GitHub App tokens need `organization_self_hosted_runners` write permission and repository metadata read access; classic personal access tokens need the `admin:org` scope and repository access. Do not attach a new or permissive runner group to active runners before saving its first allowlist. For a fail-closed bootstrap, initialize the group with selected-repository visibility, public-repository access disabled, and no selected repositories.

### 3. Run from VS Code

Install the .NET 10 SDK and the C# Dev Kit extension. In the VS Code integrated terminal:

```sh
dotnet dev-certs https --trust
dotnet restore RunnerControlPlane.sln
dotnet run --project src/RunnerControlPlane --launch-profile https
```

Open `https://localhost:7142` and sign in. The HTTPS launch profile sets `ASPNETCORE_ENVIRONMENT=Development`, which enables User Secrets loading. Use `Ctrl+C` in the terminal to stop the app.

### Troubleshooting

- Startup errors such as `Entra:ClientSecret is required` identify the setting to add. The reader and operator group IDs must also be different.
- `AADSTS50011` means the redirect URI in Entra does not exactly match `https://localhost:7142/signin-oidc`.
- If sign-in succeeds but the page reports access denied, check that the user is in one of the configured groups and that the token contains its object ID in the `groups` claim.
- The GitHub settings are independently required; once Entra validation passes, missing GitHub settings can still prevent startup.

## Configuration reference

| Setting | Purpose |
| --- | --- |
| `Entra__TenantId` | Microsoft Entra tenant ID |
| `Entra__ClientId` | Entra application/client ID |
| `Entra__ClientSecret` | Entra application client secret value |
| `Entra__ReadOnlyGroupId` | Entra group object ID with read-only access |
| `Entra__OperatorGroupId` | Entra group object ID with operator access |
| `Entra__ApiAudience` | Optional API audience; defaults to `api://<client-id>` |
| `GitHub__Organization` | The single GitHub organization managed by this instance |
| `GitHub__Token` | GitHub token for runner-group writes and repository metadata reads |
| `GitHub__RunnerGroupId` | Numeric ID of the organization's shared runner group |
| `GitHub__ApiBaseUrl` | Optional GitHub API URL; defaults to `https://api.github.com/` |
| `Runner__Label` | Stable runner label; defaults to `linux-dotnet` |

`Entra:ApiAudience` only needs an explicit value when configuring API bearer-token clients; browser sign-in uses the OIDC client settings. The application's JWT validation requires tokens from the configured tenant. Do not use User Secrets for production.

The group should be an organization runner group; the application replaces its selected repository IDs and sets visibility to selected repositories with public-repository access disabled whenever an operator saves the allowlist. Repository allowlist entries use `organization/repository` names and are resolved to repository IDs before the GitHub group is updated.

## Interfaces

- `GET /api/runner-access` returns the organization, runner group, stable label, restriction state, selected repositories, and whether the signed-in user can manage the allowlist.
- `PUT /api/runner-access` replaces the allowlist. The JSON body is `{ "repositories": ["organization/repository"] }`; an empty array selects no repositories.
- `/` is the authenticated website. It hides the editor for read-only users and submits changes to the same API endpoint. `/login` starts Entra sign-in.

API requests without valid authentication receive `401`; authenticated users outside both configured groups receive `403`. Read-only users receive `403` for allowlist updates. The group membership checks use the `groups` claim; configure Entra to emit that claim for users accessing this application.
