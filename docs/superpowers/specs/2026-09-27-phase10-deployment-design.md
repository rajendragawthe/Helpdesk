# Phase 10 — Deployment: Design Spec

**Date:** 2026-09-27
**Status:** Approved for planning
**Implementation plan:** `docs/superpowers/plans/2026-09-27-phase10-deployment.md` (to be written)

## Purpose

Stand up a production deployment of the Helpdesk MVP on Azure: PostgreSQL, the
.NET API, the React frontend, production Entra ID / Graph configuration, and
CI/CD from the existing `rajendragawthe/Helpdesk` GitHub repo — covering
implementation-plan.md items 54-60.

## Scope decisions (from brainstorming)

- **Cloud:** Azure (matches the stack — Entra ID and Graph are already
  Azure-native; Azure MCP tooling is available in this session).
- **Tier:** production-grade, not the cheapest option — the user explicitly
  chose this over a free/burstable tier.
- **IaC:** Bicep, versioned under `infra/` in the repo, applied via
  `az deployment group create` — not manual portal clicks, not Terraform.
- **Deploy method:** native App Service deployment (no Docker) for the API;
  Azure Static Web Apps for the frontend — not containers.
- **Domain:** default Azure hostnames (`*.azurewebsites.net`,
  `*.azurestaticapps.net`) — no custom domain/TLS work in this phase.
- **Secrets:** Azure Key Vault, referenced by the App Service via managed
  identity (`@Microsoft.KeyVault(...)` app settings) — not plain App Service
  config values.
- **Prod Entra/mailbox:** separate prod app registrations (API sign-in app +
  Graph mail app) and a separate monitored mailbox from dev — not reuse of
  the existing dev app registrations/mailbox.
- **Who sets up secrets/app registrations:** the user, manually, at the point
  the plan calls for it — not attempted via Azure MCP tools in this session.

## Architecture

### Resource group
`rg-helpdesk-prod` (single region — region choice deferred to the plan/user
at apply time; no cross-region requirement for an MVP).

### Azure Database for PostgreSQL Flexible Server
- General Purpose tier (e.g. `Standard_D2ds_v4`), matching the "production
  grade" decision.
- 7-day automated backups.
- Single-zone (no zone-redundant HA) — HA roughly doubles compute cost and
  isn't justified for MVP traffic; exposed as an easy Bicep parameter to
  flip on later rather than hardcoded off.
- Firewall: allow Azure services + the App Service's outbound IP(s); no
  public internet access beyond that.
- Database name/user distinct from dev (`helpdesk` role reused as the
  pattern, prod credentials distinct, stored in Key Vault).

### App Service
- Linux App Service Plan, Standard S1, running `Helpdesk.Api` on the .NET 10
  runtime.
- A **staging deployment slot**, used for migrate-before-swap deploys (see
  CI/CD below) — standard production practice, minimal incremental cost on
  S1+.
- System-assigned managed identity, granted `get`/`list` on Key Vault
  secrets.
- App settings reference Key Vault secrets via
  `@Microsoft.KeyVault(SecretUri=...)` for: `OpenRouter:ApiKey`,
  `GraphApi:ClientSecret`, the Postgres connection string. Non-secret config
  (model name, polling interval, CORS origins, confidence threshold, Entra
  tenant/client IDs) stays as plain app settings, matching the existing
  `appsettings.Development.json` shape.
- `CorsOrigins` set to the Static Web App's hostname (CORS is already
  opt-in via config per Phase 9 — see `Helpdesk.Api/Cors/CorsExtensions.cs`).

### Azure Static Web Apps
- Standard tier, serving the built `client/helpdesk-web` (`npm run build`
  output).
- Calls the API at its full `https://<api>.azurewebsites.net/api/...` URL
  (no dev proxy in prod) — frontend needs a build-time env var for the API
  base URL, since the Vite dev proxy (`vite.config.ts`) only exists in dev.
  This is a small frontend change: an `VITE_API_BASE_URL` (or similar) env
  var, defaulting to relative `/api` in dev (unchanged) and set to the full
  API URL in the SWA build.
- Deployed via SWA's built-in GitHub Actions integration (deploy token).

### Azure Key Vault
- Holds `OpenRouter:ApiKey`, `GraphApi:ClientSecret`, and the Postgres
  admin password / full connection string.
- Access via the App Service's managed identity only — no shared access
  keys used by the app at runtime.
- The CI/CD workflow's Azure login (OIDC) also reads the Postgres
  connection string from Key Vault to run migrations — see below.

### Application Insights
- Added because OpenTelemetry instrumentation already exists
  (Phase 9, `Helpdesk.Api/Observability/ObservabilityExtensions.cs`) but has
  no exporter configured by default. Production gets an actual exporter:
  `Otel:OtlpEndpoint` set to the App Insights connection string's OTLP
  endpoint.
- Not a new instrumentation effort — just wiring the existing `AddObservability`
  code path's already-supported OTLP exporter to a real destination.

### Bicep layout (`infra/`)
```
infra/
├── main.bicep              # orchestrates the modules below, resource group scope
├── modules/
│   ├── postgres.bicep       # Flexible Server + firewall rules + database
│   ├── appservice.bicep     # Plan + Web App + staging slot + managed identity
│   ├── staticwebapp.bicep   # Static Web App resource
│   ├── keyvault.bicep       # Key Vault + access policy for the App Service identity
│   └── appinsights.bicep    # App Insights + Log Analytics workspace
└── main.parameters.json     # environment-specific parameter values (prod)
```
Secret *values* (Postgres password, OpenRouter key, Graph client secret) are
never Bicep parameters checked into the repo — they're set into Key Vault
out-of-band by the user (via `az keyvault secret set`) after the Key Vault
resource exists, per the plan's manual steps.

## CI/CD

Two GitHub Actions workflows in `.github/workflows/`:

### `ci.yml` (existing behavior, formalized)
On every PR and push: `dotnet build`/`dotnet test` for `Helpdesk.slnx`,
`npm ci && npm run build && npm run lint` for `client/helpdesk-web`. No infra
or deploy steps. Serves as a required check before merge.

### `deploy.yml` (new)
Triggered on push to `main`. Two jobs:

1. **API deploy**
   - `dotnet publish` the API.
   - Azure login via OIDC federated credential (no publish-profile secret
     stored in GitHub).
   - Deploy the published output to the App Service **staging slot**.
   - Run `dotnet ef database update --project src/Helpdesk.Infrastructure --startup-project src/Helpdesk.Api` against the production database, using a
     connection string read from Key Vault through the workflow's Azure
     login (not a GitHub secret).
   - If migration succeeds, swap staging → production
     (`az webapp deployment slot swap`). If it fails, the workflow stops;
     production is untouched and staging is left in a fixable state for the
     next push.

2. **Frontend deploy**
   - `npm run build` with the prod `VITE_API_BASE_URL`.
   - Deploy `dist/` to the Static Web App via the SWA GitHub Actions
     integration, using its deployment token (stored as a GitHub secret —
     SWA's standard mechanism doesn't support OIDC the same way App Service
     does).

The two jobs are independent (frontend deploy doesn't wait on the API
migration), since a frontend-only or API-only change shouldn't block on the
other.

## Manual setup (user, not this session)

These are prerequisites the plan will call for at the right point, each with
exact steps/values, but the user executes them:

1. Two new Entra app registrations for production:
   - API sign-in app (SPA/API), redirect URI set to the SWA's hostname.
   - Graph mail app (app-only), with `Mail.ReadWrite` + `Mail.Send`
     application permissions and admin consent, for the **new prod
     mailbox** (distinct from the dev mailbox already used for testing).
2. A dedicated production mailbox for the monitored inbox.
3. A production OpenRouter API key.
4. GitHub repo secrets/config:
   - Azure OIDC federated credential (client ID, tenant ID, subscription ID)
     for the `deploy.yml` Azure login.
   - SWA deployment token.
5. Setting the three Key Vault secret values after `infra/` is applied
   (`az keyvault secret set` for `OpenRouter-ApiKey`, `GraphApi-ClientSecret`,
   the Postgres connection string/password).

## Data flow (deployment sequence)

Matches implementation-plan.md items 54-60:
1. Apply `infra/main.bicep` to `rg-helpdesk-prod` (provisions Postgres, App
   Service + slot, Static Web App, Key Vault, App Insights).
2. User sets Key Vault secret values and creates the prod Entra app
   registrations + mailbox (manual steps above).
3. User configures GitHub OIDC + SWA token as repo secrets.
4. Push to `main` triggers `deploy.yml`: API → staging slot → migrate →
   swap; frontend → SWA build+deploy.
5. Manual smoke test in production: the same 5-varied-email procedure used
   for the Phase 9 smoke test (implementation-plan.md item 53), run against
   the prod mailbox, verified via `psql` against the prod database (item 60).

## Error handling

- A failed migration blocks the slot swap — production never receives a
  schema it can't run against.
- Existing app-level failure handling is unchanged: Graph/OpenRouter opt-in
  gating, retry-with-backoff, and review-flag escalation (Phases 4-9) all
  carry over unmodified to production config.
- App Service and Static Web Apps deploy failures fail the GitHub Actions
  run visibly; no silent partial deploys.

## Testing

- CI (`ci.yml`) already runs the full `dotnet test` suite and frontend
  build/lint on every push — unchanged, just formalized as a required
  check for this phase.
- No new automated E2E coverage for deployment itself (infra correctness
  isn't something the existing Playwright suite can exercise, and doing so
  is out of scope for an MVP deployment phase).
- Manual verification: implementation-plan.md item 60, the 5-email smoke
  test against production, as described above.

## Out of scope for this phase

- Custom domain / TLS certificate binding.
- Zone-redundant Postgres HA (left as a Bicep parameter, not enabled).
- Autoscale rules for the App Service Plan.
- Containerization (Docker/App Service for Containers/Container Apps).
- Multi-region or disaster-recovery setup.
