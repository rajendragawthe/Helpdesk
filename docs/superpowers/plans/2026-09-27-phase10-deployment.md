# Phase 10 — Deployment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up a production deployment of the Helpdesk MVP on Azure — infrastructure as Bicep, native App Service + Static Web Apps hosting, Key Vault-backed secrets, and staged CI/CD with migrate-before-swap — covering implementation-plan.md items 54-60.

**Architecture:** A new `infra/` Bicep tree provisions `rg-helpdesk-prod` (PostgreSQL Flexible Server, an App Service Plan + Web App + staging slot, a Static Web App, Key Vault, and Application Insights). Two GitHub Actions workflows build/test on every push and, on push to `main`, deploy the API to a staging slot, run EF Core migrations against production, swap to production only on success, and deploy the frontend to Static Web Apps. Two small app-code changes support this: the frontend gains a configurable API base URL (SWA and the API live on different origins in prod, unlike the dev proxy), and the API gains opt-in Application Insights export via the Azure Monitor OpenTelemetry distro.

**Tech Stack:** Bicep, Azure CLI, GitHub Actions (`azure/login` OIDC, `azure/webapps-deploy`, `Azure/static-web-apps-deploy`), `Azure.Monitor.OpenTelemetry.AspNetCore`, existing .NET 10 / React+Vite / EF Core stack.

**Spec:** `docs/superpowers/specs/2026-09-27-phase10-deployment-design.md`

## Global Constraints

- Cloud provider is Azure; IaC is Bicep under `infra/`, applied via `az deployment group create` — no Terraform, no manual portal-only resources.
- API deploys natively to App Service (no Docker); frontend deploys to Azure Static Web Apps.
- Default Azure hostnames only (`*.azurewebsites.net`, `*.azurestaticapps.net`) — no custom domain/TLS work.
- Secrets (`OpenRouter:ApiKey`, `GraphApi:ClientSecret`, the Postgres connection string) live in Key Vault, referenced by the App Service via its system-assigned managed identity — never as plain App Service config values, never in Bicep parameters or GitHub Actions YAML.
- Production Entra app registrations (API sign-in app + Graph mail app) and the monitored mailbox are separate from the existing dev ones — this plan does not touch dev config.
- PostgreSQL Flexible Server is General Purpose tier, 7-day backups, single-zone (no HA) — HA is a Bicep parameter default `false`, not hardcoded unavailable.
- App Service Plan is Linux Standard S1 with a staging deployment slot; deploys always go staging → migrate → swap, never straight to production.
- `az bicep build --file <path>` must succeed (no errors) for every `.bicep` file this plan adds — this is the plan's substitute for unit tests on IaC. Azure CLI with the Bicep extension (`az bicep install`) is a prerequisite; if `az` is not on PATH, install it first (https://learn.microsoft.com/cli/azure/install-azure-cli).
- Solution-wide test command remains `dotnet test Helpdesk.slnx` from the repo root; frontend has no test runner configured (`npm run build` — which runs `tsc -b` — is the frontend's correctness check, per existing project convention).

## Review Focus

- **Frontend calls a relative `/api/...` path in production, where the SPA (Static Web Apps) and the API (App Service) are different origins** — every request 404s. Pinned in Task 1: a grep check that no source file under `client/helpdesk-web/src` calls `fetch(`/apiFetch(instance, ` with a literal `/api...` string outside the new `buildApiUrl` helper.
- **`CorsOrigins` doesn't exactly match the deployed Static Web App's hostname** (scheme, trailing slash, or wrong hostname) — the browser silently blocks every API call with no server-side error to look at. Pinned in Task 8: `main.bicep`'s App Service module input is wired directly from the Static Web App module's `hostname` output (`https://` + output, no hand-typed hostname anywhere in the Bicep tree).
- **A failing EF Core migration doesn't stop the slot swap** — production would end up serving old code against a database schema no migration ever finished, or (worse) swap to a slot that started against a half-migrated schema. Pinned in Task 10: the swap step is gated with `if: success()` directly after the migration step, verified by reading the job's step order and conditions.
- **The Key Vault reference on an App Service setting doesn't resolve** (managed identity lacks access, or the `SecretUri` is wrong) — the app doesn't crash at Bicep-apply time, it crashes at container startup with an opaque `InvalidOperationException` (e.g. "OpenRouter:ApiKey is not configured", per the existing Phase 5 error message) that only shows up in Log Stream/Kudu. Pinned in Task 6: the Key Vault access policy for the App Service's managed identity is created in the same Bicep deployment as the App Service (no separate manual step to forget), and Task 11's runbook has an explicit "check Key Vault references resolved" verification step using `az webapp config appsettings list`.
- **The Postgres firewall doesn't allow the App Service's actual outbound IPs**, so the app deploys successfully but every DB call fails at runtime. Pinned in Task 8: `main.bicep` passes the App Service module's `possibleOutboundIpAddresses` output into the Postgres module's firewall rule parameter, rather than a hand-typed IP list.

---

## Task 1: Frontend — configurable API base URL

**Files:**
- Create: `client/helpdesk-web/src/lib/apiUrl.ts`
- Modify: `client/helpdesk-web/src/api/apiFetch.ts`
- Modify: `client/helpdesk-web/src/lib/clientErrorReporter.ts`

**Interfaces:**
- Produces: `buildApiUrl(path: string): string` — exported from `src/lib/apiUrl.ts`. Reads `import.meta.env.VITE_API_BASE_URL`; if unset/empty, returns `path` unchanged (today's relative-path dev-proxy behavior); if set, returns `` `${baseUrl.replace(/\/$/, '')}${path}` `` (strips a trailing slash from the base so `VITE_API_BASE_URL=https://api.example.com/` and `.../` both produce a single slash before `/api`).

- [ ] **Step 1: Write `buildApiUrl`**

```typescript
// client/helpdesk-web/src/lib/apiUrl.ts

/**
 * Prefixes a relative API path (e.g. "/api/tickets") with VITE_API_BASE_URL when set. In dev,
 * VITE_API_BASE_URL is unset and the Vite proxy (vite.config.ts) handles same-origin "/api"
 * requests, so this returns the path unchanged. In production the SPA (Static Web Apps) and the
 * API (App Service) are different origins, so every call needs the full API URL.
 */
export function buildApiUrl(path: string): string {
  const baseUrl = import.meta.env.VITE_API_BASE_URL
  if (!baseUrl) {
    return path
  }
  return `${baseUrl.replace(/\/$/, '')}${path}`
}
```

- [ ] **Step 2: Use it in `apiFetch`**

Modify `client/helpdesk-web/src/api/apiFetch.ts`:

```typescript
import type { IPublicClientApplication } from '@azure/msal-browser'
import { InteractionRequiredAuthError } from '@azure/msal-browser'
import { apiScopes } from '../authConfig'
import { buildApiUrl } from '../lib/apiUrl'

export async function apiFetch(
  msalInstance: IPublicClientApplication,
  path: string,
  init: RequestInit = {},
): Promise<Response> {
  const account = msalInstance.getActiveAccount()
  if (!account) {
    throw new Error('No active account: user must be signed in before calling apiFetch')
  }

  let token: string
  try {
    const result = await msalInstance.acquireTokenSilent({ scopes: apiScopes, account })
    token = result.accessToken
  } catch (error) {
    if (error instanceof InteractionRequiredAuthError) {
      await msalInstance.acquireTokenRedirect({ scopes: apiScopes, account })
      throw new Error('Redirecting for interactive sign-in')
    }
    throw error
  }

  return fetch(buildApiUrl(path), {
    ...init,
    headers: {
      ...init.headers,
      Authorization: `Bearer ${token}`,
    },
  })
}
```

- [ ] **Step 3: Use it in `clientErrorReporter`**

Modify `client/helpdesk-web/src/lib/clientErrorReporter.ts` (only the `fetch` call changes — everything else, including the doc comment explaining why this doesn't use `apiFetch`, stays as-is):

```typescript
import { buildApiUrl } from './apiUrl'

// ... MAX_MESSAGE_LENGTH / MAX_STACK_LENGTH / reportClientError body unchanged until the fetch call ...

  fetch(buildApiUrl('/api/client-errors'), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).catch(() => {
    // Reporting failures must never surface as a second crash.
  })
```

- [ ] **Step 4: Verify no other call site bypasses the helper**

Run: `grep -rn "fetch('/api\|fetch(\"/api\|fetch(\`/api" client/helpdesk-web/src`
Expected: no output (every direct `fetch` call to an `/api/...` literal now goes through `buildApiUrl`). The `apiFetch(instance, '/api/...')` call sites in `TicketsProvider.tsx`, `useCurrentUser.ts`, `AdminUsersPage.tsx`, and `TicketDetail.tsx` are unaffected — they still pass a plain `/api/...` path, and `apiFetch` itself now applies `buildApiUrl` internally.

- [ ] **Step 5: Type-check and build**

Run: `cd client/helpdesk-web && npm run build`
Expected: succeeds (this project has no test runner configured; `npm run build` runs `tsc -b` first, which is this codebase's existing correctness check for frontend changes).

- [ ] **Step 6: Commit**

```bash
git add client/helpdesk-web/src/lib/apiUrl.ts client/helpdesk-web/src/api/apiFetch.ts client/helpdesk-web/src/lib/clientErrorReporter.ts
git commit -m "feat: support a configurable API base URL for production deploys

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 2: Backend — Application Insights export via Azure Monitor OpenTelemetry distro

**Files:**
- Modify: `src/Helpdesk.Api/Helpdesk.Api.csproj`
- Create: `src/Helpdesk.Api/Observability/AzureMonitorExtensions.cs`
- Modify: `src/Helpdesk.Api/Program.cs`
- Create: `tests/Helpdesk.Api.Tests/Observability/AzureMonitorExtensionsTests.cs`

**Interfaces:**
- Produces: `AzureMonitorExtensions.TryAddAzureMonitor(this IServiceCollection services, IConfiguration configuration): bool` — mirrors the existing `CorsExtensions.TryAddCors` opt-in pattern (`tests/Helpdesk.Api.Tests/Cors/CorsExtensionsTests.cs`, `src/Helpdesk.Api/Cors/CorsExtensions.cs`). Returns `false` and registers nothing when config key `APPLICATIONINSIGHTS_CONNECTION_STRING` is absent/empty; otherwise registers Azure Monitor export via `UseAzureMonitor()` and returns `true`.

The generic `Otel:OtlpEndpoint`/`Otel:ConsoleExporter` path in the existing `ObservabilityExtensions.AddObservability` (`src/Helpdesk.Api/Observability/ObservabilityExtensions.cs`) is unchanged — raw OTLP-with-no-auth cannot reach Application Insights directly (Azure Monitor's OTLP ingestion needs either managed-identity auth against a Data Collection Rule or the dedicated Azure Monitor exporter package; see Microsoft Learn, "Enable OpenTelemetry in Application Insights", `opentelemetry-enable`), so Application Insights gets its own opt-in extension using the supported package instead of overloading the OTLP path.

- [ ] **Step 1: Add the NuGet package**

Run: `cd src/Helpdesk.Api && dotnet add package Azure.Monitor.OpenTelemetry.AspNetCore`
Expected: `Helpdesk.Api.csproj` gains a `PackageReference` for `Azure.Monitor.OpenTelemetry.AspNetCore` at whatever the latest stable version resolves to.

- [ ] **Step 2: Write the failing test**

```csharp
// tests/Helpdesk.Api.Tests/Observability/AzureMonitorExtensionsTests.cs
using Helpdesk.Api.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Api.Tests.Observability;

public class AzureMonitorExtensionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Fact]
    public void TryAddAzureMonitor_NoConnectionString_ReturnsFalseAndRegistersNothing()
    {
        var services = new ServiceCollection();

        var result = services.TryAddAzureMonitor(Config());

        Assert.False(result);
        Assert.Empty(services);
    }

    [Fact]
    public void TryAddAzureMonitor_WithConnectionString_ReturnsTrueAndRegistersOpenTelemetry()
    {
        var services = new ServiceCollection();

        var result = services.TryAddAzureMonitor(Config(
            ("APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=00000000-0000-0000-0000-000000000000")));

        Assert.True(result);
        Assert.NotEmpty(services);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj --filter "FullyQualifiedName~AzureMonitorExtensionsTests"`
Expected: FAIL — `TryAddAzureMonitor` and `AzureMonitorExtensions` don't exist yet (compile error).

- [ ] **Step 4: Implement `AzureMonitorExtensions`**

```csharp
// src/Helpdesk.Api/Observability/AzureMonitorExtensions.cs
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Helpdesk.Api.Observability;

/// <summary>
/// Exports the existing OpenTelemetry instrumentation (see ObservabilityExtensions) to Azure
/// Application Insights when APPLICATIONINSIGHTS_CONNECTION_STRING is configured. Kept separate
/// from ObservabilityExtensions.AddObservability's generic OTLP/console exporter path because
/// Application Insights doesn't accept unauthenticated OTLP - it needs the dedicated Azure Monitor
/// exporter package, which this wraps behind the same opt-in pattern as CorsExtensions.TryAddCors.
/// Absent config leaves today's behavior (no Azure Monitor export) unchanged.
/// </summary>
public static class AzureMonitorExtensions
{
    public static bool TryAddAzureMonitor(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        services.AddOpenTelemetry().UseAzureMonitor(options => options.ConnectionString = connectionString);
        return true;
    }
}
```

- [ ] **Step 5: Wire it into `Program.cs`**

Modify `src/Helpdesk.Api/Program.cs` — add the call right after the existing `AddObservability` line (line 41):

```csharp
builder.Services.AddObservability(builder.Configuration);
builder.Services.TryAddAzureMonitor(builder.Configuration);
```

Add `using Helpdesk.Api.Observability;` if not already present (it already is, for `ObservabilityExtensions` — no new using needed).

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/Helpdesk.Api.Tests/Helpdesk.Api.Tests.csproj --filter "FullyQualifiedName~AzureMonitorExtensionsTests"`
Expected: PASS (2/2).

- [ ] **Step 7: Run the full solution test suite**

Run: `dotnet test Helpdesk.slnx`
Expected: all tests pass (no regression from the `Program.cs`/package change).

- [ ] **Step 8: Commit**

```bash
git add src/Helpdesk.Api/Helpdesk.Api.csproj src/Helpdesk.Api/Observability/AzureMonitorExtensions.cs src/Helpdesk.Api/Program.cs tests/Helpdesk.Api.Tests/Observability/AzureMonitorExtensionsTests.cs
git commit -m "feat: export telemetry to Application Insights when configured

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 3: Bicep module — Key Vault

**Files:**
- Create: `infra/modules/keyvault.bicep`

**Interfaces:**
- Produces (Bicep outputs, consumed by Task 8's `main.bicep`): `keyVaultName string`, `keyVaultUri string` (the `properties.vaultUri` of the created vault, e.g. `https://<name>.vault.azure.net/`).
- Consumes (Bicep params, supplied by `main.bicep`): `location string`, `keyVaultName string`, `tenantId string`.

- [ ] **Step 1: Write the module**

```bicep
// infra/modules/keyvault.bicep
@description('Azure region for the Key Vault.')
param location string

@description('Globally-unique Key Vault name (3-24 alphanumeric/hyphen characters).')
param keyVaultName string

@description('Entra tenant ID that owns this subscription, used for the vault access policy.')
param tenantId string

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: tenantId
    enableRbacAuthorization: false
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    accessPolicies: []
  }
}

output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
```

- [ ] **Step 2: Validate it compiles**

Run: `az bicep build --file infra/modules/keyvault.bicep`
Expected: succeeds, producing `infra/modules/keyvault.json` with no errors (delete the generated `.json` afterward — it's a build artifact, not committed; add `infra/**/*.json` to `.gitignore` if not already covered by an existing rule).

- [ ] **Step 3: Commit**

```bash
git add infra/modules/keyvault.bicep .gitignore
git commit -m "feat: add Key Vault Bicep module for prod secrets

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 4: Bicep module — PostgreSQL Flexible Server

**Files:**
- Create: `infra/modules/postgres.bicep`

**Interfaces:**
- Produces (outputs): `serverName string`, `fullyQualifiedDomainName string`, `databaseName string`.
- Consumes (params): `location string`, `serverName string`, `administratorLogin string`, `@secure() administratorPassword string`, `databaseName string`, `allowedOutboundIps array` (list of App Service outbound IP strings, wired from Task 8/the App Service module's output — empty array is valid and simply adds no firewall rules), `enableHighAvailability bool = false`.

- [ ] **Step 1: Write the module**

```bicep
// infra/modules/postgres.bicep
@description('Azure region for the Postgres Flexible Server.')
param location string

@description('Globally-unique Postgres Flexible Server name.')
param serverName string

@description('Postgres administrator login name.')
param administratorLogin string

@secure()
@description('Postgres administrator password. Never pass this as a literal - the caller (main.bicep) must source it from a secure parameter file or CLI prompt, never a committed value.')
param administratorPassword string

@description('Name of the application database to create on this server.')
param databaseName string = 'helpdesk'

@description('App Service outbound IP addresses to allow through the server firewall. Empty is valid (no rules added yet); populated once the App Service module has run.')
param allowedOutboundIps array = []

@description('Whether to enable zone-redundant HA. Off by default per the Phase 10 design (MVP traffic does not justify roughly doubling compute cost); flip to true here to enable it later without restructuring the module.')
param enableHighAvailability bool = false

resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2023-06-01-preview' = {
  name: serverName
  location: location
  sku: {
    name: 'Standard_D2ds_v4'
    tier: 'GeneralPurpose'
  }
  properties: {
    version: '16'
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorPassword
    storage: {
      storageSizeGB: 32
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: enableHighAvailability ? 'ZoneRedundant' : 'Disabled'
    }
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2023-06-01-preview' = {
  parent: postgresServer
  name: databaseName
}

resource allowAzureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2023-06-01-preview' = {
  parent: postgresServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource outboundIpRules 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2023-06-01-preview' = [
  for (ip, i) in allowedOutboundIps: {
    parent: postgresServer
    name: 'AllowAppServiceOutbound${i}'
    properties: {
      startIpAddress: ip
      endIpAddress: ip
    }
  }
]

output serverName string = postgresServer.name
output fullyQualifiedDomainName string = postgresServer.properties.fullyQualifiedDomainName
output databaseName string = database.name
```

- [ ] **Step 2: Validate it compiles**

Run: `az bicep build --file infra/modules/postgres.bicep`
Expected: succeeds with no errors.

- [ ] **Step 3: Commit**

```bash
git add infra/modules/postgres.bicep
git commit -m "feat: add PostgreSQL Flexible Server Bicep module

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 5: Bicep module — Application Insights

**Files:**
- Create: `infra/modules/appinsights.bicep`

**Interfaces:**
- Produces (outputs): `connectionString string` (the Application Insights connection string, consumed by Task 6's App Service module for the `APPLICATIONINSIGHTS_CONNECTION_STRING` Key Vault-backed app setting).
- Consumes (params): `location string`, `appInsightsName string`, `logAnalyticsWorkspaceName string`.

- [ ] **Step 1: Write the module**

```bicep
// infra/modules/appinsights.bicep
@description('Azure region for Application Insights and its Log Analytics workspace.')
param location string

@description('Application Insights resource name.')
param appInsightsName string

@description('Log Analytics workspace name backing the workspace-based Application Insights resource.')
param logAnalyticsWorkspaceName string

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

output connectionString string = appInsights.properties.ConnectionString
```

- [ ] **Step 2: Validate it compiles**

Run: `az bicep build --file infra/modules/appinsights.bicep`
Expected: succeeds with no errors.

- [ ] **Step 3: Commit**

```bash
git add infra/modules/appinsights.bicep
git commit -m "feat: add Application Insights Bicep module

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 6: Bicep module — App Service (Plan + Web App + staging slot + managed identity + Key Vault access)

**Files:**
- Create: `infra/modules/appservice.bicep`

**Interfaces:**
- Produces (outputs): `webAppName string`, `defaultHostName string` (production slot hostname, e.g. `helpdesk-api-prod.azurewebsites.net`), `principalId string` (the Web App's system-assigned managed identity, for any caller that needs to grant it access elsewhere), `possibleOutboundIpAddresses array` (parsed from the Web App's `possibleOutboundIpAddresses` comma-separated property, consumed by Task 8 to feed Task 4's Postgres firewall).
- Consumes (params): `location string`, `appServicePlanName string`, `webAppName string`, `keyVaultName string`, `keyVaultUri string`, `postgresConnectionStringSecretName string`, `graphClientSecretSecretName string`, `openRouterApiKeySecretName string`, `appInsightsConnectionStringSecretName string`, `corsOrigin string` (full `https://...` origin, e.g. from Task 7's Static Web App output), plus non-secret app settings passed straight through: `azureAdTenantId string`, `azureAdClientId string`, `azureAdAudience string`, `graphTenantId string`, `graphClientId string`, `graphMailboxAddress string`, `openRouterModel string`.

This module assumes the three secrets it references (`postgresConnectionStringSecretName`, `graphClientSecretSecretName`, `openRouterApiKeySecretName`, `appInsightsConnectionStringSecretName`) already exist as **versionless** Key Vault secret URIs at deploy time — Task 11's runbook sets their values with `az keyvault secret set` before the app is expected to start successfully. A missing secret doesn't fail the Bicep deployment (Key Vault references resolve lazily at container startup), which is exactly the failure mode called out in Review Focus — Task 11 has the explicit post-deploy check for it.

- [ ] **Step 1: Write the module**

```bicep
// infra/modules/appservice.bicep
@description('Azure region for the App Service Plan and Web App.')
param location string

@description('App Service Plan name.')
param appServicePlanName string

@description('Web App name (also determines the default hostname: <name>.azurewebsites.net).')
param webAppName string

@description('Key Vault name granting this app access to secrets.')
param keyVaultName string

@description('Key Vault URI, e.g. https://<name>.vault.azure.net/')
param keyVaultUri string

param postgresConnectionStringSecretName string
param graphClientSecretSecretName string
param openRouterApiKeySecretName string
param appInsightsConnectionStringSecretName string

@description('Full origin (scheme + host, no trailing slash) the API should allow via CORS - the deployed Static Web App hostname.')
param corsOrigin string

param azureAdTenantId string
param azureAdClientId string
param azureAdAudience string
param graphTenantId string
param graphClientId string
param graphMailboxAddress string
param openRouterModel string

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  sku: {
    name: 'S1'
    tier: 'Standard'
  }
  kind: 'linux'
  properties: {
    reserved: true
  }
}

var keyVaultRef = (secretName string) => '@Microsoft.KeyVault(SecretUri=${keyVaultUri}secrets/${secretName}/)'

var appSettings = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'ConnectionStrings__DefaultConnection', value: keyVaultRef(postgresConnectionStringSecretName) }
  { name: 'AzureAd__Instance', value: 'https://login.microsoftonline.com/' }
  { name: 'AzureAd__TenantId', value: azureAdTenantId }
  { name: 'AzureAd__ClientId', value: azureAdClientId }
  { name: 'AzureAd__Audience', value: azureAdAudience }
  { name: 'GraphApi__TenantId', value: graphTenantId }
  { name: 'GraphApi__ClientId', value: graphClientId }
  { name: 'GraphApi__ClientSecret', value: keyVaultRef(graphClientSecretSecretName) }
  { name: 'GraphApi__MailboxAddress', value: graphMailboxAddress }
  { name: 'OpenRouter__ApiKey', value: keyVaultRef(openRouterApiKeySecretName) }
  { name: 'OpenRouter__Model', value: openRouterModel }
  { name: 'CorsOrigins__0', value: corsOrigin }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: keyVaultRef(appInsightsConnectionStringSecretName) }
]

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appSettings: appSettings
      minTlsVersion: '1.2'
    }
  }
}

resource stagingSlot 'Microsoft.Web/sites/slots@2023-12-01' = {
  parent: webApp
  name: 'staging'
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appSettings: appSettings
      minTlsVersion: '1.2'
    }
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource productionAccessPolicy 'Microsoft.KeyVault/vaults/accessPolicies@2023-07-01' = {
  parent: keyVault
  name: 'add'
  properties: {
    accessPolicies: [
      {
        tenantId: webApp.identity.tenantId
        objectId: webApp.identity.principalId
        permissions: {
          secrets: ['get', 'list']
        }
      }
      {
        tenantId: stagingSlot.identity.tenantId
        objectId: stagingSlot.identity.principalId
        permissions: {
          secrets: ['get', 'list']
        }
      }
    ]
  }
}

output webAppName string = webApp.name
output defaultHostName string = webApp.properties.defaultHostName
output principalId string = webApp.identity.principalId
output possibleOutboundIpAddresses array = split(webApp.properties.possibleOutboundIpAddresses, ',')
```

- [ ] **Step 2: Validate it compiles**

Run: `az bicep build --file infra/modules/appservice.bicep`
Expected: succeeds with no errors.

- [ ] **Step 3: Commit**

```bash
git add infra/modules/appservice.bicep
git commit -m "feat: add App Service Bicep module with staging slot and Key Vault access

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 7: Bicep module — Static Web App

**Files:**
- Create: `infra/modules/staticwebapp.bicep`

**Interfaces:**
- Produces (outputs): `name string`, `hostname string` (bare hostname, e.g. `helpdesk-web.azurestaticapps.net` — no scheme), `deploymentToken string` (`@secure()`-marked output; consumed once by Task 11's runbook to seed the `AZURE_STATIC_WEB_APPS_API_TOKEN` GitHub secret, never stored elsewhere in the Bicep tree).
- Consumes (params): `location string`, `staticWebAppName string`.

- [ ] **Step 1: Write the module**

```bicep
// infra/modules/staticwebapp.bicep
@description('Azure region for the Static Web App. Static Web Apps only deploy to a subset of regions - see az staticwebapp environment for a current list if this fails.')
param location string

@description('Static Web App name.')
param staticWebAppName string

resource staticWebApp 'Microsoft.Web/staticSites@2023-12-01' = {
  name: staticWebAppName
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {}
}

output name string = staticWebApp.name
output hostname string = staticWebApp.properties.defaultHostname

@secure()
output deploymentToken string = staticWebApp.listSecrets().properties.apiKey
```

- [ ] **Step 2: Validate it compiles**

Run: `az bicep build --file infra/modules/staticwebapp.bicep`
Expected: succeeds with no errors. (`az bicep build` also flags `@secure()` outputs with a lint warning recommending they not be surfaced as plain outputs — expected and acceptable here since `main.bicep` in Task 8 only ever passes this output to a `az deployment group show` query the user runs once, not to another resource.)

- [ ] **Step 3: Commit**

```bash
git add infra/modules/staticwebapp.bicep
git commit -m "feat: add Static Web App Bicep module

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 8: Bicep — main.bicep orchestrator + parameters

**Files:**
- Create: `infra/main.bicep`
- Create: `infra/main.parameters.json`

**Interfaces:**
- Consumes: every module's params/outputs as defined in Tasks 3-7 — this task is where the wiring between them (the two Review Focus items about CORS-origin and Postgres-firewall-IP being derived, not hand-typed) actually happens.
- Produces (outputs, printed by `az deployment group create` and used in Task 11's runbook): `apiHostName string`, `webAppHostName string`, `keyVaultName string`, `postgresServerFqdn string`, `appInsightsConnectionString string`.

- [ ] **Step 1: Write `main.bicep`**

```bicep
// infra/main.bicep
targetScope = 'resourceGroup'

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Entra tenant ID for this subscription (used for the Key Vault access policy tenant).')
param tenantId string = subscription().tenantId

@description('Base name used to derive resource names, e.g. "helpdesk". Must be short - it is combined with resource-type suffixes and some resource types (Key Vault, Storage) have tight length limits.')
param baseName string = 'helpdesk'

@description('Postgres administrator login.')
param postgresAdminLogin string = 'helpdeskadmin'

@secure()
@description('Postgres administrator password. Supply via --parameters postgresAdminPassword=... on the CLI or a local (untracked) parameters override - never commit a real value.')
param postgresAdminPassword string

@description('Production Entra AzureAd:TenantId app setting value.')
param azureAdTenantId string

@description('Production Entra AzureAd:ClientId app setting value (the prod API sign-in app registration).')
param azureAdClientId string

@description('Production Entra AzureAd:Audience app setting value.')
param azureAdAudience string

@description('Production Graph mail app Entra tenant ID.')
param graphTenantId string

@description('Production Graph mail app client ID.')
param graphClientId string

@description('Production monitored mailbox address.')
param graphMailboxAddress string

@description('OpenRouter model identifier for production.')
param openRouterModel string = 'openai/gpt-4o-mini'

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyVaultDeploy'
  params: {
    location: location
    keyVaultName: '${baseName}-kv-prod'
    tenantId: tenantId
  }
}

module appInsights 'modules/appinsights.bicep' = {
  name: 'appInsightsDeploy'
  params: {
    location: location
    appInsightsName: '${baseName}-appinsights-prod'
    logAnalyticsWorkspaceName: '${baseName}-logs-prod'
  }
}

module staticWebApp 'modules/staticwebapp.bicep' = {
  name: 'staticWebAppDeploy'
  params: {
    location: location
    staticWebAppName: '${baseName}-web-prod'
  }
}

module appService 'modules/appservice.bicep' = {
  name: 'appServiceDeploy'
  params: {
    location: location
    appServicePlanName: '${baseName}-plan-prod'
    webAppName: '${baseName}-api-prod'
    keyVaultName: keyVault.outputs.keyVaultName
    keyVaultUri: keyVault.outputs.keyVaultUri
    postgresConnectionStringSecretName: 'ConnectionStrings-DefaultConnection'
    graphClientSecretSecretName: 'GraphApi-ClientSecret'
    openRouterApiKeySecretName: 'OpenRouter-ApiKey'
    appInsightsConnectionStringSecretName: 'ApplicationInsights-ConnectionString'
    corsOrigin: 'https://${staticWebApp.outputs.hostname}'
    azureAdTenantId: azureAdTenantId
    azureAdClientId: azureAdClientId
    azureAdAudience: azureAdAudience
    graphTenantId: graphTenantId
    graphClientId: graphClientId
    graphMailboxAddress: graphMailboxAddress
    openRouterModel: openRouterModel
  }
}

module postgres 'modules/postgres.bicep' = {
  name: 'postgresDeploy'
  params: {
    location: location
    serverName: '${baseName}-pg-prod'
    administratorLogin: postgresAdminLogin
    administratorPassword: postgresAdminPassword
    databaseName: 'helpdesk'
    allowedOutboundIps: appService.outputs.possibleOutboundIpAddresses
    enableHighAvailability: false
  }
}

output apiHostName string = appService.outputs.defaultHostName
output webAppHostName string = staticWebApp.outputs.hostname
output keyVaultName string = keyVault.outputs.keyVaultName
output postgresServerFqdn string = postgres.outputs.fullyQualifiedDomainName
output appInsightsConnectionString string = appInsights.outputs.connectionString
```

Note the deliberate ordering: `appService` is declared before `postgres` because `postgres`'s `allowedOutboundIps` param consumes `appService.outputs.possibleOutboundIpAddresses` — Bicep resolves the dependency graph regardless of declaration order, but keeping the data flow visually top-to-bottom (Key Vault → App Insights → Static Web App → App Service → Postgres) matches how a reader would trace "what needs what."

- [ ] **Step 2: Write `main.parameters.json`**

```json
{
  "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
  "contentVersion": "1.0.0.0",
  "parameters": {
    "baseName": { "value": "helpdesk" },
    "postgresAdminLogin": { "value": "helpdeskadmin" },
    "azureAdTenantId": { "value": "REPLACE_WITH_PROD_ENTRA_TENANT_ID" },
    "azureAdClientId": { "value": "REPLACE_WITH_PROD_API_APP_CLIENT_ID" },
    "azureAdAudience": { "value": "api://REPLACE_WITH_PROD_API_APP_CLIENT_ID" },
    "graphTenantId": { "value": "REPLACE_WITH_PROD_ENTRA_TENANT_ID" },
    "graphClientId": { "value": "REPLACE_WITH_PROD_GRAPH_APP_CLIENT_ID" },
    "graphMailboxAddress": { "value": "REPLACE_WITH_PROD_MAILBOX_ADDRESS" },
    "openRouterModel": { "value": "openai/gpt-4o-mini" }
  }
}
```

The `REPLACE_WITH_...` placeholders are intentional and expected here — this file's whole job is to be filled in with real values once Task 11's manual Entra/mailbox setup is done; unlike a plan step, a parameters file is meant to be edited post-merge. `postgresAdminPassword` is deliberately absent from this file (it's `@secure()`) — it's supplied on the command line at apply time, per Task 11.

- [ ] **Step 3: Validate the whole tree compiles**

Run: `az bicep build --file infra/main.bicep`
Expected: succeeds with no errors, resolving all four module references.

- [ ] **Step 4: Commit**

```bash
git add infra/main.bicep infra/main.parameters.json
git commit -m "feat: add main.bicep orchestrator wiring Key Vault, Postgres, App Service, Static Web App, App Insights

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 9: GitHub Actions — CI workflow

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- None (this task has no dependency on Bicep or the CD workflow — it only runs the existing build/test/lint commands already documented in `CLAUDE.md`).

- [ ] **Step 1: Write the workflow**

```yaml
# .github/workflows/ci.yml
name: CI

on:
  pull_request:
  push:
    branches: [main]

jobs:
  backend:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - name: Restore
        run: dotnet restore Helpdesk.slnx
      - name: Build
        run: dotnet build Helpdesk.slnx --no-restore --configuration Release
      - name: Test
        run: dotnet test Helpdesk.slnx --no-build --configuration Release

  frontend:
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: client/helpdesk-web
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-node@v4
        with:
          node-version: '20'
          cache: 'npm'
          cache-dependency-path: client/helpdesk-web/package-lock.json
      - name: Install
        run: npm ci
      - name: Lint
        run: npm run lint
      - name: Build
        run: npm run build
```

- [ ] **Step 2: Validate the YAML is well-formed**

Run: `cd "D:\Rajendra\Claude\Learning\HELPDESK" && python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/ci.yml'))" 2>&1 || echo "python/pyyaml unavailable - open the file and confirm indentation visually instead"`
Expected: no output (parses cleanly) — the fallback message is fine if Python/PyYAML isn't installed; visually check indentation is consistent (2 spaces) in that case.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add build/test/lint workflow for backend and frontend

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 10: GitHub Actions — CD workflow (migrate-before-swap deploy)

**Files:**
- Create: `.github/workflows/deploy.yml`

**Interfaces:**
- Consumes (GitHub repo secrets, set up in Task 11): `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (OIDC federated credential identity for `azure/login`), `AZURE_STATIC_WEB_APPS_API_TOKEN` (from Task 7's `staticWebApp.outputs.deploymentToken`).
- Consumes (values baked in from Task 8's Bicep, hardcoded here since they're stable resource names, not secrets): the App Service name `helpdesk-api-prod`, resource group `rg-helpdesk-prod`, Key Vault name `helpdesk-kv-prod`.

- [ ] **Step 1: Write the workflow**

```yaml
# .github/workflows/deploy.yml
name: Deploy

on:
  push:
    branches: [main]

permissions:
  id-token: write
  contents: read

env:
  RESOURCE_GROUP: rg-helpdesk-prod
  APP_SERVICE_NAME: helpdesk-api-prod
  KEY_VAULT_NAME: helpdesk-kv-prod

jobs:
  deploy-api:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Publish API
        run: dotnet publish src/Helpdesk.Api/Helpdesk.Api.csproj -c Release -o ${{ github.workspace }}/publish

      - name: Azure login (OIDC)
        uses: azure/login@v2
        with:
          client-id: ${{ secrets.AZURE_CLIENT_ID }}
          tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

      - name: Deploy to staging slot
        uses: azure/webapps-deploy@v3
        with:
          app-name: ${{ env.APP_SERVICE_NAME }}
          slot-name: staging
          package: ${{ github.workspace }}/publish

      - name: Install dotnet-ef
        run: dotnet tool install --global dotnet-ef --version 10.*

      - name: Read production connection string from Key Vault
        id: connstring
        run: |
          VALUE=$(az keyvault secret show --vault-name "${{ env.KEY_VAULT_NAME }}" --name "ConnectionStrings-DefaultConnection" --query value -o tsv)
          echo "::add-mask::$VALUE"
          echo "value=$VALUE" >> "$GITHUB_OUTPUT"

      - name: Run EF Core migrations against production
        env:
          ConnectionStrings__DefaultConnection: ${{ steps.connstring.outputs.value }}
        run: dotnet ef database update --project src/Helpdesk.Infrastructure --startup-project src/Helpdesk.Api

      - name: Swap staging to production
        if: success()
        run: az webapp deployment slot swap --resource-group "${{ env.RESOURCE_GROUP }}" --name "${{ env.APP_SERVICE_NAME }}" --slot staging --target-slot production

  deploy-frontend:
    runs-on: ubuntu-latest
    defaults:
      run:
        working-directory: client/helpdesk-web
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-node@v4
        with:
          node-version: '20'
          cache: 'npm'
          cache-dependency-path: client/helpdesk-web/package-lock.json

      - name: Install
        run: npm ci

      - name: Build
        env:
          VITE_API_BASE_URL: https://helpdesk-api-prod.azurewebsites.net
        run: npm run build

      - name: Deploy to Static Web Apps
        uses: Azure/static-web-apps-deploy@v1
        with:
          azure_static_web_apps_api_token: ${{ secrets.AZURE_STATIC_WEB_APPS_API_TOKEN }}
          repo_token: ${{ secrets.GITHUB_TOKEN }}
          action: upload
          app_location: client/helpdesk-web/dist
          skip_app_build: true
```

The `Run EF Core migrations against production` step has no `if:` guard because a step failure already stops the job by default — the very next step (`Swap staging to production`) is the one that needs the explicit `if: success()`, since without it a later step in a GitHub Actions job only skips automatically if a *previous* step failed AND no step overrode that with its own `if:`; being explicit here is the guard Review Focus calls for, not decorative.

VITE_AZURE_AD_* build-time env vars (client ID, tenant ID, redirect URI, API scope — see `client/helpdesk-web/.env.example`) are deliberately not set in this workflow yet: Task 11's runbook adds them as repo variables/secrets once the prod API sign-in app registration exists, and this step gets a follow-up edit at that point (documented in Task 11, not invented here as a placeholder).

- [ ] **Step 2: Validate the YAML is well-formed**

Run: `cd "D:\Rajendra\Claude\Learning\HELPDESK" && python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/deploy.yml'))" 2>&1 || echo "python/pyyaml unavailable - open the file and confirm indentation visually instead"`
Expected: no output (parses cleanly) — same fallback note as Task 9.

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/deploy.yml
git commit -m "ci: add migrate-before-swap production deploy workflow

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Task 11: Deployment runbook + docs updates

**Files:**
- Create: `docs/deployment.md`
- Modify: `implementation-plan.md`
- Modify: `CLAUDE.md`

**Interfaces:**
- None (documentation only). This task doesn't execute the deployment — the user does, following this runbook, since infra apply, Entra app registration, and mailbox setup all require the user's own Azure/Entra credentials (per the spec's "who sets up secrets" decision).

- [ ] **Step 1: Write the runbook**

```markdown
<!-- docs/deployment.md -->
# Production Deployment Runbook (Phase 10)

One-time setup and every-deploy flow for the Azure production environment. Prerequisites:
Azure CLI installed and logged in (`az login`) against the target subscription, with the Bicep
extension available (`az bicep install`).

## One-time setup

1. **Create the resource group.**
   ```
   az group create --name rg-helpdesk-prod --location <your-region>
   ```

2. **Create the two production Entra app registrations** (distinct from the existing dev ones -
   see CLAUDE.md's "Auth model" and "Email ingestion" sections for what the dev equivalents look
   like):
   - API sign-in app: redirect URI `https://<static-web-app-hostname>` (get the hostname after
     step 4 applies the Bicep template, or reserve the name first via `az staticwebapp create
     --name helpdesk-web-prod --resource-group rg-helpdesk-prod --sku Standard` and read its
     `defaultHostname` before running the full `main.bicep` apply, since Bicep will happily manage
     an existing resource of the same name).
   - Graph mail app: application permissions `Mail.ReadWrite` + `Mail.Send`, admin consent granted,
     scoped to the **new production mailbox** (not the dev mailbox already used for testing).

3. **Provision a dedicated production mailbox** and grant the Graph mail app access to it.

4. **Get a production OpenRouter API key.**

5. **Fill in `infra/main.parameters.json`** with the real `azureAdTenantId`, `azureAdClientId`,
   `azureAdAudience`, `graphTenantId`, `graphClientId`, and `graphMailboxAddress` values from steps
   2-3 (replacing every `REPLACE_WITH_...` placeholder).

6. **Apply the Bicep template:**
   ```
   az deployment group create \
     --resource-group rg-helpdesk-prod \
     --template-file infra/main.bicep \
     --parameters infra/main.parameters.json \
     --parameters postgresAdminPassword='<generate a strong password>'
   ```
   Save the `postgresAdminPassword` you generated - Key Vault holds the connection string, not
   Postgres's own admin credential record, so it isn't retrievable from Azure after this step.

7. **Set the Key Vault secret values** (the deployment above provisions the vault but not its
   secret *values* - see the Phase 10 spec's "Secrets" decision for why):
   ```
   VAULT=helpdesk-kv-prod

   az keyvault secret set --vault-name $VAULT --name ConnectionStrings-DefaultConnection \
     --value "Host=<postgresServerFqdn output>;Database=helpdesk;Username=<postgresAdminLogin>;Password=<the password from step 6>;Ssl Mode=Require"

   az keyvault secret set --vault-name $VAULT --name GraphApi-ClientSecret --value '<prod Graph app client secret>'

   az keyvault secret set --vault-name $VAULT --name OpenRouter-ApiKey --value '<prod OpenRouter key>'

   az keyvault secret set --vault-name $VAULT --name ApplicationInsights-ConnectionString \
     --value "$(az deployment group show --resource-group rg-helpdesk-prod --name main --query properties.outputs.appInsightsConnectionString.value -o tsv 2>/dev/null || echo 'read from the appInsightsDeploy nested deployment output if the above query name differs')"
   ```

8. **Set up GitHub OIDC federated credential** for `deploy.yml`'s `azure/login` step: create (or
   reuse) an Entra app registration for GitHub Actions, add a federated credential scoped to
   `repo:rajendragawthe/Helpdesk:ref:refs/heads/main`, grant it `Contributor` on
   `rg-helpdesk-prod` and `Key Vault Secrets User` (or an access policy with `get`) on
   `helpdesk-kv-prod`, then set these as GitHub repo secrets (Settings > Secrets and variables >
   Actions): `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`.

9. **Get the Static Web Apps deployment token** and set it as a GitHub repo secret:
   ```
   az staticwebapp secrets list --name helpdesk-web-prod --resource-group rg-helpdesk-prod --query properties.apiKey -o tsv
   ```
   Set this as `AZURE_STATIC_WEB_APPS_API_TOKEN`.

10. **Add the frontend's remaining build-time env vars** to `.github/workflows/deploy.yml`'s
    `deploy-frontend` job (the `Build` step's `env:` block), alongside the existing
    `VITE_API_BASE_URL`: `VITE_AZURE_AD_CLIENT_ID`, `VITE_AZURE_AD_TENANT_ID`,
    `VITE_AZURE_AD_REDIRECT_URI` (the Static Web App's `https://` hostname),
    `VITE_AZURE_AD_API_SCOPE` (matching the values in `client/helpdesk-web/.env.example`, using
    the prod API sign-in app's values from step 2).

## Every deploy

Push to `main`. `deploy.yml` runs automatically: API deploys to the staging slot, migrations run
against production, the slot swaps to production only if migrations succeeded, and the frontend
deploys to Static Web Apps independently.

## Post-deploy verification (implementation-plan.md item 60)

1. **Confirm Key Vault references resolved** (the failure mode that doesn't show up in Bicep or
   the deploy workflow - see the Phase 10 plan's Review Focus):
   ```
   az webapp config appsettings list --name helpdesk-api-prod --resource-group rg-helpdesk-prod \
     --query "[?contains(value, '@Microsoft.KeyVault')].{name:name}"
   ```
   Then check `https://helpdesk-api-prod.scm.azurewebsites.net/api/settings` (Kudu) or Log Stream
   for a startup error like "OpenRouter:ApiKey is not configured." - its absence means every
   Key Vault reference resolved.

2. **Run the same 5-email smoke test used for the Phase 9 manual verification**
   (`CLAUDE.md`'s "Hardening & polish" section has the exact procedure) against the production
   mailbox, and verify with:
   ```
   psql "host=<postgresServerFqdn> dbname=helpdesk user=<postgresAdminLogin> sslmode=require" \
     -c 'select "Subject", "Status", "ReviewReasons" from "Tickets" order by "CreatedAt" desc limit 10;'
   ```

3. **Sign in through the deployed frontend** (`https://<staticWebAppHostname>`) as the seeded
   bootstrap admin, confirm the queue loads, claim/reply on the smoke-test tickets, and confirm the
   reply arrives threaded in the sender's inbox - the same acceptance bar as the Phase 7 manual
   test, run once against production.
```

- [ ] **Step 2: Update `implementation-plan.md`**

Add a note under the Phase 10 heading (after line 87, before item 54) recording what's built vs.
what needs the user's live execution:

```markdown
## Phase 10 — Deployment

**Infra/CI code: done.** `infra/` (Bicep: Key Vault, PostgreSQL Flexible Server, App Service +
staging slot, Static Web App, Application Insights) and `.github/workflows/` (`ci.yml`,
`deploy.yml`) are written and committed. Items 54-60 below require the user's own Azure
subscription, Entra tenant, and mailbox access to execute - see `docs/deployment.md` for the exact
runbook (one-time setup steps 1-10, then push to `main`, then the post-deploy verification
checklist). Design: `docs/superpowers/specs/2026-09-27-phase10-deployment-design.md`; plan:
`docs/superpowers/plans/2026-09-27-phase10-deployment.md`.

```

(leave the existing numbered items 54-60 as they are immediately below this note - it's additive context, not a replacement)

- [ ] **Step 3: Update `CLAUDE.md`**

Add a new subsection after the existing "Hardening & polish (Phase 9 done)" section (before "Data
flow (MVP core loop)"), following the same style as the other phase sections:

```markdown
### Deployment (Phase 10 — infra/CI done, live deploy pending)

Azure, provisioned via Bicep (`infra/main.bicep` orchestrating `infra/modules/{keyvault,postgres,appservice,staticwebapp,appinsights}.bicep`): PostgreSQL Flexible Server (General Purpose, no HA), an App Service (Linux, .NET 10, Standard S1) with a staging slot for migrate-before-swap deploys, a Static Web App (Standard) for the frontend, Key Vault (secrets referenced by the App Service's managed identity via `@Microsoft.KeyVault(...)` app settings — never plain config values), and Application Insights (wired through a new opt-in `AzureMonitorExtensions.TryAddAzureMonitor`, `src/Helpdesk.Api/Observability/AzureMonitorExtensions.cs` — separate from the existing generic `Otel:OtlpEndpoint` path in `ObservabilityExtensions`, because Application Insights doesn't accept unauthenticated OTLP; gated on `APPLICATIONINSIGHTS_CONNECTION_STRING`, same opt-in pattern as CORS/GraphApi/OpenRouter). Two GitHub Actions workflows: `ci.yml` (build/test/lint on every push/PR) and `deploy.yml` (on push to `main`: API → staging slot → EF Core migrations against production → swap to production only on migration success; frontend → Static Web Apps, independently). Production uses separate Entra app registrations and a separate monitored mailbox from dev. Default Azure hostnames only, no custom domain. The frontend's dev-only Vite proxy (`vite.config.ts`) doesn't exist in production, since the SPA and API are different origins there — `client/helpdesk-web/src/lib/apiUrl.ts`'s `buildApiUrl` prefixes every API call with `VITE_API_BASE_URL` when it's set (unset in dev, so dev behavior is unchanged), used by both `apiFetch` and `clientErrorReporter`. Full one-time setup and post-deploy verification: `docs/deployment.md`. Spec: `docs/superpowers/specs/2026-09-27-phase10-deployment-design.md`; plan: `docs/superpowers/plans/2026-09-27-phase10-deployment.md`. **Not yet executed live** — infra apply, Entra/mailbox setup, and the production smoke test all need the user's own Azure/Entra credentials; `docs/deployment.md` is the checklist for that.

```

- [ ] **Step 4: Commit**

```bash
git add docs/deployment.md implementation-plan.md CLAUDE.md
git commit -m "docs: add production deployment runbook and Phase 10 status notes

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
```

---

## Final check (whole-branch)

After all 11 tasks:
- [ ] `dotnet test Helpdesk.slnx` passes (backend, including the two new `AzureMonitorExtensionsTests`).
- [ ] `cd client/helpdesk-web && npm run build && npm run lint` both succeed.
- [ ] `az bicep build --file infra/main.bicep` succeeds (transitively validates every module).
- [ ] Both workflow YAML files parse (Task 9/10 Step 2's check).
- [ ] `docs/deployment.md`, `implementation-plan.md`, and `CLAUDE.md` all exist/were edited and read consistently with what was actually built (no stray references to a design decision that changed during implementation).
