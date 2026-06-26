namespace PipelineForge.Core.Events;

/// <summary>
/// Raised when a pipeline run starts executing.
/// </summary>
public sealed record PipelineRunStarted : PipelineEvent
{
    public required string PipelineName { get; init; }
    public required string TriggerReason { get; init; }
    public string? SourceBranch { get; init; }
    public string? SourceCommit { get; init; }
    public string? RequestedBy { get; init; }
}
