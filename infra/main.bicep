targetScope = 'resourceGroup'

param company string = 'mm'
param product string = 'ghshr'
param environmentName string = 'prod'
@description('Short location code used in resource names.')
param locationShort string = 'uks'
param location string = 'uksouth'

@description('Dedicated App Service and plan names. The plan must not be shared with other apps.')
param webAppName string = '${company}-${product}-${locationShort}-${environmentName}-app'
param workerAppName string = '${company}-${product}-${locationShort}-${environmentName}-worker'
param appServicePlanName string = '${company}-${product}-${locationShort}-${environmentName}-asp'
param identityName string = '${company}-${product}-${locationShort}-${environmentName}-id'
@allowed([
  'P1v3'
  'P2v3'
  'P3v3'
])
param appServiceSku string = 'P1v3'

@description('Existing RBAC-enabled Key Vault and secret names. Values are never passed to ARM.')
param keyVaultName string = '${company}-${product}-${locationShort}-${environmentName}-kv'
param keyVaultResourceGroup string = resourceGroup().name
param entraSecretName string
param githubTokenSecretName string
@description('Existing Key Vault key used to wrap the shared ASP.NET Core data-protection key ring.')
param dataProtectionKeyName string

param entraTenantId string
param entraClientId string
param entraApiAudience string
param entraReadOnlyGroupId string
param entraOperatorGroupId string
param githubOrganization string
@minValue(1)
param githubRunnerGroupId int
param runnerLabel string = 'linux-dotnet'
param imageWorkloadClientId string

@description('Optional VMSS in the current subscription. The custom role must be created beforehand.')
param vmssEnabled bool = false
param vmssResourceGroup string = ''
param vmssName string = ''
param vmssCustomRoleDefinitionId string = ''

@description('Globally unique storage account name; override for an existing account.')
param storageAccountName string = '${take(toLower(replace('${company}${product}${locationShort}${environmentName}', '-', '')), 14)}st${take(uniqueString(resourceGroup().id), 8)}'

@description('Disable only for a controlled migration of existing file-based state.')
param tableStorageEnabled bool = true

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource capacityTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'RunnerCapacity'
}

resource imageTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'RunnerImages'
}

resource vmTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'RunnerVms'
}

resource rolloutTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'RunnerRollouts'
}

resource buildQueueTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  parent: tableService
  name: 'RunnerBuildQueue'
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource dataProtectionContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'dataprotection'
  properties: {
    publicAccess: 'None'
  }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

var tableDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')

resource capacityTableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(capacityTable.id, identity.id, tableDataContributor)
  scope: capacityTable
  properties: {
    roleDefinitionId: tableDataContributor
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource imageTableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(imageTable.id, identity.id, tableDataContributor)
  scope: imageTable
  properties: {
    roleDefinitionId: tableDataContributor
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource vmTableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vmTable.id, identity.id, tableDataContributor)
  scope: vmTable
  properties: {
    roleDefinitionId: tableDataContributor
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource rolloutTableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(rolloutTable.id, identity.id, tableDataContributor)
  scope: rolloutTable
  properties: {
    roleDefinitionId: tableDataContributor
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource buildQueueTableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(buildQueueTable.id, identity.id, tableDataContributor)
  scope: buildQueueTable
  properties: {
    roleDefinitionId: tableDataContributor
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

var blobDataContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource dataProtectionBlobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dataProtectionContainer.id, identity.id, blobDataContributor)
  scope: dataProtectionContainer
  properties: {
    roleDefinitionId: blobDataContributor
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

module vaultAccess 'vault-access.bicep' = {
  name: '${deployment().name}-vault'
  scope: resourceGroup(keyVaultResourceGroup)
  params: {
    vaultName: keyVaultName
    entraSecretName: entraSecretName
    githubTokenSecretName: githubTokenSecretName
    dataProtectionKeyName: dataProtectionKeyName
    principalId: identity.properties.principalId
  }
}

module vmssAccess 'vmss-access.bicep' = if (vmssEnabled) {
  name: '${deployment().name}-vmss'
  scope: resourceGroup(vmssResourceGroup)
  params: {
    vmssName: vmssName
    principalId: identity.properties.principalId
    roleDefinitionId: vmssCustomRoleDefinitionId
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  kind: 'linux'
  sku: {
    name: appServiceSku
    tier: 'PremiumV3'
    capacity: 1
  }
  properties: {
    reserved: true
    perSiteScaling: false
    maximumElasticWorkerCount: 1
  }
}

resource app 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identity.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: ''
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      http20Enabled: true
      remoteDebuggingEnabled: false
      numberOfWorkers: 1
    }
  }
  dependsOn: [
    vaultAccess
    vmssAccess
  ]
}

var commonSettings = {
    WEBSITES_ENABLE_APP_SERVICE_STORAGE: 'true'
    WEBSITE_RUN_FROM_PACKAGE: '1'
    WEBSITE_DISABLE_OVERLAPPED_RECYCLING: '1'
    WEBSITE_SWAP_WARMUP_PING_PATH: '/health/ready'
    WEBSITE_SWAP_WARMUP_PING_STATUSES: '200'
    Entra__TenantId: entraTenantId
    Entra__ClientId: entraClientId
    Entra__ApiAudience: entraApiAudience
    Entra__ReadOnlyGroupId: entraReadOnlyGroupId
    Entra__OperatorGroupId: entraOperatorGroupId
    Entra__ClientSecret: '@Microsoft.KeyVault(SecretUri=https://${keyVaultName}${environment().suffixes.keyvaultDns}/secrets/${entraSecretName}/)'
    GitHub__Organization: githubOrganization
    GitHub__RunnerGroupId: string(githubRunnerGroupId)
    GitHub__Token: '@Microsoft.KeyVault(SecretUri=https://${keyVaultName}${environment().suffixes.keyvaultDns}/secrets/${githubTokenSecretName}/)'
    Runner__Label: runnerLabel
    RunnerCapacity__StateFilePath: '/home/runner-control-plane/runner-capacity-state.json'
    RunnerImageBuild__StateFilePath: '/home/runner-control-plane/runner-image-state.json'
    RunnerImageBuild__WorkloadClientId: imageWorkloadClientId
    TableStorage__Enabled: string(tableStorageEnabled)
    TableStorage__Endpoint: storage.properties.primaryEndpoints.table
    DataProtection__BlobUri: '${storage.properties.primaryEndpoints.blob}dataprotection/keys.xml'
    DataProtection__KeyUri: 'https://${keyVaultName}${environment().suffixes.keyvaultDns}/keys/${dataProtectionKeyName}'
    AzureVmScaleSet__Enabled: string(vmssEnabled)
    AzureVmScaleSet__SubscriptionId: subscription().subscriptionId
    AzureVmScaleSet__ResourceGroupName: vmssResourceGroup
    AzureVmScaleSet__ScaleSetName: vmssName
    AZURE_CLIENT_ID: identity.properties.clientId
}

resource appSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: app
  name: 'appsettings'
  properties: union(commonSettings, {
    Entra__PublicBaseUrl: 'https://${app.properties.defaultHostName}'
    BackgroundWorkers__Enabled: 'false'
    BackgroundWorkers__WorkerOnly: 'false'
  })
}

resource workerApp 'Microsoft.Web/sites@2023-12-01' = {
  name: workerAppName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identity.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: ''
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      http20Enabled: true
      remoteDebuggingEnabled: false
      numberOfWorkers: 1
    }
  }
  dependsOn: [
    vaultAccess
    vmssAccess
  ]
}

resource workerAppSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: workerApp
  name: 'appsettings'
  properties: union(commonSettings, {
    Entra__PublicBaseUrl: 'https://${app.properties.defaultHostName}'
    BackgroundWorkers__Enabled: 'true'
    BackgroundWorkers__WorkerOnly: 'true'
  })
}

resource stagingSlot 'Microsoft.Web/sites/slots@2023-12-01' = {
  parent: app
  name: 'staging'
  location: location
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${identity.id}': {}
    }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    keyVaultReferenceIdentity: identity.id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      appCommandLine: ''
      alwaysOn: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      http20Enabled: true
      remoteDebuggingEnabled: false
      numberOfWorkers: 1
    }
  }
}

resource stagingAppSettings 'Microsoft.Web/sites/slots/config@2023-12-01' = {
  parent: stagingSlot
  name: 'appsettings'
  properties: union(commonSettings, {
    Entra__PublicBaseUrl: 'https://${stagingSlot.properties.defaultHostName}'
    BackgroundWorkers__Enabled: 'false'
    BackgroundWorkers__WorkerOnly: 'false'
  })
}

resource slotConfigNames 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: app
  name: 'slotConfigNames'
  properties: {
    appSettingNames: [
      'Entra__PublicBaseUrl'
    ]
  }
}

output appUrl string = 'https://${app.properties.defaultHostName}'
output stagingUrl string = 'https://${stagingSlot.properties.defaultHostName}'
output workerUrl string = 'https://${workerApp.properties.defaultHostName}'
output tableStorageAccountName string = storage.name
output tableStorageEndpoint string = storage.properties.primaryEndpoints.table
