using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Kusto.Data;
using Kusto.Ingest;
using Microsoft.Extensions.Logging;
using ServiceHealthAssistant.Models;

namespace ServiceHealthAssistant.Adx;

/// <summary>
/// Writes Brain Intent evaluation rows to ADX via queued ingestion.
///
/// Target:
///   Cluster  : https://shm-dev-uksouth-kusto.uksouth.kusto.windows.net
///   Database : SHMDatabase
///   Table    : MCP_BrainIntentEvaluation
///
/// Auth     : DefaultAzureCredential (Managed Identity preferred, falls back to
///            developer credential when running locally).
///
/// Helper KQL – latest evaluation per monitor:
///   MCP_BrainIntentEvaluation
///   | summarize arg_max(EvaluationTimestamp, *) by ServiceId, MonitorId
/// </summary>
public sealed class KustoBrainIntentWriter : IKustoBrainIntentWriter, IDisposable
{
    private const string ClusterUri = "https://shm-dev-uksouth-kusto.uksouth.kusto.windows.net";
    private const string DatabaseName = "SHMDatabase";
    private const string TableName = "MCP_BrainIntentEvaluation";
    private const string EvaluationSource = "MCP:evaluate_monitor_brain_integration";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IKustoIngestClient _ingestClient;
    private readonly ILogger<KustoBrainIntentWriter> _logger;
    private bool _disposed;

    public KustoBrainIntentWriter(ILogger<KustoBrainIntentWriter> logger)
    {
        _logger = logger;
        _ingestClient = CreateIngestClient();
    }

    // Internal constructor for testing – allows injecting a pre-built client.
    internal KustoBrainIntentWriter(IKustoIngestClient ingestClient, ILogger<KustoBrainIntentWriter> logger)
    {
        _ingestClient = ingestClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task IngestBatchAsync(
        IReadOnlyList<BrainIntentEvaluationRow> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
            return;

        // Serialize each row as a JSON line (newline-delimited JSON).
        var sb = new StringBuilder(rows.Count * 256);
        foreach (var row in rows)
            sb.AppendLine(JsonSerializer.Serialize(row, JsonOptions));

        var json = sb.ToString();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var ingestionProperties = new KustoIngestionProperties(DatabaseName, TableName)
        {
            Format = Kusto.Data.Common.DataSourceFormat.multijson,
            IngestionMapping = new IngestionMapping
            {
                IngestionMappingKind = Kusto.Data.Ingestion.IngestionMappingKind.Json
            }
        };

        _logger.LogInformation(
            "Ingesting {Count} Brain Intent evaluation row(s) into {Table}.", rows.Count, TableName);

        await _ingestClient.IngestFromStreamAsync(stream, ingestionProperties)
            .WaitAsync(cancellationToken);

        _logger.LogInformation("Ingestion submitted for {Count} row(s).", rows.Count);
    }

    private static IKustoIngestClient CreateIngestClient()
    {
        // Build a KustoConnectionStringBuilder that uses DefaultAzureCredential
        // (Managed Identity → Workload Identity → Developer credential chain).
        var tokenCredential = new DefaultAzureCredential();

        var kcsb = new KustoConnectionStringBuilder(ClusterUri)
            .WithAadAzureTokenCredentialsAuthentication(tokenCredential);

        return KustoIngestFactory.CreateQueuedIngestClient(kcsb);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _ingestClient.Dispose();
            _disposed = true;
        }
    }
}
