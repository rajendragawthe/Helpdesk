// infra/modules/appservice.bicep

// Builds an App Service Key Vault reference app-setting value. A Bicep user-defined function cannot
// see outer params/vars, so the vault URI is passed explicitly.
func keyVaultRef(vaultUri string, secretName string) string => '@Microsoft.KeyVault(SecretUri=${vaultUri}secrets/${secretName}/)'

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

@description('Application Insights connection string, set directly as a plain app setting (not a Key Vault reference): it is not a credential, and routing it through Key Vault would only add an unresolved-reference failure path.')
param appInsightsConnectionString string

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


var appSettings = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'ConnectionStrings__DefaultConnection', value: keyVaultRef(keyVaultUri, postgresConnectionStringSecretName) }
  { name: 'AzureAd__Instance', value: environment().authentication.loginEndpoint }
  { name: 'AzureAd__TenantId', value: azureAdTenantId }
  { name: 'AzureAd__ClientId', value: azureAdClientId }
  { name: 'AzureAd__Audience', value: azureAdAudience }
  { name: 'GraphApi__TenantId', value: graphTenantId }
  { name: 'GraphApi__ClientId', value: graphClientId }
  { name: 'GraphApi__ClientSecret', value: keyVaultRef(keyVaultUri, graphClientSecretSecretName) }
  { name: 'GraphApi__MailboxAddress', value: graphMailboxAddress }
  { name: 'OpenRouter__ApiKey', value: keyVaultRef(keyVaultUri, openRouterApiKeySecretName) }
  { name: 'OpenRouter__Model', value: openRouterModel }
  { name: 'CorsOrigins__0', value: corsOrigin }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
]

// The staging slot must never run the email poller or AI calls: before a swap it would poll the
// production mailbox with not-yet-live code, and after a swap the previous build would sit in
// staging polling forever (duplicate tickets, double AI spend - ingestion dedup on
// ExternalMessageId is not a unique index). "false" is the app's explicit opt-out for both.
var stagingAppSettings = union(appSettings, [
  { name: 'GraphApi__Enabled', value: 'false' }
  { name: 'OpenRouter__Enabled', value: 'false' }
])

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
      // Keeps the hosted email poller running; without it the app unloads after ~20 min idle.
      alwaysOn: true
    }
  }
}

// Slot-sticky setting names: a swap never moves these values between slots, so staging keeps
// ingestion disabled and production never receives the staging opt-outs, however many swaps run.
resource slotConfig 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: webApp
  name: 'slotConfigNames'
  properties: {
    appSettingNames: ['GraphApi__Enabled', 'OpenRouter__Enabled']
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
      appSettings: stagingAppSettings
      minTlsVersion: '1.2'
    }
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

// Built-in "Key Vault Secrets User" role (read secret values). The vault uses RBAC authorization.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource webAppKvRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, webApp.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource stagingSlotKvRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, stagingSlot.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: stagingSlot.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output webAppName string = webApp.name
output defaultHostName string = webApp.properties.defaultHostName
output principalId string = webApp.identity.principalId
output possibleOutboundIpAddresses array = split(webApp.properties.possibleOutboundIpAddresses, ',')
