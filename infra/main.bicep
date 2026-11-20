targetScope = 'resourceGroup'

@description('Dedicated App Service and plan names. Never share the plan or use deployment slots.')
param webAppName string
param appServicePlanName string
param identityName string
param location string = resourceGroup().location
@allowed([
  'P1v3'
  'P2v3'
  'P3v3'
])
param appServiceSku string = 'P1v3'

@description('Existing RBAC-enabled Key Vault and secret names. Values are never passed to ARM.')
param keyVaultName string
param keyVaultResourceGroup string
param entraSecretName string
param githubTokenSecretName string

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

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
}

module vaultAccess 'vault-access.bicep' = {
  name: '${deployment().name}-vault'
  scope: resourceGroup(keyVaultResourceGroup)
  params: {
    vaultName: keyVaultName
    entraSecretName: entraSecretName
    githubTokenSecretName: githubTokenSecretName
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
      appCommandLine: 'sleep infinity'
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

resource appSettings 'Microsoft.Web/sites/config@2023-12-01' = {
  parent: app
  name: 'appsettings'
  properties: {
    WEBSITES_ENABLE_APP_SERVICE_STORAGE: 'true'
    WEBSITE_RUN_FROM_PACKAGE: '1'
    WEBSITE_DISABLE_OVERLAPPED_RECYCLING: '1'
    Entra__TenantId: entraTenantId
    Entra__ClientId: entraClientId
    Entra__ApiAudience: entraApiAudience
    Entra__PublicBaseUrl: 'https://${app.properties.defaultHostName}'
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
    AzureVmScaleSet__Enabled: string(vmssEnabled)
    AzureVmScaleSet__SubscriptionId: subscription().subscriptionId
    AzureVmScaleSet__ResourceGroupName: vmssResourceGroup
    AzureVmScaleSet__ScaleSetName: vmssName
    AZURE_CLIENT_ID: identity.properties.clientId
  }
}

output appUrl string = 'https://${app.properties.defaultHostName}'
