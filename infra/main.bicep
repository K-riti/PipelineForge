// PipelineForge Infrastructure - Azure Bicep Template
// Deploys: Service Bus, Key Vault, Application Insights, Container Apps

@description('The location for all resources')
param location string = resourceGroup().location

@description('Environment name (dev, staging, prod)')
@allowed(['dev', 'staging', 'prod'])
param environment string = 'dev'

@description('Base name for resources')
param baseName string = 'pipelineforge'

var resourceSuffix = '${baseName}-${environment}'
var serviceBusNamespaceName = 'sb-${resourceSuffix}'
var keyVaultName = 'kv-${replace(resourceSuffix, '-', '')}'
var appInsightsName = 'ai-${resourceSuffix}'
var logAnalyticsName = 'la-${resourceSuffix}'
var containerAppEnvName = 'cae-${resourceSuffix}'
var containerAppName = 'ca-${resourceSuffix}'

// Log Analytics Workspace
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsName
  location: location
  properties: {
	sku: {
	  name: 'PerGB2018'
	}
	retentionInDays: 30
  }
}

// Application Insights
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
	Application_Type: 'web'
	WorkspaceResourceId: logAnalytics.id
	IngestionMode: 'LogAnalytics'
	publicNetworkAccessForIngestion: 'Enabled'
	publicNetworkAccessForQuery: 'Enabled'
  }
}

// Service Bus Namespace
resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: serviceBusNamespaceName
  location: location
  sku: {
	name: 'Standard'
	tier: 'Standard'
  }
  properties: {}
}

// Pipeline Events Topic
resource pipelineEventsTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: 'pipeline-events'
  properties: {
	maxSizeInMegabytes: 1024
	defaultMessageTimeToLive: 'P14D'
	enableBatchedOperations: true
  }
}

// Pipeline Events Subscription
resource pipelineEventsSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: pipelineEventsTopic
  name: 'pipelineforge-worker'
  properties: {
	maxDeliveryCount: 3
	lockDuration: 'PT5M'
	deadLetteringOnMessageExpiration: true
	enableBatchedOperations: true
  }
}

// Remediation Commands Topic
resource remediationTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: 'remediation-commands'
  properties: {
	maxSizeInMegabytes: 1024
	defaultMessageTimeToLive: 'P1D'
	enableBatchedOperations: true
  }
}

// Remediation Commands Subscription
resource remediationSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: remediationTopic
  name: 'pipelineforge-worker'
  properties: {
	maxDeliveryCount: 3
	lockDuration: 'PT5M'
	deadLetteringOnMessageExpiration: true
	enableBatchedOperations: true
  }
}

// Key Vault
resource keyVault 'Microsoft.KeyVault/vaults@2023-02-01' = {
  name: keyVaultName
  location: location
  properties: {
	sku: {
	  family: 'A'
	  name: 'standard'
	}
	tenantId: subscription().tenantId
	enableRbacAuthorization: true
	enableSoftDelete: true
	softDeleteRetentionInDays: 7
	enablePurgeProtection: false // Set to true for production
  }
}

// Container Apps Environment
resource containerAppEnvironment 'Microsoft.App/managedEnvironments@2023-05-01' = {
  name: containerAppEnvName
  location: location
  properties: {
	appLogsConfiguration: {
	  destination: 'log-analytics'
	  logAnalyticsConfiguration: {
		customerId: logAnalytics.properties.customerId
		sharedKey: logAnalytics.listKeys().primarySharedKey
	  }
	}
	daprAIConnectionString: appInsights.properties.ConnectionString
  }
}

// Container App (PipelineForge Worker)
resource containerApp 'Microsoft.App/containerApps@2023-05-01' = {
  name: containerAppName
  location: location
  identity: {
	type: 'SystemAssigned'
  }
  properties: {
	managedEnvironmentId: containerAppEnvironment.id
	configuration: {
	  activeRevisionsMode: 'Single'
	  secrets: [
		{
		  name: 'servicebus-connection'
		  value: serviceBusNamespace.listKeys().primaryConnectionString
		}
		{
		  name: 'appinsights-connection'
		  value: appInsights.properties.ConnectionString
		}
	  ]
	}
	template: {
	  containers: [
		{
		  name: 'pipelineforge-worker'
		  image: 'mcr.microsoft.com/dotnet/runtime:8.0' // Replace with your image
		  resources: {
			cpu: json('0.5')
			memory: '1Gi'
		  }
		  env: [
			{
			  name: 'AzureServiceBus__ConnectionString'
			  secretRef: 'servicebus-connection'
			}
			{
			  name: 'OpenTelemetry__ConnectionString'
			  secretRef: 'appinsights-connection'
			}
			{
			  name: 'KeyVault__Uri'
			  value: keyVault.properties.vaultUri
			}
		  ]
		}
	  ]
	  scale: {
		minReplicas: 1
		maxReplicas: 5
		rules: [
		  {
			name: 'servicebus-scale'
			custom: {
			  type: 'azure-servicebus'
			  metadata: {
				topicName: 'pipeline-events'
				subscriptionName: 'pipelineforge-worker'
				messageCount: '10'
			  }
			  auth: [
				{
				  secretRef: 'servicebus-connection'
				  triggerParameter: 'connection'
				}
			  ]
			}
		  }
		]
	  }
	}
  }
}

// Role Assignment: Container App -> Key Vault Secrets User
resource keyVaultRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, containerApp.id, 'Key Vault Secrets User')
  scope: keyVault
  properties: {
	roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') // Key Vault Secrets User
	principalId: containerApp.identity.principalId
	principalType: 'ServicePrincipal'
  }
}

// Role Assignment: Container App -> Service Bus Data Receiver
resource serviceBusReceiverRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, containerApp.id, 'Service Bus Data Receiver')
  scope: serviceBusNamespace
  properties: {
	roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0') // Azure Service Bus Data Receiver
	principalId: containerApp.identity.principalId
	principalType: 'ServicePrincipal'
  }
}

// Role Assignment: Container App -> Service Bus Data Sender
resource serviceBusSenderRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, containerApp.id, 'Service Bus Data Sender')
  scope: serviceBusNamespace
  properties: {
	roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39') // Azure Service Bus Data Sender
	principalId: containerApp.identity.principalId
	principalType: 'ServicePrincipal'
  }
}

// Outputs
output serviceBusNamespace string = serviceBusNamespace.name
output serviceBusConnectionString string = serviceBusNamespace.listKeys().primaryConnectionString
output keyVaultUri string = keyVault.properties.vaultUri
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output containerAppFqdn string = containerApp.properties.configuration.ingress.fqdn
