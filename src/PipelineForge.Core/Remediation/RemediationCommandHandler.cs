using Microsoft.Extensions.Logging;

namespace PipelineForge.Core.Remediation;

/// <summary>
/// Handles remediation command execution by calling the Azure DevOps API.
/// </summary>
public sealed class RemediationCommandHandler : IRemediationCommandHandler
{
    private readonly IAzureDevOpsClient _adoClient;
    private readonly ILogger<RemediationCommandHandler> _logger;

    public RemediationCommandHandler(
        IAzureDevOpsClient adoClient,
        ILogger<RemediationCommandHandler> logger)
    {
        _adoClient = adoClient;
        _logger = logger;
    }

    public async Task<RemediationResult> HandleAsync(
        RemediationCommand command,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Executing remediation command {CommandId}: {Action} for run {RunId}",
            command.CommandId, command.Action, command.RunId);

        try
        {
            return command.Action switch
            {
                RemediationAction.Retry => await RetryRunAsync(command, cancellationToken),
                RemediationAction.Cancel => await CancelRunAsync(command, cancellationToken),
                RemediationAction.CancelAndRetry => await CancelAndRetryRunAsync(command, cancellationToken),
                RemediationAction.NotifyOnly => NotifyOnly(command),
                _ => new RemediationResult
                {
                    CommandId = command.CommandId,
                    Success = false,
                    ErrorMessage = $"Unknown action: {command.Action}"
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to execute remediation command {CommandId}",
                command.CommandId);

            return new RemediationResult
            {
                CommandId = command.CommandId,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private async Task<RemediationResult> RetryRunAsync(
        RemediationCommand command,
        CancellationToken cancellationToken)
    {
        var newRunId = await _adoClient.TriggerPipelineRunAsync(
            command.OrganizationUrl,
            command.ProjectId,
            command.PipelineId,
            cancellationToken);

        _logger.LogInformation(
            "Triggered new run {NewRunId} for pipeline {PipelineId}",
            newRunId, command.PipelineId);

        return new RemediationResult
        {
            CommandId = command.CommandId,
            Success = true,
            NewRunId = newRunId
        };
    }

    private async Task<RemediationResult> CancelRunAsync(
        RemediationCommand command,
        CancellationToken cancellationToken)
    {
        await _adoClient.CancelPipelineRunAsync(
            command.OrganizationUrl,
            command.ProjectId,
            command.RunId,
            cancellationToken);

        _logger.LogInformation(
            "Cancelled run {RunId} for pipeline {PipelineId}",
            command.RunId, command.PipelineId);

        return new RemediationResult
        {
            CommandId = command.CommandId,
            Success = true
        };
    }

    private async Task<RemediationResult> CancelAndRetryRunAsync(
        RemediationCommand command,
        CancellationToken cancellationToken)
    {
        // First cancel the stuck run
        await _adoClient.CancelPipelineRunAsync(
            command.OrganizationUrl,
            command.ProjectId,
            command.RunId,
            cancellationToken);

        _logger.LogInformation("Cancelled stuck run {RunId}", command.RunId);

        // Then trigger a new run
        var newRunId = await _adoClient.TriggerPipelineRunAsync(
            command.OrganizationUrl,
            command.ProjectId,
            command.PipelineId,
            cancellationToken);

        _logger.LogInformation(
            "Triggered new run {NewRunId} after cancelling {OldRunId}",
            newRunId, command.RunId);

        return new RemediationResult
        {
            CommandId = command.CommandId,
            Success = true,
            NewRunId = newRunId
        };
    }

    private RemediationResult NotifyOnly(RemediationCommand command)
    {
        _logger.LogInformation(
            "Notification only for command {CommandId}: {Reason}",
            command.CommandId, command.Reason);

        return new RemediationResult
        {
            CommandId = command.CommandId,
            Success = true
        };
    }
}

/// <summary>
/// Interface for Azure DevOps REST API client operations.
/// </summary>
public interface IAzureDevOpsClient
{
    Task<string> TriggerPipelineRunAsync(
        string organizationUrl,
        string projectId,
        string pipelineId,
        CancellationToken cancellationToken = default);

    Task CancelPipelineRunAsync(
        string organizationUrl,
        string projectId,
        string runId,
        CancellationToken cancellationToken = default);
}
