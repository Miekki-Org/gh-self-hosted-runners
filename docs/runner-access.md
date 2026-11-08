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

### 2. Configure GitHub for a Free organization

This app calls GitHub's **organization** runner-group API. `GitHub:Organization` must therefore be your organization URL slug (the part after `github.com/`), not your personal username. The repository you manage must belong to that organization; a repository still owned by your personal account cannot be added to this organization's allowlist.

GitHub Free organizations have one default runner group. Use it; creating additional organization runner groups requires GitHub Team. See [GitHub's runner-group documentation](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/manage-access).

#### Find the default runner-group ID

In the organization, open **Settings → Actions → Runner groups** and confirm the default group is present. To get its numeric ID, use GitHub CLI signed in as an organization owner with permission to read runner groups. If needed, authenticate with `gh auth login` and confirm the account with `gh auth status`:

```sh
gh api "orgs/<org-slug>/actions/runner-groups" \
  --jq '.runner_groups[] | select(.default) | [.id, .name] | @tsv'
```

Replace `<org-slug>` with your organization's URL slug. The command prints the default group's numeric ID and name; use the ID, not the name, for `GitHub:RunnerGroupId`. If the result is empty or access is denied, check the organization slug and that `gh auth status` shows the owner account.

#### Create a GitHub token

For local development, create a **fine-grained personal access token** from GitHub **Settings → Developer settings → Personal access tokens → Fine-grained tokens**:

1. Choose your new organization as the **Resource owner**.
2. Under **Repository access**, select the organization repositories you might add to the app's allowlist. The token must have access to each private repository whose metadata the app will look up.
3. Under **Organization permissions**, set **Self-hosted runners** to **Read and write**. The app reads the runner group and its selected repositories, then updates the selected-repository list and group visibility.
4. Choose an expiration, generate the token, and copy its value. Your GitHub account must have organization-owner/admin access; a token cannot grant more access than its owner has. Replace the token in User Secrets when it expires.

GitHub includes the repository **Metadata: Read-only** permission for fine-grained tokens. See the [fine-grained token permission reference](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens#organization-permissions-for-self-hosted-runners). If using a classic personal access token instead, it needs `admin:org` and, for private repositories, `repo`; prefer the fine-grained token because it can be limited to this organization and selected repositories.

#### Store the GitHub values in User Secrets

From the repository root, set the organization slug and numeric group ID. Substitute your actual values:

```sh
dotnet user-secrets set "GitHub:Organization" "<org-slug>" --project src/RunnerControlPlane
dotnet user-secrets set "GitHub:RunnerGroupId" "<default-runner-group-id>" --project src/RunnerControlPlane
```

Set the token without putting its value directly into a command saved in shell history. In macOS's default `zsh`, enter it at the hidden prompt:

```sh
read -s "GITHUB_TOKEN?Paste the fine-grained token (input hidden): "
echo
dotnet user-secrets set "GitHub:Token" "$GITHUB_TOKEN" --project src/RunnerControlPlane
unset GITHUB_TOKEN
```

You can check the non-secret values without printing the token:

```sh
dotnet user-secrets get "GitHub:Organization" --project src/RunnerControlPlane
dotnet user-secrets get "GitHub:RunnerGroupId" --project src/RunnerControlPlane
```

The app's first allowlist save sets the group to selected-repository access and disables public-repository access. To bootstrap safely, sign in as an Entra operator, leave **Allowed repositories** empty, and click **Save allowlist** before registering active self-hosted runners in the default group. If runners are already active in that group, pause their use until the empty allowlist is saved. Then add only the repositories you intend to use and save again.

### 3. Run from VS Code

Install the .NET 10 SDK and the C# Dev Kit extension. In the VS Code integrated terminal:

```sh
dotnet dev-certs https --trust
dotnet restore RunnerControlPlane.sln
dotnet run --project src/RunnerControlPlane --launch-profile https
```

Open `https://localhost:7142` and sign in. The page should show the configured organization and runner-group name. The HTTPS launch profile sets `ASPNETCORE_ENVIRONMENT=Development`, which enables User Secrets loading. Use `Ctrl+C` in the terminal to stop the app.

### Troubleshooting

- Startup errors such as `Entra:ClientSecret is required` identify the setting to add. The reader and operator group IDs must also be different.
- `AADSTS50011` means the redirect URI in Entra does not exactly match `https://localhost:7142/signin-oidc`.
- If sign-in succeeds but the page reports access denied, check that the user is in one of the configured groups and that the token contains its object ID in the `groups` claim.
- The GitHub settings are independently required; once Entra validation passes, missing GitHub settings can still prevent startup.
- GitHub API failures appear in the app as a generic “Unable to update or read the GitHub runner group” message. If checking the same token with GitHub CLI, `401` usually means the token is invalid or expired; `403` points to missing **Self-hosted runners: Read and write**, missing access to a private repository, insufficient organization-owner/admin access, or a token that needs organization approval.
- A GitHub `404` from the CLI commonly means `GitHub:Organization` is a personal username or display name instead of the organization slug, `GitHub:RunnerGroupId` does not belong to that organization, or the token cannot access the repository.
- If the app starts but cannot load the group, check the organization slug and numeric runner-group ID. The app currently reports missing/invalid GitHub settings together in one startup validation message.

## Configuration reference

| Setting | Purpose |
| --- | --- |
| `Entra__TenantId` | Microsoft Entra tenant ID |
| `Entra__ClientId` | Entra application/client ID |
| `Entra__ClientSecret` | Entra application client secret value |
| `Entra__ReadOnlyGroupId` | Entra group object ID with read-only access |
| `Entra__OperatorGroupId` | Entra group object ID with operator access |
| `Entra__ApiAudience` | Optional API audience; defaults to `api://<client-id>` |
| `GitHub__Organization` | Organization URL slug managed by this instance (not a personal username) |
| `GitHub__Token` | Fine-grained PAT with organization Self-hosted runners read/write and access to repositories to manage |
| `GitHub__RunnerGroupId` | Numeric ID of the organization's runner group; use the default group on GitHub Free |
| `GitHub__ApiBaseUrl` | Optional GitHub API URL; defaults to `https://api.github.com/` |
| `Runner__Label` | Stable runner label; defaults to `linux-dotnet` |

`Entra:ApiAudience` only needs an explicit value when configuring API bearer-token clients; browser sign-in uses the OIDC client settings. The application's JWT validation requires tokens from the configured tenant. Do not use User Secrets for production.

The group must belong to the configured organization. Whenever an operator saves the allowlist, the application replaces its selected repository IDs and sets visibility to selected repositories with public-repository access disabled. Repository allowlist entries use `organization/repository` names and are resolved to repository IDs before the GitHub group is updated.

## Interfaces

- `GET /api/runner-access` returns the organization, runner group, stable label, restriction state, selected repositories, and whether the signed-in user can manage the allowlist.
- `PUT /api/runner-access` replaces the allowlist. The JSON body is `{ "repositories": ["organization/repository"] }`; an empty array selects no repositories.
- `/` is the authenticated website. It hides the editor for read-only users and submits changes to the same API endpoint. `/login` starts Entra sign-in.

API requests without valid authentication receive `401`; authenticated users outside both configured groups receive `403`. Read-only users receive `403` for allowlist updates. The group membership checks use the `groups` claim; configure Entra to emit that claim for users accessing this application.
