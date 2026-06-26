using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PipelineForge.Core.Analysis;
using PipelineForge.Core.Events;
using PipelineForge.Core.Gates;
using PipelineForge.Core.Remediation;
using PipelineForge.Infrastructure.AzureDevOps;
using PipelineForge.Infrastructure.KeyVault;
using PipelineForge.Infrastructure.ServiceBus;
using PipelineForge.Infrastructure.Telemetry;
using PipelineForge.Workers;

var builder = Host.CreateApplicationBuilder(args);

// Configuration
builder.Services.Configure<ServiceBusOptions>(
    builder.Configuration.GetSection(ServiceBusOptions.SectionName));
builder.Services.Configure<AdoRestClientOptions>(
    builder.Configuration.GetSection(AdoRestClientOptions.SectionName));
builder.Services.Configure<KeyVaultOptions>(
    builder.Configuration.GetSection(KeyVaultOptions.SectionName));
builder.Services.Configure<StuckRunDetectorOptions>(
    builder.Configuration.GetSection(StuckRunDetectorOptions.SectionName));
builder.Services.Configure<PipelineHealthAnalyserOptions>(
    builder.Configuration.GetSection(PipelineHealthAnalyserOptions.SectionName));
builder.Services.Configure<SecretExpiryGateOptions>(
    builder.Configuration.GetSection(SecretExpiryGateOptions.SectionName));
builder.Services.Configure<SecretExpiryGateWorkerOptions>(
    builder.Configuration.GetSection(SecretExpiryGateWorkerOptions.SectionName));

// Core services
builder.Services.AddSingleton<IPipelineEventChannel, PipelineEventChannel>();
builder.Services.AddSingleton<IStuckRunDetector, StuckRunDetector>();
builder.Services.AddSingleton<IPipelineHealthAnalyser, PipelineHealthAnalyser>();
builder.Services.AddSingleton<ISecretExpiryGate, SecretExpiryGate>();
builder.Services.AddSingleton<IRemediationCommandHandler, RemediationCommandHandler>();

// Infrastructure services
builder.Services.AddSingleton<IAdoWebhookReceiver, AdoWebhookReceiver>();
builder.Services.AddSingleton<ISecretMetadataReader, SecretMetadataReader>();
builder.Services.AddSingleton<IRemediationCommandPublisher, ServiceBusPublisher>();

// HTTP client for Azure DevOps REST API
builder.Services.AddHttpClient<IAzureDevOpsClient, AdoRestClient>();

// Worker services
builder.Services.AddHostedService<EventIngestionWorker>();
builder.Services.AddHostedService<PipelineHealthWorker>();
builder.Services.AddHostedService<RemediationWorker>();
builder.Services.AddHostedService<SecretExpiryGateWorker>();

// OpenTelemetry configuration
var resourceBuilder = ResourceBuilder.CreateDefault()
    .AddService(
        serviceName: OtelInstrumentation.ServiceName,
        serviceVersion: OtelInstrumentation.ServiceVersion);

var azureMonitorConnectionString = builder.Configuration["OpenTelemetry:ConnectionString"];

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .SetResourceBuilder(resourceBuilder)
            .AddSource(OtelInstrumentation.ServiceName)
            .AddHttpClientInstrumentation();

        if (!string.IsNullOrEmpty(azureMonitorConnectionString))
        {
            tracing.AddAzureMonitorTraceExporter(options =>
            {
                options.ConnectionString = azureMonitorConnectionString;
            });
        }
        else
        {
            tracing.AddConsoleExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        metrics
            .SetResourceBuilder(resourceBuilder)
            .AddMeter(OtelInstrumentation.ServiceName)
            .AddRuntimeInstrumentation()
            .AddHttpClientInstrumentation();

        if (!string.IsNullOrEmpty(azureMonitorConnectionString))
        {
            metrics.AddAzureMonitorMetricExporter(options =>
            {
                options.ConnectionString = azureMonitorConnectionString;
            });
        }
        else
        {
            metrics.AddConsoleExporter();
        }
    });

var host = builder.Build();

host.Run();
