namespace PipelineForge.Core.Events;

/// <summary>
/// Raised when a pipeline run completes successfully.
/// </summary>
public sealed record PipelineRunCompleted : PipelineEvent
{
    public required string PipelineName { get; init; }
    public required string Result { get; init; }
    public required TimeSpan Duration { get; init; }
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset FinishTime { get; init; }
}
