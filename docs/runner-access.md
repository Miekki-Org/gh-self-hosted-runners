# Runner access control

The control plane exposes the configured organization's runner group and its selected repository allowlist. It uses Microsoft Entra ID for website sign-in and API bearer-token validation. Membership in the configured read-only or operator Entra group permits viewing; only the operator group can update the GitHub runner-group allowlist.

## Configuration

Set these values through the hosting environment or a secret store. Do not commit credentials.

| Setting | Purpose |
| --- | --- |
| `Entra__TenantId` | Microsoft Entra tenant ID |
| `Entra__ClientId` | Entra application/client ID |
| `Entra__ClientSecret` | Entra application client secret |
| `Entra__ReadOnlyGroupId` | Entra group object ID with read-only access |
| `Entra__OperatorGroupId` | Entra group object ID with operator access |
| `Entra__ApiAudience` | Optional API audience; defaults to `api://<client-id>` |
| `GitHub__Organization` | The single GitHub organization managed by this instance |
| `GitHub__Token` | GitHub token with organization runner-group write and repository metadata read permissions; GitHub App tokens need the `organization_self_hosted_runners` permission |
| `GitHub__RunnerGroupId` | Numeric ID of the organization's shared runner group |
| `GitHub__ApiBaseUrl` | Optional GitHub API URL; defaults to `https://api.github.com/` |
| `Runner__Label` | Stable runner label; defaults to `linux-dotnet` |

Register the Entra application with the website redirect URI `https://<host>/signin-oidc`. Configure group membership claims in the ID token and API access token. API clients must obtain a bearer token for the configured API audience. The application's JWT validation requires tokens from the configured tenant.

The GitHub token must be able to read the selected repositories and modify the configured organization's self-hosted runner groups. GitHub App tokens need `organization_self_hosted_runners` write permission and repository metadata read access; classic personal access tokens need the `admin:org` scope and repository access. The group should be an organization runner group; the application replaces its selected repository IDs and sets visibility to selected repositories with public-repository access disabled whenever an operator saves the allowlist. Repository allowlist entries use `organization/repository` names and are resolved to repository IDs before the GitHub group is updated.

Do not attach a new or permissive runner group to active runners before an operator saves its first allowlist. For a fail-closed bootstrap, initialize the group with selected-repository visibility, public-repository access disabled, and no selected repositories.

## Interfaces

- `GET /api/runner-access` returns the organization, runner group, stable label, restriction state, selected repositories, and whether the signed-in user can manage the allowlist.
- `PUT /api/runner-access` replaces the allowlist. The JSON body is `{ "repositories": ["organization/repository"] }`; an empty array selects no repositories.
- `/` is the authenticated website. It hides the editor for read-only users and submits changes to the same API endpoint. `/login` starts Entra sign-in.

API requests without valid authentication receive `401`; authenticated users outside both configured groups receive `403`. Read-only users receive `403` for allowlist updates. The group membership checks use the `groups` claim; configure Entra to emit that claim for users accessing this application.
