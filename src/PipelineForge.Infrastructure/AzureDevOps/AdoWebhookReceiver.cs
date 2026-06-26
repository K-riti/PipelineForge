using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PipelineForge.Core.Events;

namespace PipelineForge.Infrastructure.AzureDevOps;

/// <summary>
/// Azure DevOps webhook event types.
/// </summary>
public static class AdoEventTypes
{
    public const string BuildComplete = "build.complete";
    public const string RunStageStateChanged = "ms.vss-pipelines.run-stage-state-changed";
    public const string RunStateChanged = "ms.vss-pipelines.run-state-changed";
    public const string ReleaseDeploymentCompleted = "ms.vss-release.deployment-completed";
}

/// <summary>
/// Base Azure DevOps webhook payload.
/// </summary>
public sealed class AdoWebhookPayload
{
    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }

    [JsonPropertyName("notificationId")]
    public int NotificationId { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("publisherId")]
    public string? PublisherId { get; set; }

    [JsonPropertyName("resource")]
    public JsonElement Resource { get; set; }

    [JsonPropertyName("resourceContainers")]
    public ResourceContainers? ResourceContainers { get; set; }

    [JsonPropertyName("createdDate")]
    public DateTimeOffset CreatedDate { get; set; }
}

public sealed class ResourceContainers
{
    [JsonPropertyName("collection")]
    public ResourceContainer? Collection { get; set; }

    [JsonPropertyName("account")]
    public ResourceContainer? Account { get; set; }

    [JsonPropertyName("project")]
    public ResourceContainer? Project { get; set; }
}

public sealed class ResourceContainer
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; set; }
}

/// <summary>
/// Receives and parses Azure DevOps webhook payloads into domain events.
/// </summary>
public interface IAdoWebhookReceiver
{
    Task<PipelineEvent?> ParseWebhookAsync(string payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="IAdoWebhookReceiver"/>.
/// </summary>
public sealed class AdoWebhookReceiver : IAdoWebhookReceiver
{
    private readonly ILogger<AdoWebhookReceiver> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AdoWebhookReceiver(ILogger<AdoWebhookReceiver> logger)
    {
        _logger = logger;
    }

    public Task<PipelineEvent?> ParseWebhookAsync(string payload, CancellationToken cancellationToken = default)
    {
        try
        {
            var webhookPayload = JsonSerializer.Deserialize<AdoWebhookPayload>(payload, JsonOptions);
            if (webhookPayload == null)
            {
                _logger.LogWarning("Failed to deserialize webhook payload");
                return Task.FromResult<PipelineEvent?>(null);
            }

            _logger.LogDebug(
                "Received webhook event: {EventType}, NotificationId: {NotificationId}",
                webhookPayload.EventType, webhookPayload.NotificationId);

            var pipelineEvent = webhookPayload.EventType switch
            {
                AdoEventTypes.BuildComplete => ParseBuildComplete(webhookPayload),
                AdoEventTypes.RunStageStateChanged => ParseRunStageStateChanged(webhookPayload),
                AdoEventTypes.RunStateChanged => ParseRunStateChanged(webhookPayload),
                AdoEventTypes.ReleaseDeploymentCompleted => ParseReleaseDeploymentCompleted(webhookPayload),
                _ => null
            };

            if (pipelineEvent == null)
            {
                _logger.LogDebug("Unhandled event type: {EventType}", webhookPayload.EventType);
            }

            return Task.FromResult(pipelineEvent);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse webhook payload JSON");
            return Task.FromResult<PipelineEvent?>(null);
        }
    }

    private PipelineEvent? ParseBuildComplete(AdoWebhookPayload payload)
    {
        var resource = payload.Resource;

        var result = resource.TryGetProperty("result", out var resultProp) 
            ? resultProp.GetString() ?? "unknown" 
            : "unknown";

        var buildId = resource.TryGetProperty("id", out var idProp) 
            ? idProp.ToString() 
            : Guid.NewGuid().ToString();

        var definitionId = resource.TryGetProperty("definition", out var defProp) &&
                          defProp.TryGetProperty("id", out var defIdProp)
            ? defIdProp.ToString()
            : "unknown";

        var definitionName = resource.TryGetProperty("definition", out var defNameProp) &&
                            defNameProp.TryGetProperty("name", out var nameProp)
            ? nameProp.GetString() ?? "unknown"
            : "unknown";

        var projectId = payload.ResourceContainers?.Project?.Id ?? "unknown";
        var orgUrl = payload.ResourceContainers?.Account?.BaseUrl ?? "unknown";

        if (result == "succeeded" || result == "partiallySucceeded")
        {
            var startTime = resource.TryGetProperty("startTime", out var startProp)
                ? startProp.GetDateTimeOffset()
                : DateTimeOffset.UtcNow;

            var finishTime = resource.TryGetProperty("finishTime", out var finishProp)
                ? finishProp.GetDateTimeOffset()
                : DateTimeOffset.UtcNow;

            return new PipelineRunCompleted
            {
                PipelineId = definitionId,
                RunId = buildId,
                ProjectId = projectId,
                OrganizationUrl = orgUrl,
                PipelineName = definitionName,
                Result = result,
                Duration = finishTime - startTime,
                StartTime = startTime,
                FinishTime = finishTime,
                CorrelationId = payload.Id
            };
        }
        else
        {
            var startTime = resource.TryGetProperty("startTime", out var startProp)
                ? startProp.GetDateTimeOffset()
                : DateTimeOffset.UtcNow;

            return new PipelineRunFailed
            {
                PipelineId = definitionId,
                RunId = buildId,
                ProjectId = projectId,
                OrganizationUrl = orgUrl,
                PipelineName = definitionName,
                FailureReason = result,
                StartTime = startTime,
                FailureTime = DateTimeOffset.UtcNow,
                CorrelationId = payload.Id
            };
        }
    }

    private PipelineEvent? ParseRunStageStateChanged(AdoWebhookPayload payload)
    {
        var resource = payload.Resource;

        var state = resource.TryGetProperty("state", out var stateProp)
            ? stateProp.GetString()
            : null;

        if (state != "inProgress")
        {
            return null;
        }

        var runId = resource.TryGetProperty("run", out var runProp) &&
                   runProp.TryGetProperty("id", out var runIdProp)
            ? runIdProp.ToString()
            : Guid.NewGuid().ToString();

        var pipelineId = resource.TryGetProperty("run", out var runProp2) &&
                        runProp2.TryGetProperty("pipeline", out var pipeProp) &&
                        pipeProp.TryGetProperty("id", out var pipeIdProp)
            ? pipeIdProp.ToString()
            : "unknown";

        var pipelineName = resource.TryGetProperty("run", out var runProp3) &&
                          runProp3.TryGetProperty("pipeline", out var pipeProp2) &&
                          pipeProp2.TryGetProperty("name", out var pipeNameProp)
            ? pipeNameProp.GetString() ?? "unknown"
            : "unknown";

        var projectId = payload.ResourceContainers?.Project?.Id ?? "unknown";
        var orgUrl = payload.ResourceContainers?.Account?.BaseUrl ?? "unknown";

        return new PipelineRunStarted
        {
            PipelineId = pipelineId,
            RunId = runId,
            ProjectId = projectId,
            OrganizationUrl = orgUrl,
            PipelineName = pipelineName,
            TriggerReason = "stageStateChanged",
            CorrelationId = payload.Id
        };
    }

    private PipelineEvent? ParseRunStateChanged(AdoWebhookPayload payload)
    {
        var resource = payload.Resource;

        var state = resource.TryGetProperty("state", out var stateProp)
            ? stateProp.GetString()
            : null;

        var runId = resource.TryGetProperty("id", out var runIdProp)
            ? runIdProp.ToString()
            : Guid.NewGuid().ToString();

        var pipelineId = resource.TryGetProperty("pipeline", out var pipeProp) &&
                        pipeProp.TryGetProperty("id", out var pipeIdProp)
            ? pipeIdProp.ToString()
            : "unknown";

        var pipelineName = resource.TryGetProperty("pipeline", out var pipeProp2) &&
                          pipeProp2.TryGetProperty("name", out var pipeNameProp)
            ? pipeNameProp.GetString() ?? "unknown"
            : "unknown";

        var projectId = payload.ResourceContainers?.Project?.Id ?? "unknown";
        var orgUrl = payload.ResourceContainers?.Account?.BaseUrl ?? "unknown";

        return state switch
        {
            "inProgress" => new PipelineRunStarted
            {
                PipelineId = pipelineId,
                RunId = runId,
                ProjectId = projectId,
                OrganizationUrl = orgUrl,
                PipelineName = pipelineName,
                TriggerReason = "runStateChanged",
                CorrelationId = payload.Id
            },
            "completed" => ParseCompletedRun(resource, pipelineId, runId, pipelineName, projectId, orgUrl, payload.Id),
            _ => null
        };
    }

    private static PipelineEvent ParseCompletedRun(
        JsonElement resource,
        string pipelineId,
        string runId,
        string pipelineName,
        string projectId,
        string orgUrl,
        string? correlationId)
    {
        var result = resource.TryGetProperty("result", out var resultProp)
            ? resultProp.GetString() ?? "unknown"
            : "unknown";

        var startTime = resource.TryGetProperty("createdDate", out var startProp)
            ? startProp.GetDateTimeOffset()
            : DateTimeOffset.UtcNow;

        var finishTime = resource.TryGetProperty("finishedDate", out var finishProp)
            ? finishProp.GetDateTimeOffset()
            : DateTimeOffset.UtcNow;

        if (result == "succeeded")
        {
            return new PipelineRunCompleted
            {
                PipelineId = pipelineId,
                RunId = runId,
                ProjectId = projectId,
                OrganizationUrl = orgUrl,
                PipelineName = pipelineName,
                Result = result,
                Duration = finishTime - startTime,
                StartTime = startTime,
                FinishTime = finishTime,
                CorrelationId = correlationId
            };
        }
        else
        {
            return new PipelineRunFailed
            {
                PipelineId = pipelineId,
                RunId = runId,
                ProjectId = projectId,
                OrganizationUrl = orgUrl,
                PipelineName = pipelineName,
                FailureReason = result,
                StartTime = startTime,
                FailureTime = finishTime,
                CorrelationId = correlationId
            };
        }
    }

    private PipelineEvent? ParseReleaseDeploymentCompleted(AdoWebhookPayload payload)
    {
        var resource = payload.Resource;

        var deploymentStatus = resource.TryGetProperty("deployment", out var depProp) &&
                              depProp.TryGetProperty("deploymentStatus", out var statusProp)
            ? statusProp.GetString()
            : null;

        var releaseId = resource.TryGetProperty("deployment", out var depProp2) &&
                       depProp2.TryGetProperty("release", out var relProp) &&
                       relProp.TryGetProperty("id", out var relIdProp)
            ? relIdProp.ToString()
            : Guid.NewGuid().ToString();

        var definitionId = resource.TryGetProperty("deployment", out var depProp3) &&
                          depProp3.TryGetProperty("releaseDefinition", out var defProp) &&
                          defProp.TryGetProperty("id", out var defIdProp)
            ? defIdProp.ToString()
            : "unknown";

        var definitionName = resource.TryGetProperty("deployment", out var depProp4) &&
                            depProp4.TryGetProperty("releaseDefinition", out var defProp2) &&
                            defProp2.TryGetProperty("name", out var defNameProp)
            ? defNameProp.GetString() ?? "unknown"
            : "unknown";

        var projectId = payload.ResourceContainers?.Project?.Id ?? "unknown";
        var orgUrl = payload.ResourceContainers?.Account?.BaseUrl ?? "unknown";

        if (deploymentStatus == "succeeded")
        {
            return new PipelineRunCompleted
            {
                PipelineId = definitionId,
                RunId = releaseId,
                ProjectId = projectId,
                OrganizationUrl = orgUrl,
                PipelineName = definitionName,
                Result = deploymentStatus,
                Duration = TimeSpan.Zero,
                StartTime = payload.CreatedDate,
                FinishTime = payload.CreatedDate,
                CorrelationId = payload.Id
            };
        }
        else
        {
            return new PipelineRunFailed
            {
                PipelineId = definitionId,
                RunId = releaseId,
                ProjectId = projectId,
                OrganizationUrl = orgUrl,
                PipelineName = definitionName,
                FailureReason = deploymentStatus ?? "unknown",
                StartTime = payload.CreatedDate,
                FailureTime = payload.CreatedDate,
                CorrelationId = payload.Id
            };
        }
    }
}
