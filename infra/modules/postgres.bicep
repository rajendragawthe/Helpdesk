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

resource postgresServer 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
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

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgresServer
  name: databaseName
}

resource allowAzureServices 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgresServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource outboundIpRules 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = [
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
