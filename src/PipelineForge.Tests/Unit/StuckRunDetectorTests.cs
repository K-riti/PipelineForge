using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PipelineForge.Core.Analysis;
using Xunit;

namespace PipelineForge.Tests.Unit;

public class StuckRunDetectorTests
{
    private readonly Mock<ILogger<StuckRunDetector>> _loggerMock;
    private readonly StuckRunDetector _detector;

    public StuckRunDetectorTests()
    {
        _loggerMock = new Mock<ILogger<StuckRunDetector>>();
        var options = Options.Create(new StuckRunDetectorOptions
        {
            DefaultTimeoutMinutes = 60,
            PipelineTimeouts = new Dictionary<string, int>
            {
                { "long-pipeline", 120 }
            }
        });

        _detector = new StuckRunDetector(options, _loggerMock.Object);
    }

    [Fact]
    public void TrackRunStart_AddsRunToActiveList()
    {
        // Arrange
        var tracker = new PipelineRunTracker
        {
            RunId = "run-1",
            PipelineId = "pipeline-1",
            PipelineName = "Test Pipeline",
            ProjectId = "project-1",
            OrganizationUrl = "https://dev.azure.com/test"
        };

        // Act
        _detector.TrackRunStart(tracker);
        var retrieved = _detector.GetTracker("run-1");

        // Assert
        Assert.NotNull(retrieved);
        Assert.Equal("run-1", retrieved.RunId);
        Assert.Equal(PipelineRunState.Running, retrieved.State);
    }

    [Fact]
    public void TrackRunComplete_RemovesRunFromActiveList()
    {
        // Arrange
        var tracker = new PipelineRunTracker
        {
            RunId = "run-1",
            PipelineId = "pipeline-1",
            PipelineName = "Test Pipeline",
            ProjectId = "project-1",
            OrganizationUrl = "https://dev.azure.com/test"
        };

        _detector.TrackRunStart(tracker);

        // Act
        _detector.TrackRunComplete("run-1");
        var retrieved = _detector.GetTracker("run-1");

        // Assert
        Assert.Null(retrieved);
    }

    [Fact]
    public void GetStuckRuns_ReturnsEmptyWhenNoRunsAreStuck()
    {
        // Arrange
        var tracker = new PipelineRunTracker
        {
            RunId = "run-1",
            PipelineId = "pipeline-1",
            PipelineName = "Test Pipeline",
            ProjectId = "project-1",
            OrganizationUrl = "https://dev.azure.com/test"
        };

        _detector.TrackRunStart(tracker);

        // Act
        var stuckRuns = _detector.GetStuckRuns();

        // Assert
        Assert.Empty(stuckRuns);
    }

    [Fact]
    public void GetTracker_ReturnsNullForUnknownRun()
    {
        // Act
        var tracker = _detector.GetTracker("unknown-run");

        // Assert
        Assert.Null(tracker);
    }
}
