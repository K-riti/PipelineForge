using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PipelineForge.Core.Gates;
using PipelineForge.Infrastructure.Telemetry;

namespace PipelineForge.Workers;

/// <summary>
/// Configuration options for Secret Expiry Gate Worker.
/// </summary>
public sealed class SecretExpiryGateWorkerOptions
{
    public const string SectionName = "SecretExpiryGateWorker";

    /// <summary>
    /// Interval in minutes between secret expiry checks.
    /// </summary>
    public int CheckIntervalMinutes { get; set; } = 60;
}

/// <summary>
/// Worker service that monitors secret expiry and blocks deployments when needed.
/// </summary>
public sealed class SecretExpiryGateWorker : BackgroundService
{
    private readonly ISecretExpiryGate _secretExpiryGate;
    private readonly SecretExpiryGateWorkerOptions _options;
    private readonly SecretExpiryGateOptions _gateOptions;
    private readonly ILogger<SecretExpiryGateWorker> _logger;

    public SecretExpiryGateWorker(
        ISecretExpiryGate secretExpiryGate,
        IOptions<SecretExpiryGateWorkerOptions> options,
        IOptions<SecretExpiryGateOptions> gateOptions,
        ILogger<SecretExpiryGateWorker> logger)
    {
        _secretExpiryGate = secretExpiryGate;
        _options = options.Value;
        _gateOptions = gateOptions.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Starting Secret Expiry Gate Worker. Check interval: {Interval} minutes",
            _options.CheckIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckExpiringSecretsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during secret expiry check");
            }

            await Task.Delay(
                TimeSpan.FromMinutes(_options.CheckIntervalMinutes),
                stoppingToken);
        }
    }

    private async Task CheckExpiringSecretsAsync(CancellationToken cancellationToken)
    {
        using var activity = OtelInstrumentation.ActivitySource.StartActivity("secret_expiry.scan");

        _logger.LogDebug("Running secret expiry scan...");

        // Check all secrets expiring within the warning window
        var expiringSecrets = await _secretExpiryGate.GetAllExpiringSecretsAsync(
            _gateOptions.WarningDays,
            cancellationToken);

        var secretsList = expiringSecrets.ToList();

        if (secretsList.Count == 0)
        {
            _logger.LogDebug("No expiring secrets found");
            return;
        }

        _logger.LogWarning(
            "Found {Count} secrets expiring within {Days} days",
            secretsList.Count,
            _gateOptions.WarningDays);

        foreach (var secret in secretsList)
        {
            var daysUntilExpiry = secret.DaysUntilExpiry ?? 0;
            var logLevel = daysUntilExpiry <= _gateOptions.BlockingDays
                ? LogLevel.Error
                : LogLevel.Warning;

            _logger.Log(logLevel,
                "Secret {SecretName} expires in {Days} days (on {ExpiryDate})",
                secret.SecretName,
                daysUntilExpiry,
                secret.ExpiresOn);

            if (secret.IsExpired || daysUntilExpiry <= _gateOptions.BlockingDays)
            {
                OtelInstrumentation.DeploymentBlockedTotal.Add(1,
                    new KeyValuePair<string, object?>("secret.name", secret.SecretName),
                    new KeyValuePair<string, object?>("reason", secret.IsExpired ? "expired" : "expiring_soon"));
            }
        }

        activity?.SetTag("secrets.expiring_count", secretsList.Count);
    }
}
