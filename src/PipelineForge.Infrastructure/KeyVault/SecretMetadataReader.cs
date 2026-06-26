using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PipelineForge.Core.Gates;

namespace PipelineForge.Infrastructure.KeyVault;

/// <summary>
/// Configuration options for Azure Key Vault.
/// </summary>
public sealed class KeyVaultOptions
{
    public const string SectionName = "KeyVault";

    /// <summary>
    /// Key Vault URI (e.g., https://myvault.vault.azure.net/).
    /// </summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// Days before expiry to trigger warnings.
    /// </summary>
    public int SecretExpiryWarningDays { get; set; } = 7;
}

/// <summary>
/// Reads secret metadata from Azure Key Vault.
/// </summary>
public sealed class SecretMetadataReader : ISecretMetadataReader
{
    private readonly SecretClient _secretClient;
    private readonly ILogger<SecretMetadataReader> _logger;

    public SecretMetadataReader(
        IOptions<KeyVaultOptions> options,
        ILogger<SecretMetadataReader> logger)
    {
        var config = options.Value;
        _logger = logger;

        var vaultUri = new Uri(config.Uri);
        _secretClient = new SecretClient(vaultUri, new DefaultAzureCredential());
    }

    public async Task<SecretExpiryInfo?> GetSecretExpiryAsync(
        string secretName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var secret = await _secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken);

            return new SecretExpiryInfo
            {
                SecretName = secretName,
                ExpiresOn = secret.Value.Properties.ExpiresOn
            };
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("Secret {SecretName} not found in Key Vault", secretName);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving secret {SecretName} from Key Vault", secretName);
            throw;
        }
    }

    public async Task<IEnumerable<SecretExpiryInfo>> GetAllSecretsAsync(
        CancellationToken cancellationToken = default)
    {
        var secrets = new List<SecretExpiryInfo>();

        await foreach (var secretProperties in _secretClient.GetPropertiesOfSecretsAsync(cancellationToken))
        {
            secrets.Add(new SecretExpiryInfo
            {
                SecretName = secretProperties.Name,
                ExpiresOn = secretProperties.ExpiresOn
            });
        }

        _logger.LogDebug("Retrieved {Count} secrets from Key Vault", secrets.Count);

        return secrets;
    }
}
