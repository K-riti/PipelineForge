using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PipelineForge.Core.Events;
using PipelineForge.Core.Remediation;

namespace PipelineForge.Core.Analysis;

/// <summary>
/// Configuration options for pipeline health analysis.
/// </summary>
public sealed class PipelineHealthAnalyserOptions
{
    public const string SectionName = "PipelineHealthAnalyser";

    /// <summary>
    /// Number of consecutive failures before triggering remediation.
    /// </summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>
    /// Time window in minutes for counting consecutive failures.
    /// </summary>
    public int FailureWindowMinutes { get; set; } = 30;

    /// <summary>
    /// Interval in seconds to check for stuck runs.
    /// </summary>
    public int StuckRunCheckIntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Analyzes pipeline health and triggers remediation when issues are detected.
/// </summary>
public interface IPipelineHealthAnalyser
{
    Task ProcessEventAsync(PipelineEvent @event, CancellationToken cancellationToken = default);
    Task<IEnumerable<RemediationCommand>> CheckForIssuesAsync(CancellationToken cancellationToken = default);
    PipelineHealthSummary GetHealthSummary(string pipelineId);
}

/// <summary>
/// Summary of pipeline health metrics.
/// </summary>
public sealed class PipelineHealthSummary
{
    public required string PipelineId { get; init; }
    public int ActiveRuns { get; init; }
    public int StuckRuns { get; init; }
    public int RecentFailures { get; init; }
    public int TotalCompletedRuns { get; init; }
    public TimeSpan? AverageDuration { get; init; }
    public DateTimeOffset? LastRunTime { get; init; }
}

/// <summary>
/// Default implementation of <see cref="IPipelineHealthAnalyser"/>.
/// </summary>
public sealed class PipelineHealthAnalyser : IPipelineHealthAnalyser
{
    private readonly IStuckRunDetector _stuckRunDetector;
    private readonly IRemediationCommandPublisher _remediationPublisher;
    private readonly PipelineHealthAnalyserOptions _options;
    private readonly ILogger<PipelineHealthAnalyser> _logger;
    private readonly ConcurrentDictionary<string, PipelineMetrics> _pipelineMetrics = new();

    public PipelineHealthAnalyser(
        IStuckRunDetector stuckRunDetector,
        IRemediationCommandPublisher remediationPublisher,
        IOptions<PipelineHealthAnalyserOptions> options,
        ILogger<PipelineHealthAnalyser> logger)
    {
        _stuckRunDetector = stuckRunDetector;
        _remediationPublisher = remediationPublisher;
        _options = options.Value;
        _logger = logger;
    }

    public async Task ProcessEventAsync(PipelineEvent @event, CancellationToken cancellationToken = default)
    {
        switch (@event)
        {
            case PipelineRunStarted started:
                await HandleRunStartedAsync(started, cancellationToken);
                break;

            case PipelineRunCompleted completed:
                await HandleRunCompletedAsync(completed, cancellationToken);
                break;

            case PipelineRunFailed failed:
                await HandleRunFailedAsync(failed, cancellationToken);
                break;
        }
    }

    public async Task<IEnumerable<RemediationCommand>> CheckForIssuesAsync(CancellationToken cancellationToken = default)
    {
        var commands = new List<RemediationCommand>();

        // Check for stuck runs
        var stuckRuns = _stuckRunDetector.GetStuckRuns();
        foreach (var run in stuckRuns)
        {
            var command = new RemediationCommand
            {
                CommandId = Guid.NewGuid().ToString(),
                RunId = run.RunId,
                PipelineId = run.PipelineId,
                ProjectId = run.ProjectId,
                OrganizationUrl = run.OrganizationUrl,
                Action = RemediationAction.CancelAndRetry,
                Reason = $"Run stuck for {(DateTimeOffset.UtcNow - run.StartTime).TotalMinutes:F0} minutes"
            };

            commands.Add(command);
            await _remediationPublisher.PublishAsync(command, cancellationToken);

            _logger.LogWarning(
                "Published remediation command {CommandId} for stuck run {RunId}",
                command.CommandId, run.RunId);
        }

        return commands;
    }

    public PipelineHealthSummary GetHealthSummary(string pipelineId)
    {
        var metrics = _pipelineMetrics.GetOrAdd(pipelineId, _ => new PipelineMetrics());

        return new PipelineHealthSummary
        {
            PipelineId = pipelineId,
            ActiveRuns = metrics.ActiveRuns,
            StuckRuns = metrics.StuckRuns,
            RecentFailures = metrics.RecentFailures,
            TotalCompletedRuns = metrics.TotalCompletedRuns,
            AverageDuration = metrics.AverageDuration,
            LastRunTime = metrics.LastRunTime
        };
    }

    private Task HandleRunStartedAsync(PipelineRunStarted started, CancellationToken cancellationToken)
    {
        var tracker = new PipelineRunTracker
        {
            RunId = started.RunId,
            PipelineId = started.PipelineId,
            PipelineName = started.PipelineName,
            ProjectId = started.ProjectId,
            OrganizationUrl = started.OrganizationUrl
        };

        _stuckRunDetector.TrackRunStart(tracker);

        var metrics = _pipelineMetrics.GetOrAdd(started.PipelineId, _ => new PipelineMetrics());
        Interlocked.Increment(ref metrics.ActiveRuns);
        metrics.LastRunTime = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "Pipeline run started: {RunId} for {PipelineName}",
            started.RunId, started.PipelineName);

        return Task.CompletedTask;
    }

    private Task HandleRunCompletedAsync(PipelineRunCompleted completed, CancellationToken cancellationToken)
    {
        _stuckRunDetector.TrackRunComplete(completed.RunId);

        var metrics = _pipelineMetrics.GetOrAdd(completed.PipelineId, _ => new PipelineMetrics());
        Interlocked.Decrement(ref metrics.ActiveRuns);
        Interlocked.Increment(ref metrics.TotalCompletedRuns);
        metrics.RecentFailures = 0; // Reset on success
        metrics.UpdateAverageDuration(completed.Duration);

        _logger.LogInformation(
            "Pipeline run completed: {RunId} for {PipelineName}. Duration: {Duration}",
            completed.RunId, completed.PipelineName, completed.Duration);

        return Task.CompletedTask;
    }

    private async Task HandleRunFailedAsync(PipelineRunFailed failed, CancellationToken cancellationToken)
    {
        _stuckRunDetector.TrackRunComplete(failed.RunId);

        var metrics = _pipelineMetrics.GetOrAdd(failed.PipelineId, _ => new PipelineMetrics());
        Interlocked.Decrement(ref metrics.ActiveRuns);
        Interlocked.Increment(ref metrics.RecentFailures);

        _logger.LogWarning(
            "Pipeline run failed: {RunId} for {PipelineName}. Reason: {Reason}",
            failed.RunId, failed.PipelineName, failed.FailureReason);

        // Check if we've hit the failure threshold
        if (metrics.RecentFailures >= _options.FailureThreshold)
        {
            var command = new RemediationCommand
            {
                CommandId = Guid.NewGuid().ToString(),
                RunId = failed.RunId,
                PipelineId = failed.PipelineId,
                ProjectId = failed.ProjectId,
                OrganizationUrl = failed.OrganizationUrl,
                Action = RemediationAction.Retry,
                Reason = $"Pipeline failed {metrics.RecentFailures} times within threshold"
            };

            await _remediationPublisher.PublishAsync(command, cancellationToken);

            _logger.LogWarning(
                "Published remediation command {CommandId} after {FailureCount} consecutive failures",
                command.CommandId, metrics.RecentFailures);
        }
    }

    private sealed class PipelineMetrics
    {
        public int ActiveRuns;
        public int StuckRuns;
        public int RecentFailures;
        public int TotalCompletedRuns;
        public TimeSpan? AverageDuration { get; private set; }
        public DateTimeOffset? LastRunTime { get; set; }

        private readonly object _durationLock = new();
        private TimeSpan _totalDuration = TimeSpan.Zero;
        private int _durationCount;

        public void UpdateAverageDuration(TimeSpan duration)
        {
            lock (_durationLock)
            {
                _totalDuration += duration;
                _durationCount++;
                AverageDuration = TimeSpan.FromTicks(_totalDuration.Ticks / _durationCount);
            }
        }
    }
}
