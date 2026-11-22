// Example parameters for infra/main.bicep. Copy to main.bicepparam and fill in real values.
// Contains no secrets - the client secret (if one is used) goes into Key Vault, not here.
using './main.bicep'

param location = 'swedencentral'
param environmentName = 'prod'
param workloadName = 'dafacontracts'

// Microsoft Entra group that administers the SQL server (create it first).
param sqlAdminGroupName = 'sg-dafa-contracts-sql-admins'
param sqlAdminGroupObjectId = '00000000-0000-0000-0000-000000000000'

// Application (client) ID of the "DAFA Contracts" app registration.
param entraClientId = '00000000-0000-0000-0000-000000000000'

param appServiceSkuName = 'P0v3'
param sqlDatabaseSkuName = 'S0'
param sqlDatabaseSkuTier = 'Standard'
param aiMode = 'Heuristic'
param suppliersMode = 'Seed'
