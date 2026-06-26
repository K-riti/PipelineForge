using PipelineForge.Core.Events;
using Xunit;

namespace PipelineForge.Tests.Integration;

public class PipelineEventChannelTests
{
    [Fact]
    public async Task Channel_PublishAndConsume_EventsFlowCorrectly()
    {
        // Arrange
        var channel = new PipelineEventChannel();
        var events = new List<PipelineEvent>();

        var testEvent = new PipelineRunStarted
        {
            PipelineId = "pipeline-1",
            RunId = "run-1",
            ProjectId = "project-1",
            OrganizationUrl = "https://dev.azure.com/test",
            PipelineName = "Test Pipeline",
            TriggerReason = "manual"
        };

        // Act - Write to channel
        await channel.Writer.WriteAsync(testEvent);
        channel.Writer.Complete();

        // Consume from channel
        await foreach (var evt in channel.Reader.ReadAllAsync())
        {
            events.Add(evt);
        }

        // Assert
        Assert.Single(events);
        Assert.IsType<PipelineRunStarted>(events[0]);
        Assert.Equal("run-1", events[0].RunId);
    }

    [Fact]
    public async Task Channel_MultipleEvents_AllDelivered()
    {
        // Arrange
        var channel = new PipelineEventChannel();
        var events = new List<PipelineEvent>();

        var event1 = new PipelineRunStarted
        {
            PipelineId = "p1", RunId = "r1", ProjectId = "proj",
            OrganizationUrl = "https://dev.azure.com/test",
            PipelineName = "Pipeline 1", TriggerReason = "ci"
        };

        var event2 = new PipelineRunCompleted
        {
            PipelineId = "p1", RunId = "r1", ProjectId = "proj",
            OrganizationUrl = "https://dev.azure.com/test",
            PipelineName = "Pipeline 1", Result = "succeeded",
            Duration = TimeSpan.FromMinutes(5),
            StartTime = DateTimeOffset.UtcNow.AddMinutes(-5),
            FinishTime = DateTimeOffset.UtcNow
        };

        var event3 = new PipelineRunFailed
        {
            PipelineId = "p2", RunId = "r2", ProjectId = "proj",
            OrganizationUrl = "https://dev.azure.com/test",
            PipelineName = "Pipeline 2", FailureReason = "test failure",
            StartTime = DateTimeOffset.UtcNow.AddMinutes(-10),
            FailureTime = DateTimeOffset.UtcNow
        };

        // Act
        await channel.Writer.WriteAsync(event1);
        await channel.Writer.WriteAsync(event2);
        await channel.Writer.WriteAsync(event3);
        channel.Writer.Complete();

        await foreach (var evt in channel.Reader.ReadAllAsync())
        {
            events.Add(evt);
        }

        // Assert
        Assert.Equal(3, events.Count);
        Assert.IsType<PipelineRunStarted>(events[0]);
        Assert.IsType<PipelineRunCompleted>(events[1]);
        Assert.IsType<PipelineRunFailed>(events[2]);
    }
}
