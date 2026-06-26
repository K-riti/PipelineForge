namespace PipelineForge.Core.Remediation;

/// <summary>
/// Publishes remediation commands to the message bus.
/// </summary>
public interface IRemediationCommandPublisher
{
    Task PublishAsync(RemediationCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Handles execution of remediation commands.
/// </summary>
public interface IRemediationCommandHandler
{
    Task<RemediationResult> HandleAsync(RemediationCommand command, CancellationToken cancellationToken = default);
}
