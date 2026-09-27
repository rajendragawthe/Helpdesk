// infra/modules/keyvault.bicep
@description('Azure region for the Key Vault.')
param location string

@description('Globally-unique Key Vault name (3-24 alphanumeric/hyphen characters).')
param keyVaultName string

@description('Entra tenant ID that owns this subscription.')
param tenantId string

@description('Optional Entra object ID of the human deployer. When set, grants "Key Vault Secrets Officer" so they can set secret values (runbook step 7). Get it with: az ad signed-in-user show --query id -o tsv')
param deployerObjectId string = ''

@description('Optional Entra object ID of the GitHub Actions (OIDC) service principal. When set, grants "Key Vault Secrets User" so deploy.yml can read the connection string for migrations.')
param ciPrincipalObjectId string = ''

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: tenantId
    // RBAC (not access policies): data-plane access is granted with role assignments, so the
    // deployer, the CD workflow's principal and the App Service identities are all handled the same way.
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
  }
}

// Built-in Key Vault data-plane roles.
var keyVaultSecretsOfficerRoleId = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource deployerSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerObjectId)) {
  name: guid(keyVault.id, deployerObjectId, keyVaultSecretsOfficerRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficerRoleId)
    principalId: deployerObjectId
    principalType: 'User'
  }
}

resource ciPrincipalSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(ciPrincipalObjectId)) {
  name: guid(keyVault.id, ciPrincipalObjectId, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: ciPrincipalObjectId
    principalType: 'ServicePrincipal'
  }
}

output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
