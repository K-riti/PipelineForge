using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PipelineForge.Core.Remediation;

namespace PipelineForge.Infrastructure.AzureDevOps;

/// <summary>
/// Configuration options for Azure DevOps REST API client.
/// </summary>
public sealed class AdoRestClientOptions
{
    public const string SectionName = "AzureDevOps";

    /// <summary>
    /// Azure DevOps organization URL (e.g., https://dev.azure.com/myorg).
    /// </summary>
    public string OrganisationUrl { get; set; } = string.Empty;

    /// <summary>
    /// Personal Access Token for authentication.
    /// </summary>
    public string PersonalAccessToken { get; set; } = string.Empty;

    /// <summary>
    /// API version to use.
    /// </summary>
    public string ApiVersion { get; set; } = "7.1";
}

/// <summary>
/// REST API client for Azure DevOps operations.
/// </summary>
public sealed class AdoRestClient : IAzureDevOpsClient
{
    private readonly HttpClient _httpClient;
    private readonly AdoRestClientOptions _options;
    private readonly ILogger<AdoRestClient> _logger;

    public AdoRestClient(
        HttpClient httpClient,
        IOptions<AdoRestClientOptions> options,
        ILogger<AdoRestClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        ConfigureHttpClient();
    }

    private void ConfigureHttpClient()
    {
        if (!string.IsNullOrEmpty(_options.PersonalAccessToken))
        {
            var authValue = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($":{_options.PersonalAccessToken}"));
            _httpClient.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Basic", authValue);
        }

        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> TriggerPipelineRunAsync(
        string organizationUrl,
        string projectId,
        string pipelineId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{organizationUrl}/{projectId}/_apis/pipelines/{pipelineId}/runs?api-version={_options.ApiVersion}";

        var requestBody = new
        {
            resources = new
            {
                repositories = new
                {
                    self = new { refName = "refs/heads/main" }
                }
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        _logger.LogInformation(
            "Triggering pipeline run: {PipelineId} in project {ProjectId}",
            pipelineId, projectId);

        var response = await _httpClient.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(responseBody);

        var newRunId = doc.RootElement.TryGetProperty("id", out var idProp)
            ? idProp.ToString()
            : Guid.NewGuid().ToString();

        _logger.LogInformation("Pipeline run triggered successfully: {RunId}", newRunId);

        return newRunId;
    }

    public async Task CancelPipelineRunAsync(
        string organizationUrl,
        string projectId,
        string runId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{organizationUrl}/{projectId}/_apis/build/builds/{runId}?api-version={_options.ApiVersion}";

        var requestBody = new { status = "cancelling" };

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        _logger.LogInformation(
            "Cancelling pipeline run: {RunId} in project {ProjectId}",
            runId, projectId);

        var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = content };
        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        _logger.LogInformation("Pipeline run cancelled successfully: {RunId}", runId);
    }
}
