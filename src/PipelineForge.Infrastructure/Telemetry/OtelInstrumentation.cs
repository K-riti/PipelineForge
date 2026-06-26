using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PipelineForge.Infrastructure.Telemetry;

/// <summary>
/// OpenTelemetry instrumentation for PipelineForge.
/// </summary>
public static class OtelInstrumentation
{
    public const string ServiceName = "PipelineForge";
    public const string ServiceVersion = "1.0.0";

    // Activity source for distributed tracing
    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

    // Meter for metrics
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    // Counters
    public static readonly Counter<long> EventsIngested = Meter.CreateCounter<long>(
        "pipelineforge.events.ingested",
        description: "Number of pipeline events ingested");

    public static readonly Counter<long> PipelineFailureTotal = Meter.CreateCounter<long>(
        "pipelineforge.pipeline_failure_total",
        description: "Total number of pipeline failures");

    public static readonly Counter<long> PipelineStuckTotal = Meter.CreateCounter<long>(
        "pipelineforge.pipeline_stuck_total",
        description: "Total number of stuck pipeline runs detected");

    public static readonly Counter<long> RemediationActionsTotal = Meter.CreateCounter<long>(
        "pipelineforge.remediation_actions_total",
        description: "Total number of remediation actions executed");

    public static readonly Counter<long> DeploymentBlockedTotal = Meter.CreateCounter<long>(
        "pipelineforge.deployment_blocked_total",
        description: "Total number of deployments blocked due to secret expiry");

    // Histograms
    public static readonly Histogram<double> BuildDurationMs = Meter.CreateHistogram<double>(
        "pipelineforge.build_duration_ms",
        unit: "ms",
        description: "Build duration in milliseconds");

    public static readonly Histogram<double> RemediationLatencyMs = Meter.CreateHistogram<double>(
        "pipelineforge.remediation_latency_ms",
        unit: "ms",
        description: "Time from issue detection to remediation execution");

    // Gauges (using ObservableGauge for DORA metrics)
    private static int _activeRuns;
    private static double _mttrHours;
    private static double _deploymentFrequencyDaily;
    private static double _changeFailureRatePercent;

    static OtelInstrumentation()
    {
        Meter.CreateObservableGauge(
            "pipelineforge.active_runs",
            () => _activeRuns,
            description: "Number of currently active pipeline runs");

        Meter.CreateObservableGauge(
            "pipelineforge.mttr_hours",
            () => _mttrHours,
            unit: "h",
            description: "Mean Time to Restore in hours (DORA metric)");

        Meter.CreateObservableGauge(
            "pipelineforge.deployment_frequency_daily",
            () => _deploymentFrequencyDaily,
            description: "Deployment frequency per day (DORA metric)");

        Meter.CreateObservableGauge(
            "pipelineforge.change_failure_rate_percent",
            () => _changeFailureRatePercent,
            unit: "%",
            description: "Change failure rate percentage (DORA metric)");
    }

    public static void SetActiveRuns(int count) => _activeRuns = count;
    public static void SetMttrHours(double hours) => _mttrHours = hours;
    public static void SetDeploymentFrequencyDaily(double frequency) => _deploymentFrequencyDaily = frequency;
    public static void SetChangeFailureRatePercent(double rate) => _changeFailureRatePercent = rate;

    /// <summary>
    /// Creates a new activity for event ingestion.
    /// </summary>
    public static Activity? StartEventIngestionActivity(string eventType, string pipelineId, string runId)
    {
        var activity = ActivitySource.StartActivity("event.ingested", ActivityKind.Consumer);
        activity?.SetTag("event.type", eventType);
        activity?.SetTag("pipeline.id", pipelineId);
        activity?.SetTag("run.id", runId);
        return activity;
    }

    /// <summary>
    /// Creates a new activity for remediation execution.
    /// </summary>
    public static Activity? StartRemediationActivity(string action, string pipelineId, string runId)
    {
        var activity = ActivitySource.StartActivity("remediation.executed", ActivityKind.Internal);
        activity?.SetTag("remediation.action", action);
        activity?.SetTag("pipeline.id", pipelineId);
        activity?.SetTag("run.id", runId);
        return activity;
    }

    /// <summary>
    /// Creates a new activity for secret expiry gate check.
    /// </summary>
    public static Activity? StartSecretGateActivity(string pipelineId)
    {
        var activity = ActivitySource.StartActivity("secret_gate.check", ActivityKind.Internal);
        activity?.SetTag("pipeline.id", pipelineId);
        return activity;
    }

    /// <summary>
    /// Records a build completion with duration.
    /// </summary>
    public static void RecordBuildCompletion(string pipelineId, string pipelineName, TimeSpan duration, bool success)
    {
        BuildDurationMs.Record(
            duration.TotalMilliseconds,
            new KeyValuePair<string, object?>("pipeline.id", pipelineId),
            new KeyValuePair<string, object?>("pipeline.name", pipelineName),
            new KeyValuePair<string, object?>("result", success ? "succeeded" : "failed"));

        if (!success)
        {
            PipelineFailureTotal.Add(1,
                new KeyValuePair<string, object?>("pipeline.id", pipelineId),
                new KeyValuePair<string, object?>("pipeline.name", pipelineName));
        }
    }
}
