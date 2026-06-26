namespace PipelineForge.Core.Remediation;

/// <summary>
/// Types of remediation actions that can be performed.
/// </summary>
public enum RemediationAction
{
    None,
    Retry,
    Cancel,
    CancelAndRetry,
    NotifyOnly
}

/// <summary>
/// Command to trigger a remediation action for a pipeline run.
/// </summary>
public sealed class RemediationCommand
{
    public required string CommandId { get; init; }
    public required string RunId { get; init; }
    public required string PipelineId { get; init; }
    public required string ProjectId { get; init; }
    public required string OrganizationUrl { get; init; }
    public required RemediationAction Action { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public int RetryCount { get; set; }
    public int MaxRetries { get; init; } = 3;
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Result of a remediation action execution.
/// </summary>
public sealed class RemediationResult
{
    public required string CommandId { get; init; }
    public required bool Success { get; init; }
    public string? NewRunId { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset ExecutedAt { get; init; } = DateTimeOffset.UtcNow;
}
