targetScope = 'resourceGroup'

param vaultName string
param entraSecretName string
param githubTokenSecretName string
param dataProtectionKeyName string
param principalId string

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: vaultName
}

resource entraSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: vault
  name: entraSecretName
}

resource githubSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: vault
  name: githubTokenSecretName
}

resource dataProtectionKey 'Microsoft.KeyVault/vaults/keys@2023-07-01' existing = {
  parent: vault
  name: dataProtectionKeyName
}

var secretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var cryptoUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '12338af0-0e69-4776-bea7-57ae8d297424')

resource entraAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(entraSecret.id, principalId, secretsUser)
  scope: entraSecret
  properties: {
    roleDefinitionId: secretsUser
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}

resource githubAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (githubTokenSecretName != entraSecretName) {
  name: guid(githubSecret.id, principalId, secretsUser)
  scope: githubSecret
  properties: {
    roleDefinitionId: secretsUser
    principalId: principalId
    principalType: 'ServicePrincipal'
  }

}

resource dataProtectionKeyAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dataProtectionKey.id, principalId, cryptoUser)
  scope: dataProtectionKey
  properties: {
    roleDefinitionId: cryptoUser
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
