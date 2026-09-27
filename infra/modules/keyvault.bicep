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
