namespace PipelineForge.Core.Analysis;

/// <summary>
/// Represents the current state of a pipeline run.
/// </summary>
public enum PipelineRunState
{
    Unknown,
    Queued,
    Running,
    Completed,
    Failed,
    Canceled,
    Stuck
}

/// <summary>
/// Tracks the state of an individual pipeline run.
/// </summary>
public sealed class PipelineRunTracker
{
    public required string RunId { get; init; }
    public required string PipelineId { get; init; }
    public required string PipelineName { get; init; }
    public required string ProjectId { get; init; }
    public required string OrganizationUrl { get; init; }
    public PipelineRunState State { get; set; } = PipelineRunState.Unknown;
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public TimeSpan? Duration => EndTime.HasValue ? EndTime.Value - StartTime : null;
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
}
