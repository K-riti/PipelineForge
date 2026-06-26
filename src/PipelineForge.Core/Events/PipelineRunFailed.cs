namespace PipelineForge.Core.Events;

/// <summary>
/// Raised when a pipeline run fails.
/// </summary>
public sealed record PipelineRunFailed : PipelineEvent
{
    public required string PipelineName { get; init; }
    public required string FailureReason { get; init; }
    public string? FailedStageName { get; init; }
    public string? FailedJobName { get; init; }
    public string? ErrorMessage { get; init; }
    public int FailureCount { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset FailureTime { get; init; }
}
