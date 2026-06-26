namespace PipelineForge.Core.Events;

/// <summary>
/// Base class for all pipeline domain events.
/// </summary>
public abstract record PipelineEvent
{
    public required string PipelineId { get; init; }
    public required string RunId { get; init; }
    public required string ProjectId { get; init; }
    public required string OrganizationUrl { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? CorrelationId { get; init; }
}
