using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PipelineForge.Core.Analysis;
using PipelineForge.Core.Events;
using PipelineForge.Core.Remediation;
using Xunit;

namespace PipelineForge.Tests.Unit;

public class PipelineHealthAnalyserTests
{
    private readonly Mock<IStuckRunDetector> _stuckRunDetectorMock;
    private readonly Mock<IRemediationCommandPublisher> _remediationPublisherMock;
    private readonly Mock<ILogger<PipelineHealthAnalyser>> _loggerMock;
    private readonly PipelineHealthAnalyser _analyser;

    public PipelineHealthAnalyserTests()
    {
        _stuckRunDetectorMock = new Mock<IStuckRunDetector>();
        _remediationPublisherMock = new Mock<IRemediationCommandPublisher>();
        _loggerMock = new Mock<ILogger<PipelineHealthAnalyser>>();

        var options = Options.Create(new PipelineHealthAnalyserOptions
        {
            FailureThreshold = 3,
            FailureWindowMinutes = 30
        });

        _analyser = new PipelineHealthAnalyser(
            _stuckRunDetectorMock.Object,
            _remediationPublisherMock.Object,
            options,
            _loggerMock.Object);
    }

    [Fact]
    public async Task ProcessEventAsync_WhenRunStarted_TracksRun()
    {
        // Arrange
        var startedEvent = new PipelineRunStarted
        {
            PipelineId = "pipeline-1",
            RunId = "run-1",
            ProjectId = "project-1",
            OrganizationUrl = "https://dev.azure.com/test",
            PipelineName = "Test Pipeline",
            TriggerReason = "manual"
        };

        // Act
        await _analyser.ProcessEventAsync(startedEvent);

        // Assert
        _stuckRunDetectorMock.Verify(x => x.TrackRunStart(
            It.Is<PipelineRunTracker>(t => 
                t.RunId == "run-1" && 
                t.PipelineId == "pipeline-1")),
            Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_WhenRunCompleted_TracksCompletion()
    {
        // Arrange
        var completedEvent = new PipelineRunCompleted
        {
            PipelineId = "pipeline-1",
            RunId = "run-1",
            ProjectId = "project-1",
            OrganizationUrl = "https://dev.azure.com/test",
            PipelineName = "Test Pipeline",
            Result = "succeeded",
            Duration = TimeSpan.FromMinutes(10),
            StartTime = DateTimeOffset.UtcNow.AddMinutes(-10),
            FinishTime = DateTimeOffset.UtcNow
        };

        // Act
        await _analyser.ProcessEventAsync(completedEvent);

        // Assert
        _stuckRunDetectorMock.Verify(x => x.TrackRunComplete("run-1"), Times.Once);
    }

    [Fact]
    public async Task CheckForIssuesAsync_WhenStuckRunsDetected_PublishesRemediationCommands()
    {
        // Arrange
        var stuckRun = new PipelineRunTracker
        {
            RunId = "run-1",
            PipelineId = "pipeline-1",
            PipelineName = "Test Pipeline",
            ProjectId = "project-1",
            OrganizationUrl = "https://dev.azure.com/test",
            State = PipelineRunState.Stuck,
            StartTime = DateTimeOffset.UtcNow.AddHours(-2)
        };

        _stuckRunDetectorMock.Setup(x => x.GetStuckRuns())
            .Returns(new[] { stuckRun });

        // Act
        var commands = await _analyser.CheckForIssuesAsync();

        // Assert
        Assert.Single(commands);
        _remediationPublisherMock.Verify(x => x.PublishAsync(
            It.Is<RemediationCommand>(c => 
                c.RunId == "run-1" && 
                c.Action == RemediationAction.CancelAndRetry),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void GetHealthSummary_ReturnsEmptyForUnknownPipeline()
    {
        // Act
        var summary = _analyser.GetHealthSummary("unknown-pipeline");

        // Assert
        Assert.Equal("unknown-pipeline", summary.PipelineId);
        Assert.Equal(0, summary.ActiveRuns);
        Assert.Equal(0, summary.RecentFailures);
    }
}
