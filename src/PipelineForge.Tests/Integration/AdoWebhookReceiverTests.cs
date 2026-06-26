using Microsoft.Extensions.Logging;
using Moq;
using PipelineForge.Core.Events;
using PipelineForge.Infrastructure.AzureDevOps;
using Xunit;

namespace PipelineForge.Tests.Integration;

public class AdoWebhookReceiverTests
{
    private readonly AdoWebhookReceiver _receiver;

    public AdoWebhookReceiverTests()
    {
        var loggerMock = new Mock<ILogger<AdoWebhookReceiver>>();
        _receiver = new AdoWebhookReceiver(loggerMock.Object);
    }

    [Fact]
    public async Task ParseWebhookAsync_BuildComplete_Succeeded_ReturnsPipelineRunCompleted()
    {
        // Arrange
        var payload = """
        {
            "subscriptionId": "sub-123",
            "notificationId": 1,
            "id": "event-123",
            "eventType": "build.complete",
            "publisherId": "tfs",
            "resource": {
                "id": 42,
                "result": "succeeded",
                "definition": {
                    "id": 10,
                    "name": "CI-Pipeline"
                },
                "startTime": "2024-01-15T10:00:00Z",
                "finishTime": "2024-01-15T10:15:00Z"
            },
            "resourceContainers": {
                "project": {
                    "id": "project-1",
                    "baseUrl": "https://dev.azure.com/test"
                },
                "account": {
                    "id": "account-1",
                    "baseUrl": "https://dev.azure.com/test"
                }
            },
            "createdDate": "2024-01-15T10:15:01Z"
        }
        """;

        // Act
        var result = await _receiver.ParseWebhookAsync(payload);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<PipelineRunCompleted>(result);

        var completed = (PipelineRunCompleted)result;
        Assert.Equal("10", completed.PipelineId);
        Assert.Equal("42", completed.RunId);
        Assert.Equal("CI-Pipeline", completed.PipelineName);
        Assert.Equal("succeeded", completed.Result);
        Assert.Equal("project-1", completed.ProjectId);
        Assert.Equal(TimeSpan.FromMinutes(15), completed.Duration);
    }

    [Fact]
    public async Task ParseWebhookAsync_BuildComplete_Failed_ReturnsPipelineRunFailed()
    {
        // Arrange
        var payload = """
        {
            "subscriptionId": "sub-123",
            "notificationId": 2,
            "id": "event-456",
            "eventType": "build.complete",
            "publisherId": "tfs",
            "resource": {
                "id": 43,
                "result": "failed",
                "definition": {
                    "id": 10,
                    "name": "CI-Pipeline"
                },
                "startTime": "2024-01-15T11:00:00Z"
            },
            "resourceContainers": {
                "project": {
                    "id": "project-1",
                    "baseUrl": "https://dev.azure.com/test"
                },
                "account": {
                    "id": "account-1",
                    "baseUrl": "https://dev.azure.com/test"
                }
            },
            "createdDate": "2024-01-15T11:10:00Z"
        }
        """;

        // Act
        var result = await _receiver.ParseWebhookAsync(payload);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<PipelineRunFailed>(result);

        var failed = (PipelineRunFailed)result;
        Assert.Equal("10", failed.PipelineId);
        Assert.Equal("43", failed.RunId);
        Assert.Equal("CI-Pipeline", failed.PipelineName);
        Assert.Equal("failed", failed.FailureReason);
    }

    [Fact]
    public async Task ParseWebhookAsync_RunStateChanged_InProgress_ReturnsPipelineRunStarted()
    {
        // Arrange
        var payload = """
        {
            "subscriptionId": "sub-123",
            "notificationId": 3,
            "id": "event-789",
            "eventType": "ms.vss-pipelines.run-state-changed",
            "publisherId": "pipelines",
            "resource": {
                "id": 100,
                "state": "inProgress",
                "pipeline": {
                    "id": 20,
                    "name": "Deploy-Pipeline"
                }
            },
            "resourceContainers": {
                "project": {
                    "id": "project-2",
                    "baseUrl": "https://dev.azure.com/test"
                },
                "account": {
                    "id": "account-1",
                    "baseUrl": "https://dev.azure.com/test"
                }
            },
            "createdDate": "2024-01-15T12:00:00Z"
        }
        """;

        // Act
        var result = await _receiver.ParseWebhookAsync(payload);

        // Assert
        Assert.NotNull(result);
        Assert.IsType<PipelineRunStarted>(result);

        var started = (PipelineRunStarted)result;
        Assert.Equal("20", started.PipelineId);
        Assert.Equal("100", started.RunId);
        Assert.Equal("Deploy-Pipeline", started.PipelineName);
        Assert.Equal("project-2", started.ProjectId);
    }

    [Fact]
    public async Task ParseWebhookAsync_InvalidJson_ReturnsNull()
    {
        // Arrange
        var invalidPayload = "{ invalid json }}}";

        // Act
        var result = await _receiver.ParseWebhookAsync(invalidPayload);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ParseWebhookAsync_UnknownEventType_ReturnsNull()
    {
        // Arrange
        var payload = """
        {
            "subscriptionId": "sub-123",
            "notificationId": 4,
            "id": "event-000",
            "eventType": "unknown.event.type",
            "publisherId": "tfs",
            "resource": {},
            "resourceContainers": {},
            "createdDate": "2024-01-15T12:00:00Z"
        }
        """;

        // Act
        var result = await _receiver.ParseWebhookAsync(payload);

        // Assert
        Assert.Null(result);
    }
}
