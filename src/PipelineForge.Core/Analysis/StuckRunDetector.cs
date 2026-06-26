using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PipelineForge.Core.Analysis;

/// <summary>
/// Configuration options for stuck run detection.
/// </summary>
public sealed class StuckRunDetectorOptions
{
    public const string SectionName = "StuckRunDetector";

    /// <summary>
    /// Default timeout in minutes before a run is considered stuck.
    /// </summary>
    public int DefaultTimeoutMinutes { get; set; } = 60;

    /// <summary>
    /// Pipeline-specific timeout overrides (PipelineId -> TimeoutMinutes).
    /// </summary>
    public Dictionary<string, int> PipelineTimeouts { get; set; } = new();
}

/// <summary>
/// Detects pipeline runs that have been running longer than expected.
/// </summary>
public interface IStuckRunDetector
{
    void TrackRunStart(PipelineRunTracker tracker);
    void TrackRunComplete(string runId);
    IEnumerable<PipelineRunTracker> GetStuckRuns();
    PipelineRunTracker? GetTracker(string runId);
}

/// <summary>
/// Default implementation of <see cref="IStuckRunDetector"/>.
/// </summary>
public sealed class StuckRunDetector : IStuckRunDetector
{
    private readonly ConcurrentDictionary<string, PipelineRunTracker> _activeRuns = new();
    private readonly StuckRunDetectorOptions _options;
    private readonly ILogger<StuckRunDetector> _logger;

    public StuckRunDetector(
        IOptions<StuckRunDetectorOptions> options,
        ILogger<StuckRunDetector> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public void TrackRunStart(PipelineRunTracker tracker)
    {
        tracker.State = PipelineRunState.Running;
        tracker.StartTime = DateTimeOffset.UtcNow;

        _activeRuns.AddOrUpdate(tracker.RunId, tracker, (_, _) => tracker);

        _logger.LogInformation(
            "Tracking run {RunId} for pipeline {PipelineName} ({PipelineId})",
            tracker.RunId, tracker.PipelineName, tracker.PipelineId);
    }

    public void TrackRunComplete(string runId)
    {
        if (_activeRuns.TryRemove(runId, out var tracker))
        {
            tracker.State = PipelineRunState.Completed;
            tracker.EndTime = DateTimeOffset.UtcNow;

            _logger.LogInformation(
                "Run {RunId} completed. Duration: {Duration}",
                runId, tracker.Duration);
        }
    }

    public PipelineRunTracker? GetTracker(string runId)
    {
        _activeRuns.TryGetValue(runId, out var tracker);
        return tracker;
    }

    public IEnumerable<PipelineRunTracker> GetStuckRuns()
    {
        var now = DateTimeOffset.UtcNow;
        var stuckRuns = new List<PipelineRunTracker>();

        foreach (var tracker in _activeRuns.Values)
        {
            var timeoutMinutes = GetTimeoutForPipeline(tracker.PipelineId);
            var elapsed = now - tracker.StartTime;

            if (elapsed.TotalMinutes > timeoutMinutes)
            {
                tracker.State = PipelineRunState.Stuck;
                stuckRuns.Add(tracker);

                _logger.LogWarning(
                    "Run {RunId} for pipeline {PipelineName} is stuck. " +
                    "Running for {ElapsedMinutes:F1} minutes (timeout: {TimeoutMinutes} minutes)",
                    tracker.RunId, tracker.PipelineName, elapsed.TotalMinutes, timeoutMinutes);
            }
        }

        return stuckRuns;
    }

    private int GetTimeoutForPipeline(string pipelineId)
    {
        return _options.PipelineTimeouts.TryGetValue(pipelineId, out var timeout)
            ? timeout
            : _options.DefaultTimeoutMinutes;
    }
}
