// infra/modules/staticwebapp.bicep
@description('Azure region for the Static Web App. Must be a region Static Web Apps supports (e.g. westus2, centralus, eastus2, westeurope, eastasia) - main.bicep passes its separate staticWebAppLocation param here, not the resource group region.')
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
