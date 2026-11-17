# Web component deployment plan

Status: Component locally validated; platform integration and promotion pending under the already approved whole-stack plan.

## Scope and approval
Implement the .NET 10 server-rendered Web/BFF component only. The approved central platform repository owns the single azd/Bicep lifecycle for dev, staging and shared resources. No app-owned infrastructure, azure.yaml, deployment, identity grants or new approval cycle belongs here.

## Architecture
App Service hosts a confidential Entra OpenID Connect BFF. User-delegated tokens call private Students and Academics App Services and Enrollments Container Apps. Each backend owns its independent SQL database; Web has no domain database. Managed-identity federation supplies confidential-client assertions. Azure Monitor uses managed identity.

## Component preparation completed
- Verified Identity.Web 4.18.0 / Abstractions 12.7.0 configuration and system-assigned MI federation metadata; options binding compiles and is tested.
- Implemented accessible landing, Student lookup, catalog and enrollment screens with typed HTTP clients.
- Local Release build: zero warnings/errors; 28 tests cover antiforgery, cookie boundaries, upstream error mapping, configuration and real client messages.
- Validated nine captured typed-client interactions against three immutable published candidate contracts, including Academics OpenAPI 3.1 / JSON Schema 2020-12.
- Framework-dependent App Service candidate ZIP generation includes exact clean source SHA and checksums; platform owns promotion. Local NuGet v3 audit is blocked by TLS handshake failure, not reported as a successful audit; CI must independently pass it.

## Platform handoff
Use `dotnet Contoso.Web.dll`, single instance, HTTPS, private backend reachability and a persistent protected Data Protection directory. README defines every environment key. Confidential-client federation, backend per-user scope/role consent, identity-backed telemetry ingestion and end-to-end live behavior remain unverified. No Azure resource or organization setting was changed.

## Validation boundaries
Local/CI tests are not evidence of live Entra, private networking, SQL, telemetry ingestion or deployed integration. Single-instance in-memory token cache requires re-login on restart; multi-instance distributed cache and shared protected Data Protection keys require platform design before scaling.

## Azure context
Inherited from the approved whole-stack architecture, not discovered or changed here. No subscription or tenant identifiers are stored in this repository.
