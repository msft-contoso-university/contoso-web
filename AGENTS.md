# Web component boundaries

- Work only in this repository. Central `contoso-platform` owns infrastructure, Azure provisioning and immutable artifact promotion.
- No `azure.yaml`, app-owned stack, client secrets, app registration, grants or deployment workflow here.
- Never apply human acceptance/`ready-for-agent`, approve or merge. Link a complete human-reviewed story before requesting execution acceptance.
- Use exact published dependency SHAs in `contracts/dependencies.json`. Do not read another worker's in-progress checkout or infer missing endpoints.
- Keep BFF tokens server-side. Entra confidential-client federation authenticates the Web client; backend writes still use delegated user tokens and backend-enforced scope + role checks.
- Do not log bodies, access tokens, raw upstream errors or SQL. Encode HTML and preserve antiforgery on every POST.
- Validate with locked restore, formatting, Release build/tests, captured consumer-contract checks and a fail-closed NuGet audit. Report local, CI and live evidence separately.
- Build packages from a clean commit. CI never deploys; platform promotes the same checksummed artifact.
- Include `Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>` in agent commits.
