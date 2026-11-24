targetScope = 'resourceGroup'

param vmssName string
param principalId string
param roleDefinitionId string

resource vmss 'Microsoft.Compute/virtualMachineScaleSets@2024-11-01' existing = {
  name: vmssName
}

resource access 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(vmss.id, principalId, roleDefinitionId)
  scope: vmss
  properties: {
    roleDefinitionId: roleDefinitionId
    principalId: principalId
    principalType: 'ServicePrincipal'
  }
}
