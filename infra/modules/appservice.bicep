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
