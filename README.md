# Contoso University Web

A small .NET 10 MVC/Razor confidential backend-for-frontend (BFF) for the six-repository ADLC showcase. Browser pages contain no access tokens. The server calls real HTTP APIs with the signed-in user's delegated tokens.

## What works

- Public landing page, navigation and anonymous `/health/live` and `/health/ready`.
- Authenticated Student ID lookup (not student CRUD).
- Paged course catalog, course details and an enrollment entry point.
- Paged enrollments, create, grade update and explicit delete confirmation.
- Positive-ID validation, antiforgery on POSTs, encoded HTML, secure HttpOnly cookies and visible upstream authentication, permission, missing-record, conflict and availability states.

Catalog maintenance remains in Academics; Web deliberately does not implement its full administration surface. Student/course names are not synthesized into enrollment responses. Backend APIs, not posted roles or the Web app registration, enforce authority.

## Configuration interface

Supply environment variables; never commit deployment identifiers or credentials.

| Key | Meaning |
| --- | --- |
| `AzureAd__TenantId` | Single Entra directory GUID |
| `AzureAd__ClientId` | Confidential Web app registration GUID |
| `AzureAd__ClientCredentials__0__SourceType` | `SignedAssertionFromManagedIdentity` (also the checked-in default) |
| `AzureAd__ClientCredentials__0__ManagedIdentityClientId` | Optional **user-assigned** MI client GUID; omit for system-assigned MI |
| `Services__Students__BaseUrl` | Students HTTPS origin, without a path |
| `Services__Students__ReadScopes__0` | `api://<students-resource>/students.read` |
| `Services__Academics__BaseUrl` | Academics HTTPS origin |
| `Services__Academics__ReadScopes__0` | `api://<academics-resource>/academics.read` |
| `Services__Enrollments__BaseUrl` | Enrollments HTTPS origin |
| `Services__Enrollments__ReadScopes__0` | `api://<enrollments-resource>/enrollments.read` |
| `Services__Enrollments__WriteScopes__0` | Same Enrollments read scope |
| `Services__Enrollments__WriteScopes__1` | `api://<enrollments-resource>/enrollments.write` |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Required outside Development/Testing |
| `DataProtection__KeyPath` | Required persistent, access-controlled cookie-key directory outside Development/Testing |
| `AZURE_CLIENT_ID` | Optional UAMI selector for Azure Monitor's `DefaultAzureCredential`; also set the federation MI key above when using UAMI |
| `AllowedHosts` | Required outside Development/Testing: exact deployed Web DNS hostname(s), separated by semicolons; wildcards fail startup |

Instance and callback defaults are `https://login.microsoftonline.com/` and `/signin-oidc`. Platform must register the exact HTTPS callback and signed-out callback `/signout-callback-oidc`, configure the supported trusted-proxy/HTTPS forwarding boundary, backend private DNS/network reachability and single-instance hosting.

### Secretless delegated identity

Microsoft.Identity.Web **4.18.0** binds `ClientCredentials` using `CredentialDescription.SourceType=SignedAssertionFromManagedIdentity`. Its pinned Microsoft.Identity.Abstractions **12.7.0** XML documentation explicitly supports omitting `ManagedIdentityClientId` for a system-assigned MI. The options binding and authorization-code/PKCE/cookie settings are exercised in compiled tests. No client secret, certificate fallback, app-only write or OBO substitution is implemented.

Platform must establish the Web app registration's federated identity trust for the selected MI (matching issuer, MI principal subject and `api://AzureADTokenExchange` audience), configure Web access and obtain the required delegated consent. MI authenticates the **confidential client**, not the user. Backend access remains delegated:

- Each API read requires its exact `.read` scope and a user `Reader` or `Registrar` role assignment **on that API**.
- Enrollment writes require `enrollments.write` and that API's `Registrar` role. `.read` and `.write` do not imply each other.
- Enrollment write acquisition requests read + write from the **same** resource. Incremental consent is handled separately per API by Identity.Web; scopes from different resources are never combined into one authorization request.
- `access_as_user`, Web's own token audience, and backend MI lookup roles are not substitutes.

Relevant official references: [Identity.Web NuGet](https://www.nuget.org/packages/Microsoft.Identity.Web/4.18.0), [CredentialDescription](https://learn.microsoft.com/dotnet/api/microsoft.identity.abstractions.credentialdescription), [federated assertions](https://learn.microsoft.com/entra/msal/dotnet/acquiring-tokens/web-apps-apis/workload-identity-federation).

### Session and telemetry boundaries

This demo intentionally uses one instance and an in-memory server-side token cache. Restart/cache eviction requires re-authentication/consent; no token is saved in browser storage or the authentication cookie. Persist cookie encryption keys on the configured platform mount, protected by filesystem access controls and storage encryption. Distributed token caching and shared, protected Data Protection keys must be designed before scaling or claiming production readiness. There is no Web SQL database or new cache infrastructure here.

Production/staging uses `Azure.Monitor.OpenTelemetry.AspNetCore` 1.6.0 with `Azure.Identity` 1.21.0, `UseAzureMonitor` and `DefaultAzureCredential`, service name `contoso-web`. Platform disables telemetry local authentication, grants scoped Monitoring Metrics Publisher and must not enable a second auto-instrumentation agent. No application request/response bodies or tokens are logged. Development/Testing does not export telemetry.

`/health/ready` reports only validated Web configuration and process readiness. It never acquires user tokens or fans out to private APIs, and is **not** a claim of backend/SQL/Entra readiness. API operations have a ten-second header timeout, a separate ten-second body bound and a one-MiB response limit; redirects are not followed.

## Local validation

Requires .NET SDK 10.0.404 and Python 3.14.

```powershell
dotnet restore --locked-mode
dotnet format --no-restore --verify-no-changes
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --no-restore
python -m pip install -r scripts\requirements.txt
python scripts\check_contracts.py
```

Tests inject authentication **only in the test host** and use controlled HTTP handlers through the actual typed clients. They verify HTML rendering, permissions/error states, CSRF, secure cookie options, token scope/header routing and real JSON mappings. Captured requests/responses are then checked against the exact published OpenAPI snapshots listed in `contracts/dependencies.json`, including nullable grades and all consumed operations. These candidate contracts may be unmerged; pinning does not imply release approval.

For machines whose NuGet v3 metadata TLS fails, the official v2 feed can restore locked package content without weakening certificate validation:

```powershell
dotnet restore --locked-mode --source https://www.nuget.org/api/v2/ -p:NuGetAudit=false
```

That **local workaround is not an audit**. CI requires the official v3 vulnerability source, audited restore and an additional fail-closed direct/transitive vulnerability report. A failed metadata query is unknown, never "no vulnerabilities."

For a landing-page-only local run, set the nonsecret identity/service configuration and use an HTTPS development profile (`dotnet run --project src\Contoso.Web -- --urls https://localhost:7443`). Live Entra login requires the configured managed identity host; local test authentication is not shipped as a runtime option.

## Candidate packaging and lifecycle

After committing a clean source revision and building Release:

```powershell
python scripts\package.py
```

This publishes a framework-dependent root-level App Service ZIP (`Contoso.Web.dll`, startup `dotnet Contoso.Web.dll`), embeds `candidate.json` with the exact source SHA and per-file SHA256 hashes, and emits `SHA256SUMS` for the ZIP. Deterministic SourceLink paths are normalized; packaging rejects embedded checkout/home paths. Existing `artifacts\publish` output must be removed before re-packaging. Build/publish does not deploy.

The central **contoso-platform** repository owns the one azd/Bicep lifecycle, hosting, identity, network, telemetry permissions and promotion of the **same immutable artifact** through dev/staging. There is intentionally no `azure.yaml`, app-owned IaC, deployment job or Azure credential here.

CI runs on every branch push, PR and dispatch: locked restore, formatting, warnings-as-errors build/tests, pinned consumer checks, vulnerability audit and candidate artifact. `Required checks` fails on unexpected skipped/cancelled/failed prerequisite jobs. On PRs the pinned engineering Story gate additionally requires a linked complete story and **human-audited** `ready-for-agent`; agents must not apply acceptance, approve or merge. Without that human action a draft PR's Story gate is expected to fail honestly.

## Evidence limits

Local tests/builds are not evidence of live Entra authorization-code federation, backend SQL, private networking, consent, telemetry ingestion or deployed end-to-end enrollment behavior. These must be verified by the platform/integration owners after provisioning and human approval. A bootstrapped repository, successful CI candidate and a deployed integrated environment are distinct states.
