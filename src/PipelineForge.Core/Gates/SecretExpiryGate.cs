using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PipelineForge.Core.Gates;

/// <summary>
/// Configuration options for secret expiry gate.
/// </summary>
public sealed class SecretExpiryGateOptions
{
    public const string SectionName = "SecretExpiryGate";

    /// <summary>
    /// Number of days before expiry to start warning.
    /// </summary>
    public int WarningDays { get; set; } = 7;

    /// <summary>
    /// Number of days before expiry to block deployments.
    /// </summary>
    public int BlockingDays { get; set; } = 3;

    /// <summary>
    /// Mapping of pipeline IDs to the secrets they use.
    /// </summary>
    public Dictionary<string, List<string>> PipelineSecrets { get; set; } = new();
}

/// <summary>
/// Represents a secret's expiry information.
/// </summary>
public sealed class SecretExpiryInfo
{
    public required string SecretName { get; init; }
    public required DateTimeOffset? ExpiresOn { get; init; }
    public bool IsExpired => ExpiresOn.HasValue && ExpiresOn.Value <= DateTimeOffset.UtcNow;
    public bool IsExpiringSoon(int days) => ExpiresOn.HasValue && 
        ExpiresOn.Value <= DateTimeOffset.UtcNow.AddDays(days);
    public int? DaysUntilExpiry => ExpiresOn.HasValue 
        ? (int)(ExpiresOn.Value - DateTimeOffset.UtcNow).TotalDays 
        : null;
}

/// <summary>
/// Result of a secret expiry gate check.
/// </summary>
public sealed record SecretExpiryGateResult
{
    public required string PipelineId { get; init; }
    public bool IsBlocked { get; init; }
    public bool HasWarnings { get; init; }
    public List<SecretExpiryInfo> ExpiringSecrets { get; init; } = new();
    public List<SecretExpiryInfo> ExpiredSecrets { get; init; } = new();
    public string? BlockReason { get; init; }
}

/// <summary>
/// Gate that checks for expiring secrets before allowing deployments.
/// </summary>
public interface ISecretExpiryGate
{
    Task<SecretExpiryGateResult> CheckAsync(
        string pipelineId, 
        CancellationToken cancellationToken = default);

    Task<IEnumerable<SecretExpiryInfo>> GetAllExpiringSecretsAsync(
        int withinDays,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default implementation of <see cref="ISecretExpiryGate"/>.
/// </summary>
public sealed class SecretExpiryGate : ISecretExpiryGate
{
    private readonly ISecretMetadataReader _secretReader;
    private readonly SecretExpiryGateOptions _options;
    private readonly ILogger<SecretExpiryGate> _logger;

    public SecretExpiryGate(
        ISecretMetadataReader secretReader,
        IOptions<SecretExpiryGateOptions> options,
        ILogger<SecretExpiryGate> logger)
    {
        _secretReader = secretReader;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SecretExpiryGateResult> CheckAsync(
        string pipelineId,
        CancellationToken cancellationToken = default)
    {
        var expiringSecrets = new List<SecretExpiryInfo>();
        var expiredSecrets = new List<SecretExpiryInfo>();
        var isBlocked = false;
        var hasWarnings = false;

        // Get secrets used by this pipeline
        if (!_options.PipelineSecrets.TryGetValue(pipelineId, out var secretNames) || 
            secretNames.Count == 0)
        {
            _logger.LogDebug("No secrets configured for pipeline {PipelineId}", pipelineId);
            return new SecretExpiryGateResult
            {
                PipelineId = pipelineId,
                IsBlocked = false,
                HasWarnings = false
            };
        }

        // Check each secret
        foreach (var secretName in secretNames)
        {
            var secretInfo = await _secretReader.GetSecretExpiryAsync(secretName, cancellationToken);
            if (secretInfo == null)
            {
                _logger.LogWarning("Secret {SecretName} not found", secretName);
                continue;
            }

            if (secretInfo.IsExpired)
            {
                expiredSecrets.Add(secretInfo);
                isBlocked = true;
            }
            else if (secretInfo.IsExpiringSoon(_options.BlockingDays))
            {
                expiringSecrets.Add(secretInfo);
                isBlocked = true;
            }
            else if (secretInfo.IsExpiringSoon(_options.WarningDays))
            {
                expiringSecrets.Add(secretInfo);
                hasWarnings = true;
            }
        }

        string? blockReason = null;
        if (isBlocked)
        {
            var secretDescriptions = string.Join(", ", 
                expiredSecrets.Concat(expiringSecrets)
                    .Select(s => $"{s.SecretName} ({s.DaysUntilExpiry} days)"));

            blockReason = $"Deployment blocked due to expiring/expired secrets: {secretDescriptions}";

            _logger.LogWarning(
                "Deployment blocked for pipeline {PipelineId}: {Reason}",
                pipelineId, blockReason);
        }

        return new SecretExpiryGateResult
        {
            PipelineId = pipelineId,
            IsBlocked = isBlocked,
            HasWarnings = hasWarnings,
            ExpiringSecrets = expiringSecrets,
            ExpiredSecrets = expiredSecrets,
            BlockReason = blockReason
        };
    }

    public async Task<IEnumerable<SecretExpiryInfo>> GetAllExpiringSecretsAsync(
        int withinDays,
        CancellationToken cancellationToken = default)
    {
        var allSecrets = await _secretReader.GetAllSecretsAsync(cancellationToken);
        return allSecrets.Where(s => s.IsExpiringSoon(withinDays)).ToList();
    }
}

/// <summary>
/// Interface for reading secret metadata from Key Vault.
/// </summary>
public interface ISecretMetadataReader
{
    Task<SecretExpiryInfo?> GetSecretExpiryAsync(
        string secretName,
        CancellationToken cancellationToken = default);

    Task<IEnumerable<SecretExpiryInfo>> GetAllSecretsAsync(
        CancellationToken cancellationToken = default);
}
