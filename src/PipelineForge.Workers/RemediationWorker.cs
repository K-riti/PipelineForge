using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PipelineForge.Core.Remediation;
using PipelineForge.Infrastructure.ServiceBus;
using PipelineForge.Infrastructure.Telemetry;

namespace PipelineForge.Workers;

/// <summary>
/// Worker service that consumes and executes remediation commands.
/// </summary>
public sealed class RemediationWorker : BackgroundService
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ServiceBusProcessor _processor;
    private readonly IRemediationCommandHandler _commandHandler;
    private readonly ILogger<RemediationWorker> _logger;

    public RemediationWorker(
        IOptions<ServiceBusOptions> serviceBusOptions,
        IRemediationCommandHandler commandHandler,
        ILogger<RemediationWorker> logger)
    {
        var options = serviceBusOptions.Value;
        _commandHandler = commandHandler;
        _logger = logger;

        _serviceBusClient = new ServiceBusClient(options.ConnectionString);
        _processor = _serviceBusClient.CreateProcessor(
            options.RemediationCommandsTopic,
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

        _logger.LogInformation("Starting Remediation Worker...");

        await _processor.StartProcessingAsync(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Remediation Worker stopping...");
        }

        await _processor.StopProcessingAsync();
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        var messageBody = args.Message.Body.ToString();
        RemediationCommand? command = null;

        try
        {
            command = JsonSerializer.Deserialize<RemediationCommand>(messageBody);
            if (command == null)
            {
                _logger.LogWarning("Failed to deserialize remediation command from message {MessageId}",
                    args.Message.MessageId);
                await args.DeadLetterMessageAsync(args.Message, "DeserializationError", "Failed to deserialize command");
                return;
            }

            using var activity = OtelInstrumentation.StartRemediationActivity(
                command.Action.ToString(),
                command.PipelineId,
                command.RunId);

            var startTime = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Processing remediation command {CommandId}: {Action} for run {RunId}",
                command.CommandId, command.Action, command.RunId);

            var result = await _commandHandler.HandleAsync(command, args.CancellationToken);

            var latencyMs = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
            OtelInstrumentation.RemediationLatencyMs.Record(latencyMs,
                new KeyValuePair<string, object?>("action", command.Action.ToString()),
                new KeyValuePair<string, object?>("success", result.Success));

            if (result.Success)
            {
                OtelInstrumentation.RemediationActionsTotal.Add(1,
                    new KeyValuePair<string, object?>("action", command.Action.ToString()),
                    new KeyValuePair<string, object?>("pipeline.id", command.PipelineId));

                _logger.LogInformation(
                    "Remediation command {CommandId} executed successfully. New run: {NewRunId}",
                    command.CommandId, result.NewRunId ?? "N/A");

                await args.CompleteMessageAsync(args.Message);
            }
            else
            {
                _logger.LogWarning(
                    "Remediation command {CommandId} failed: {Error}",
                    command.CommandId, result.ErrorMessage);

                // Retry logic
                command.RetryCount++;
                if (command.RetryCount < command.MaxRetries)
                {
                    await args.AbandonMessageAsync(args.Message);
                }
                else
                {
                    await args.DeadLetterMessageAsync(
                        args.Message,
                        "MaxRetriesExceeded",
                        $"Failed after {command.MaxRetries} retries: {result.ErrorMessage}");
                }

                activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error processing remediation command. MessageId: {MessageId}",
                args.Message.MessageId);

            await args.DeadLetterMessageAsync(
                args.Message,
                "ProcessingError",
                ex.Message);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "Service Bus error in Remediation Worker. Source: {ErrorSource}",
            args.ErrorSource);

        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _processor.DisposeAsync();
        await _serviceBusClient.DisposeAsync();
    }
}
