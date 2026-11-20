targetScope = 'resourceGroup'

param vaultName string
param entraSecretName string
param githubTokenSecretName string
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

var secretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

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
