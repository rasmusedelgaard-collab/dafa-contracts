// DAFA Contracts - production skeleton for Azure.
//
// STATUS: NOT DEPLOYED. This template has been compiled (CI job "infra") but never run against
// a subscription. Review SKUs, names, networking and region availability before first use.
// See docs/RUNBOOK.md, "Moving from test to production".
//
// Scope: resource group.
//   az deployment group create -g <resource-group> -f infra/main.bicep -p infra/main.bicepparam
//
// What it creates: Log Analytics + Application Insights, App Service plan + Web App (.NET 10,
// system-assigned managed identity), Azure SQL server + database (Microsoft Entra-only
// authentication), Key Vault (RBAC), and the role assignment that lets the Web App read secrets.
// No storage account: contract files live in SharePoint in production.

targetScope = 'resourceGroup'

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------

@description('Azure region. Defaults to an EU region; keep all DAFA data in the EU.')
param location string = 'swedencentral'

@description('Short workload name used in resource names (lowercase letters and digits).')
@minLength(3)
@maxLength(15)
param workloadName string = 'dafacontracts'

@description('Environment name used in resource names.')
@allowed([
  'prod'
  'staging'
  'test'
])
param environmentName string = 'prod'

@description('App Service plan SKU. Must be Basic or higher for Always On (needed by the hourly background job).')
param appServiceSkuName string = 'P0v3'

@description('Azure SQL database SKU name.')
param sqlDatabaseSkuName string = 'S0'

@description('Azure SQL database SKU tier.')
param sqlDatabaseSkuTier string = 'Standard'

@description('Backup storage redundancy for the database.')
@allowed([
  'Geo'
  'Zone'
  'Local'
])
param sqlBackupRedundancy string = 'Geo'

@description('Point-in-time restore window in days.')
@minValue(7)
@maxValue(35)
param sqlBackupRetentionDays int = 14

@description('Display name of the Microsoft Entra group that administers the SQL server.')
param sqlAdminGroupName string

@description('Object ID of the Microsoft Entra group that administers the SQL server.')
param sqlAdminGroupObjectId string

@description('Microsoft Entra tenant ID.')
param tenantId string = subscription().tenantId

@description('Application (client) ID of the Entra app registration used for sign-in. Leave empty until it exists.')
param entraClientId string = ''

@description('Name of the Key Vault secret holding the app registration client secret (or leave the secret out and use a certificate / federated credential).')
param entraClientSecretName string = 'AzureAd--ClientSecret'

@description('AI mode: Heuristic (no external service) or AzureOpenAI.')
@allowed([
  'Heuristic'
  'AzureOpenAI'
])
param aiMode string = 'Heuristic'

@description('Supplier master source: Seed or PowerBi.')
@allowed([
  'Seed'
  'PowerBi'
])
param suppliersMode string = 'Seed'

@description('Log Analytics retention in days.')
@minValue(30)
@maxValue(730)
param logRetentionDays int = 90

@description('Tags applied to every resource.')
param tags object = {
  application: 'dafa-contracts'
  environment: environmentName
  managedBy: 'bicep'
}

// ---------------------------------------------------------------------------
// Names
// ---------------------------------------------------------------------------

var suffix = uniqueString(resourceGroup().id)
var baseName = '${workloadName}-${environmentName}'
var logAnalyticsName = 'log-${baseName}'
var appInsightsName = 'appi-${baseName}'
var appServicePlanName = 'asp-${baseName}'
var webAppName = 'app-${baseName}-${take(suffix, 6)}'
var sqlServerName = 'sql-${baseName}-${take(suffix, 6)}'
var sqlDatabaseName = 'sqldb-${baseName}'
// Key Vault names: 3-24 characters, globally unique.
var keyVaultName = 'kv-${take(workloadName, 10)}-${take(suffix, 8)}'

// Built-in role: Key Vault Secrets User.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

// ---------------------------------------------------------------------------
// Monitoring
// ---------------------------------------------------------------------------

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: logRetentionDays
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
    IngestionMode: 'LogAnalytics'
  }
}

// ---------------------------------------------------------------------------
// Key Vault (RBAC authorization, purge protection)
// ---------------------------------------------------------------------------

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    tenantId: tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    // Hardening step for later: private endpoint + publicNetworkAccess 'Disabled'.
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

// ---------------------------------------------------------------------------
// Azure SQL (Microsoft Entra-only authentication, no SQL logins)
// ---------------------------------------------------------------------------

resource sqlServer 'Microsoft.Sql/servers@2021-11-01' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    minimalTlsVersion: '1.2'
    // Hardening step for later: private endpoint + publicNetworkAccess 'Disabled'.
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: sqlAdminGroupName
      sid: sqlAdminGroupObjectId
      tenantId: tenantId
      azureADOnlyAuthentication: true
    }
  }
}

// Lets Azure services (the Web App) reach the server. Replace with a private endpoint or
// VNet integration when the network design is decided.
resource sqlAllowAzureServices 'Microsoft.Sql/servers/firewallRules@2021-11-01' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2021-11-01' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  tags: tags
  sku: {
    name: sqlDatabaseSkuName
    tier: sqlDatabaseSkuTier
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    requestedBackupStorageRedundancy: sqlBackupRedundancy
  }
}

resource sqlBackupPolicy 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2021-11-01' = {
  parent: sqlDatabase
  name: 'default'
  properties: {
    retentionDays: sqlBackupRetentionDays
  }
}

// ---------------------------------------------------------------------------
// App Service (Linux, .NET 10)
// ---------------------------------------------------------------------------

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: appServiceSkuName
  }
  properties: {
    reserved: true
  }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  tags: tags
  kind: 'app,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    clientAffinityEnabled: false
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/healthz'
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        // Configuration keys follow docs/BUILD_BRIEF.md; the backend section of docs/STATUS.md
        // is authoritative for exact names. "__" maps to ":" in ASP.NET Core configuration.
        {
          name: 'Auth__Mode'
          value: 'Entra'
        }
        {
          name: 'AzureAd__Instance'
          value: environment().authentication.loginEndpoint
        }
        {
          name: 'AzureAd__TenantId'
          value: tenantId
        }
        {
          name: 'AzureAd__ClientId'
          value: entraClientId
        }
        {
          name: 'AzureAd__ClientSecret'
          value: '@Microsoft.KeyVault(VaultName=${keyVault.name};SecretName=${entraClientSecretName})'
        }
        {
          name: 'Database__Provider'
          value: 'SqlServer'
        }
        // Passwordless: the Web App signs in to SQL with its managed identity.
        {
          name: 'ConnectionStrings__Default'
          value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
        }
        {
          name: 'Storage__Mode'
          value: 'SharePoint'
        }
        {
          name: 'Ai__Mode'
          value: aiMode
        }
        {
          name: 'Suppliers__Mode'
          value: suppliersMode
        }
        {
          name: 'Notifications__Mode'
          value: 'Graph'
        }
        {
          name: 'Seed__DemoData'
          value: 'false'
        }
        {
          name: 'KeyVault__Uri'
          value: keyVault.properties.vaultUri
        }
      ]
    }
  }
}

// Deployments must use Entra identities (for example GitHub OIDC), not basic publishing credentials.
resource webAppFtpPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: webApp
  name: 'ftp'
  properties: {
    allow: false
  }
}

resource webAppScmPolicy 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2023-12-01' = {
  parent: webApp
  name: 'scm'
  properties: {
    allow: false
  }
}

resource webAppKeyVaultAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, webApp.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------------------
// Diagnostics to Log Analytics
// ---------------------------------------------------------------------------

resource webAppDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: webApp
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        category: 'AppServiceHTTPLogs'
        enabled: true
      }
      {
        category: 'AppServiceConsoleLogs'
        enabled: true
      }
      {
        category: 'AppServiceAppLogs'
        enabled: true
      }
      {
        category: 'AppServicePlatformLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
  }
}

resource keyVaultDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: keyVault
  properties: {
    workspaceId: logAnalytics.id
    logs: [
      {
        category: 'AuditEvent'
        enabled: true
      }
    ]
  }
}

// ---------------------------------------------------------------------------
// Outputs (no secrets)
// ---------------------------------------------------------------------------

output webAppName string = webApp.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output webAppPrincipalId string = webApp.identity.principalId
output sqlServerName string = sqlServer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabase.name
output keyVaultName string = keyVault.name
output keyVaultUri string = keyVault.properties.vaultUri
output appInsightsName string = appInsights.name
output logAnalyticsWorkspaceId string = logAnalytics.id
