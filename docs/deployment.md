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
