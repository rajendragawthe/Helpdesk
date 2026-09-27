# Production Deployment Runbook (Phase 10)

One-time setup and every-deploy flow for the Azure production environment. Prerequisites:
Azure CLI installed and logged in (`az login`) against the target subscription, with the Bicep
extension available (`az bicep install`). The account applying the template needs **Owner** (or
Contributor plus User Access Administrator) on the resource group, because the template creates
Key Vault role assignments.

Resource names below assume `infra/main.parameters.json`'s default `baseName` of `helpdesk`
(`helpdesk-api-prod`, `helpdesk-kv-prod`, `helpdesk-web-prod`, ...). If you change `baseName`,
substitute the derived names everywhere in this runbook and in the GitHub repository variables
from step 9.

## One-time setup

1. **Create the resource group.**
   ```
   az group create --name rg-helpdesk-prod --location <your-region>
   ```
   The resource group's region can be any region. The Static Web App is the exception: Azure
   Static Web Apps only deploy to a small set of regions (e.g. `westus2`, `centralus`, `eastus2`,
   `westeurope`, `eastasia`), so `main.bicep` places it via its own `staticWebAppLocation` param
   (default `eastus2`), independent of the resource group region. To use a different supported
   region, add `"staticWebAppLocation": { "value": "<region>" }` to `infra/main.parameters.json`.
   Check the current list with
   `az provider show --namespace Microsoft.Web --query "resourceTypes[?resourceType=='staticSites'].locations" -o tsv`.

2. **Create the two production Entra app registrations** (distinct from the existing dev ones -
   see CLAUDE.md's "Auth model" and "Email ingestion" sections for what the dev equivalents look
   like):
   - API sign-in app: redirect URI `https://<static-web-app-hostname>` (get the hostname after
     step 6 applies the Bicep template, or reserve the name first via `az staticwebapp create
     --name helpdesk-web-prod --resource-group rg-helpdesk-prod --sku Standard` and read its
     `defaultHostname` before running the full `main.bicep` apply, since Bicep will happily manage
     an existing resource of the same name).
   - Graph mail app: application permissions `Mail.ReadWrite` + `Mail.Send`, admin consent granted,
     scoped to the **new production mailbox** (not the dev mailbox already used for testing).

3. **Provision a dedicated production mailbox** and grant the Graph mail app access to it.

4. **Get a production OpenRouter API key.**

5. **Fill in `infra/main.parameters.json`**, replacing every `REPLACE_WITH_...` placeholder:
   - `azureAdTenantId`, `azureAdClientId`, `azureAdAudience`, `graphTenantId`, `graphClientId`,
     `graphMailboxAddress`: the values from steps 2-3.
   - `deployerObjectId`: your own Entra object ID, from `az ad signed-in-user show --query id -o tsv`.
     The Bicep grants it the **Key Vault Secrets Officer** role on the vault, which step 7 needs
     (the vault uses Azure RBAC authorization, and subscription Owner/Contributor does **not**
     grant access to secret values).
   - `ciPrincipalObjectId`: the object ID of the GitHub Actions service principal from step 8, if
     it already exists. The Bicep grants it **Key Vault Secrets User**, which `deploy.yml` needs to
     read the connection string for migrations. If you haven't created it yet, set this to `""`
     for now and set it (then re-run step 6) or grant the role by hand in step 8.

   Never leave a `REPLACE_WITH_...` placeholder in `deployerObjectId`/`ciPrincipalObjectId`: a
   non-empty value is used as a principal ID and the deployment fails. Use `""` to skip either
   role assignment.

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

   This also wires `APPLICATIONINSIGHTS_CONNECTION_STRING` straight from the Application Insights
   resource into the App Service as a plain app setting (it isn't a credential), so there is no
   Key Vault secret to set for it.

7. **Set the Key Vault secret values** (the deployment above provisions the vault but not its
   secret *values* - see the Phase 10 spec's "Secrets" decision for why). This needs the
   **Key Vault Secrets Officer** role from step 5's `deployerObjectId`. If you left that blank,
   grant it by hand first (role assignments can take a minute or two to take effect):
   ```
   az role assignment create --role "Key Vault Secrets Officer" \
     --assignee "$(az ad signed-in-user show --query id -o tsv)" \
     --scope "$(az keyvault show --name helpdesk-kv-prod --query id -o tsv)"
   ```
   Then set the three secrets:
   ```
   VAULT=helpdesk-kv-prod

   az keyvault secret set --vault-name $VAULT --name ConnectionStrings-DefaultConnection \
     --value "Host=<postgresServerFqdn output>;Database=helpdesk;Username=<postgresAdminLogin>;Password=<the password from step 6>;Ssl Mode=Require"

   az keyvault secret set --vault-name $VAULT --name GraphApi-ClientSecret --value '<prod Graph app client secret>'

   az keyvault secret set --vault-name $VAULT --name OpenRouter-ApiKey --value '<prod OpenRouter key>'
   ```

   **7a. Restart both slots.** App Service resolves Key Vault references when the app starts, and
   the app started in step 6 before these secrets existed, so restart it to pick them up:
   ```
   az webapp restart --name helpdesk-api-prod --resource-group rg-helpdesk-prod
   az webapp restart --name helpdesk-api-prod --resource-group rg-helpdesk-prod --slot staging
   ```

8. **Set up the GitHub OIDC federated credential** for `deploy.yml`'s `azure/login` step: create
   (or reuse) an Entra app registration for GitHub Actions, add a federated credential scoped to
   `repo:rajendragawthe/Helpdesk:ref:refs/heads/main`, and grant its service principal
   `Contributor` on `rg-helpdesk-prod`:
   ```
   az role assignment create --role Contributor --assignee <ci-app-client-id> \
     --scope "$(az group show --name rg-helpdesk-prod --query id -o tsv)"
   ```
   The workflow also reads the connection string from Key Vault, so the service principal needs
   **Key Vault Secrets User** on the vault. Get its object ID with
   `az ad sp show --id <ci-app-client-id> --query id -o tsv`, then either put it in
   `infra/main.parameters.json` as `ciPrincipalObjectId` and re-run step 6, or grant the role by
   hand:
   ```
   az role assignment create --role "Key Vault Secrets User" --assignee <ci-sp-object-id> \
     --scope "$(az keyvault show --name helpdesk-kv-prod --query id -o tsv)"
   ```
   Then set these as GitHub repo **secrets** (Settings > Secrets and variables > Actions >
   Secrets): `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`.

9. **Set the GitHub repo variables** that `deploy.yml` reads resource names from (Settings >
   Secrets and variables > Actions > **Variables** tab, not Secrets):
   - `AZURE_RESOURCE_GROUP` = `rg-helpdesk-prod`
   - `AZURE_APP_SERVICE_NAME` = the App Service name, `<baseName>-api-prod` (default
     `helpdesk-api-prod`)
   - `AZURE_KEY_VAULT_NAME` = the Key Vault name, `<baseName>-kv-prod` (default `helpdesk-kv-prod`)

   If you kept `baseName = 'helpdesk'` (the parameters file's default), these are exactly the
   literal names used throughout this runbook. `deploy-frontend` also builds `VITE_API_BASE_URL`
   from `AZURE_APP_SERVICE_NAME`.

10. **Get the Static Web Apps deployment token** and set it as a GitHub repo secret:
    ```
    az staticwebapp secrets list --name helpdesk-web-prod --resource-group rg-helpdesk-prod --query properties.apiKey -o tsv
    ```
    Set this as `AZURE_STATIC_WEB_APPS_API_TOKEN`.

11. **Add the frontend's remaining build-time env vars** to `.github/workflows/deploy.yml`'s
    `deploy-frontend` job (the `Build` step's `env:` block), alongside the existing
    `VITE_API_BASE_URL`: `VITE_AZURE_AD_CLIENT_ID`, `VITE_AZURE_AD_TENANT_ID`,
    `VITE_AZURE_AD_REDIRECT_URI` (the Static Web App's `https://` hostname),
    `VITE_AZURE_AD_API_SCOPE` (matching the values in `client/helpdesk-web/.env.example`, using
    the prod API sign-in app's values from step 2).

### How the CD workflow reaches the production database

The CD workflow's migration step relies on `postgres.bicep`'s `AllowAzureServices` firewall rule
(0.0.0.0/0.0.0.0) to reach the production database from GitHub-hosted runners, since they run on
Azure infrastructure. This is Azure's documented convention for "any Azure-hosted resource", not
a GitHub-Actions-specific guarantee - if Microsoft ever changes runner hosting, migrations may
need a dedicated firewall rule for the runner's egress IP instead (GitHub publishes runner IP
ranges via its meta API, but they rotate frequently, making a static firewall rule impractical;
the Azure-services rule is the pragmatic choice for now).

### The staging slot never ingests email

The staging slot's app settings add `GraphApi__Enabled=false` and `OpenRouter__Enabled=false`,
and both names are slot-sticky (`slotConfigNames`), so a swap never moves them. Only the
production slot polls the mailbox and calls OpenRouter; the staging slot (whether it holds the
next build before a swap or the previous build after one) never does. Production also has
Always On enabled so the hosted poller isn't unloaded when the site is idle.

## Every deploy

Push to `main`. `deploy.yml` runs automatically: the backend test suite runs first, then the API
deploys to the staging slot, migrations run against production, and the slot swaps to production
only if migrations succeeded; the frontend is linted, built and deployed to Static Web Apps
independently. Deploys are serialized (`concurrency: deploy-prod`): a second push waits for the
running deploy to finish rather than cancelling it.

## Post-deploy verification (implementation-plan.md item 60)

1. **Confirm Key Vault references resolved.** An unresolved reference doesn't make the app fail
   in an obvious way: the setting just holds the literal `@Microsoft.KeyVault(...)` text, which is
   non-empty, so the app's "is not configured" startup check never fires. Ask App Service for the
   reference status directly instead (fill `<SUBSCRIPTION_ID>` from
   `az account show --query id -o tsv`):
   ```
   az rest --method get --url "https://management.azure.com/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/rg-helpdesk-prod/providers/Microsoft.Web/sites/helpdesk-api-prod/config/configreferences/appsettings?api-version=2022-03-01" --query "value[].{name:name,status:properties.status}" -o table
   ```
   and the same for the staging slot:
   ```
   az rest --method get --url "https://management.azure.com/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/rg-helpdesk-prod/providers/Microsoft.Web/sites/helpdesk-api-prod/slots/staging/config/configreferences/appsettings?api-version=2022-03-01" --query "value[].{name:name,status:properties.status}" -o table
   ```
   Every Key Vault-referencing setting (`ConnectionStrings__DefaultConnection`,
   `GraphApi__ClientSecret`, `OpenRouter__ApiKey`) must show `Resolved`. Anything else usually
   means a missing secret, a missing role assignment, or an app that wasn't restarted after the
   secrets were set (step 7a).

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
   test, run once against production. Also reload a deep link such as
   `https://<staticWebAppHostname>/tickets` to confirm the SPA navigation fallback
   (`client/helpdesk-web/public/staticwebapp.config.json`) serves the app instead of a 404.
