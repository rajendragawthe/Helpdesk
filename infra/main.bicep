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
