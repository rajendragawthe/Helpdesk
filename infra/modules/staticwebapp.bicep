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
