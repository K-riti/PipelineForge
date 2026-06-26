using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PipelineForge.Core.Events;
using PipelineForge.Infrastructure.AzureDevOps;
using PipelineForge.Infrastructure.ServiceBus;
using PipelineForge.Infrastructure.Telemetry;

namespace PipelineForge.Workers;

/// <summary>
/// Worker service that ingests pipeline events from Azure Service Bus.
/// </summary>
public sealed class EventIngestionWorker : BackgroundService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ServiceBusProcessor _processor;
    private readonly IAdoWebhookReceiver _webhookReceiver;
    private readonly IPipelineEventChannel _eventChannel;
    private readonly ILogger<EventIngestionWorker> _logger;

    public EventIngestionWorker(
        IOptions<ServiceBusOptions> serviceBusOptions,
        IAdoWebhookReceiver webhookReceiver,
        IPipelineEventChannel eventChannel,
        ILogger<EventIngestionWorker> logger)
    {
        var options = serviceBusOptions.Value;
        _webhookReceiver = webhookReceiver;
        _eventChannel = eventChannel;
        _logger = logger;

        _serviceBusClient = new ServiceBusClient(options.ConnectionString);
        _processor = _serviceBusClient.CreateProcessor(
            options.PipelineEventsTopic,
            options.SubscriptionName,
            new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = options.MaxConcurrentCalls,
                AutoCompleteMessages = false,
                MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(options.MaxAutoLockRenewalMinutes)
            });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _processor.ProcessMessageAsync += ProcessMessageAsync;
        _processor.ProcessErrorAsync += ProcessErrorAsync;

        _logger.LogInformation("Starting Event Ingestion Worker...");

        await _processor.StartProcessingAsync(stoppingToken);

        // Keep the worker running until cancellation is requested
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Event Ingestion Worker stopping...");
        }

        await _processor.StopProcessingAsync();
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var messageBody = args.Message.Body.ToString();

        using var activity = OtelInstrumentation.StartEventIngestionActivity(
            args.Message.Subject ?? "unknown",
            args.Message.ApplicationProperties.TryGetValue("PipelineId", out var pipelineId) 
                ? pipelineId?.ToString() ?? "unknown" 
                : "unknown",
            args.Message.ApplicationProperties.TryGetValue("RunId", out var runId) 
                ? runId?.ToString() ?? "unknown" 
                : "unknown");

        try
        {
            _logger.LogDebug(
                "Processing message {MessageId} from Service Bus",
                args.Message.MessageId);

            var pipelineEvent = await _webhookReceiver.ParseWebhookAsync(
                messageBody,
                args.CancellationToken);

            if (pipelineEvent != null)
            {
                await _eventChannel.Writer.WriteAsync(pipelineEvent, args.CancellationToken);

                OtelInstrumentation.EventsIngested.Add(1,
                    new KeyValuePair<string, object?>("event.type", pipelineEvent.GetType().Name));

                _logger.LogInformation(
                    "Ingested event {EventType} for pipeline {PipelineId}, run {RunId}",
                    pipelineEvent.GetType().Name,
                    pipelineEvent.PipelineId,
                    pipelineEvent.RunId);
            }

            await args.CompleteMessageAsync(args.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing message {MessageId}. Moving to dead-letter queue.",
                args.Message.MessageId);

            await args.DeadLetterMessageAsync(
                args.Message,
                "ProcessingError",
                ex.Message);

            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "Service Bus error. Source: {ErrorSource}, Namespace: {Namespace}, Entity: {Entity}",
            args.ErrorSource,
            args.FullyQualifiedNamespace,
            args.EntityPath);

        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _processor.DisposeAsync();
        await _serviceBusClient.DisposeAsync();
    }
}
