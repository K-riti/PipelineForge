using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PipelineForge.Core.Analysis;
using PipelineForge.Core.Events;

namespace PipelineForge.Workers;

/// <summary>
/// Worker service that processes pipeline events and analyzes health.
/// </summary>
public sealed class PipelineHealthWorker : BackgroundService
{
    private readonly IPipelineEventChannel _eventChannel;
    private readonly IPipelineHealthAnalyser _healthAnalyser;
    private readonly PipelineHealthAnalyserOptions _options;
    private readonly ILogger<PipelineHealthWorker> _logger;
    private readonly PeriodicTimer _stuckRunTimer;

    public PipelineHealthWorker(
        IPipelineEventChannel eventChannel,
        IPipelineHealthAnalyser healthAnalyser,
        IOptions<PipelineHealthAnalyserOptions> options,
        ILogger<PipelineHealthWorker> logger)
    {
        _eventChannel = eventChannel;
        _healthAnalyser = healthAnalyser;
        _options = options.Value;
        _logger = logger;
        _stuckRunTimer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.StuckRunCheckIntervalSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Pipeline Health Worker...");

        // Start both tasks
        var eventProcessingTask = ProcessEventsAsync(stoppingToken);
        var stuckRunCheckTask = CheckForStuckRunsAsync(stoppingToken);

        await Task.WhenAll(eventProcessingTask, stuckRunCheckTask);
    }

    private async Task ProcessEventsAsync(CancellationToken stoppingToken)
    {
        await foreach (var pipelineEvent in _eventChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await _healthAnalyser.ProcessEventAsync(pipelineEvent, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error processing pipeline event {EventType} for run {RunId}",
                    pipelineEvent.GetType().Name,
                    pipelineEvent.RunId);
            }
        }
    }

    private async Task CheckForStuckRunsAsync(CancellationToken stoppingToken)
    {
        while (await _stuckRunTimer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var commands = await _healthAnalyser.CheckForIssuesAsync(stoppingToken);
                var commandList = commands.ToList();

                if (commandList.Count > 0)
                {
                    _logger.LogInformation(
                        "Detected {Count} issues, published remediation commands",
                        commandList.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for stuck runs");
            }
        }
    }

    public override void Dispose()
    {
        _stuckRunTimer.Dispose();
        base.Dispose();
    }
}
