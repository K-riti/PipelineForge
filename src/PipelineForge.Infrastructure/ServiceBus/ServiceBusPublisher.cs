using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PipelineForge.Core.Remediation;

namespace PipelineForge.Infrastructure.ServiceBus;

/// <summary>
/// Configuration options for Azure Service Bus.
/// </summary>
public sealed class ServiceBusOptions
{
    public const string SectionName = "AzureServiceBus";

    /// <summary>
    /// Connection string for Azure Service Bus.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Topic name for pipeline events.
    /// </summary>
    public string PipelineEventsTopic { get; set; } = "pipeline-events";

    /// <summary>
    /// Topic name for remediation commands.
    /// </summary>
    public string RemediationCommandsTopic { get; set; } = "remediation-commands";

    /// <summary>
    /// Subscription name for this worker instance.
    /// </summary>
    public string SubscriptionName { get; set; } = "pipelineforge-worker";

    /// <summary>
    /// Maximum number of concurrent message processing calls.
    /// </summary>
    public int MaxConcurrentCalls { get; set; } = 10;

    /// <summary>
    /// Maximum auto-lock renewal duration in minutes.
    /// </summary>
    public int MaxAutoLockRenewalMinutes { get; set; } = 5;
}

/// <summary>
/// Publishes messages to Azure Service Bus topics.
/// </summary>
public sealed class ServiceBusPublisher : IRemediationCommandPublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _remediationSender;
    private readonly ILogger<ServiceBusPublisher> _logger;

    public ServiceBusPublisher(
        IOptions<ServiceBusOptions> options,
        ILogger<ServiceBusPublisher> logger)
    {
        var config = options.Value;
        _logger = logger;

        _client = new ServiceBusClient(config.ConnectionString);
        _remediationSender = _client.CreateSender(config.RemediationCommandsTopic);
    }

    public async Task PublishAsync(RemediationCommand command, CancellationToken cancellationToken = default)
    {
        var messageBody = JsonSerializer.Serialize(command);
        var message = new ServiceBusMessage(messageBody)
        {
            MessageId = command.CommandId,
            Subject = command.Action.ToString(),
            ContentType = "application/json",
            ApplicationProperties =
            {
                ["PipelineId"] = command.PipelineId,
                ["RunId"] = command.RunId,
                ["Action"] = command.Action.ToString()
            }
        };

        await _remediationSender.SendMessageAsync(message, cancellationToken);

        _logger.LogInformation(
            "Published remediation command {CommandId} to Service Bus",
            command.CommandId);
    }

    public async ValueTask DisposeAsync()
    {
        await _remediationSender.DisposeAsync();
        await _client.DisposeAsync();
    }
}
