using Azure.Identity;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using Microsoft.Extensions.Logging;

namespace ServiceHealthAssistant.Repair;

/// <summary>
/// <see cref="IGenevaMonitorMetadataClient"/> implementation that:
/// <list type="bullet">
///   <item>Reads current Brain capability metadata via the Geneva health-configs Kusto cluster.</item>
///   <item>Stubs the write path pending the real Geneva REST/ARM endpoint contract.</item>
/// </list>
///
/// Read cluster:  https://geneva.kusto.windows.net
/// Read database: genevahealthconfigs
/// Read table:    MonitorConfigMetadata (Metadata dynamic column)
///
/// Auth: DefaultAzureCredential (Managed Identity → developer credential chain).
/// </summary>
public sealed class GenevaMonitorMetadataClient : IGenevaMonitorMetadataClient, IDisposable
{
    private const string ClusterUri   = "https://geneva.kusto.windows.net";
    private const string DatabaseName = "genevahealthconfigs";

    private readonly ICslQueryProvider _queryProvider;
    private readonly ILogger<GenevaMonitorMetadataClient> _logger;
    private bool _disposed;

    public GenevaMonitorMetadataClient(ILogger<GenevaMonitorMetadataClient> logger)
    {
        _logger = logger;
        _queryProvider = CreateQueryProvider();
    }

    // Internal constructor for testing – allows injecting a pre-built query provider.
    internal GenevaMonitorMetadataClient(
        ICslQueryProvider queryProvider,
        ILogger<GenevaMonitorMetadataClient> logger)
    {
        _queryProvider = queryProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string>> GetCapabilityMetadataAsync(
        string accountId,
        string monitorId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(monitorId))
            return new Dictionary<string, string>();

        // Allowlist validation for accountId (used inline in KQL literals in GenevaMonitorFetcher
        // as well; kept consistent here).
        if (!System.Text.RegularExpressions.Regex.IsMatch(accountId, @"^[a-zA-Z0-9\-_.]+$"))
            throw new ArgumentException($"accountId '{accountId}' contains invalid characters.");

        var props = new ClientRequestProperties
        {
            ClientRequestId = $"ServiceHealthAssistant;GetCapabilityMetadata;{Guid.NewGuid()}"
        };
        // Both accountId and monitorId are bound as query parameters to prevent KQL injection.
        // accountId additionally passes the allowlist check above for defence in depth.
        props.SetParameter("_accountId", accountId);
        props.SetParameter("_monitorId", monitorId);

        // Use parameter binding for the monitorId to prevent KQL injection.
        const string kql = """
            MonitorConfigMetadata
            | where Time_Fetched >= now() - 2h
            | where account_id == _accountId
            | where tostring(monitor_name) == _monitorId or tostring(MonitorGuid) == _monitorId
            | summarize arg_max(Time_Fetched, *) by tostring(monitor_name), tostring(MonitorGuid)
            | extend BrainAwareness    = tostring(Metadata["BrainIntent.BrainAwareness"])
            | extend OutageDeclaration = tostring(Metadata["BrainIntent.OutageDeclaration"])
            | extend DeploymentStops   = tostring(Metadata["BrainIntent.DeploymentStops"])
            | extend AutoComms         = tostring(Metadata["BrainIntent.AutoComms"])
            | project BrainAwareness, OutageDeclaration, DeploymentStops, AutoComms
            | take 1
            """;

        _logger.LogInformation(
            "Reading capability metadata for monitor '{MonitorId}'.", monitorId);

        var result = new Dictionary<string, string>(4);

        await Task.Run(() =>
        {
            using var reader = _queryProvider.ExecuteQuery(DatabaseName, kql, props);
            if (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                void TryAdd(int col, string key)
                {
                    if (!reader.IsDBNull(col))
                    {
                        var val = reader.GetString(col);
                        if (!string.IsNullOrWhiteSpace(val))
                            result[key] = val;
                    }
                }

                TryAdd(0, BrainCapabilityMetadataKeys.BrainAwareness);
                TryAdd(1, BrainCapabilityMetadataKeys.OutageDeclaration);
                TryAdd(2, BrainCapabilityMetadataKeys.DeploymentStops);
                TryAdd(3, BrainCapabilityMetadataKeys.AutoComms);
            }
        }, cancellationToken);

        return result;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// TODO: Geneva monitor metadata write contract is not present in the repository.
    /// This stub always succeeds in a DryRun context.  Replace with the real Geneva
    /// REST/ARM PUT endpoint once the contract is confirmed.
    /// See docs/repair-agent-bulk-metadata.md – "Open Questions / Assumptions".
    /// </remarks>
    public Task<MonitorMetadataUpdateResult> UpdateCapabilityMetadataAsync(
        string accountId,
        string monitorId,
        IReadOnlyDictionary<string, string> metadataUpdates,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning(
            "UpdateCapabilityMetadataAsync is a stub – Geneva write endpoint not yet wired. " +
            "MonitorId={MonitorId} Keys={Keys}",
            monitorId, string.Join(", ", metadataUpdates.Keys));

        // TODO: Replace with real Geneva metadata write call.
        // Expected contract (pending confirmation):
        //   PUT https://<geneva-api>/accounts/{accountId}/monitors/{monitorId}/metadata
        //   Body: { "BrainIntent.BrainAwareness": "Enabled", ... }
        return Task.FromResult(new MonitorMetadataUpdateResult(
            Succeeded: false,
            ErrorMessage: "Geneva metadata write endpoint not yet implemented. " +
                          "See docs/repair-agent-bulk-metadata.md for the missing contract."));
    }

    private static ICslQueryProvider CreateQueryProvider()
    {
        var credential = new DefaultAzureCredential();
        var kcsb = new KustoConnectionStringBuilder(ClusterUri)
            .WithAadAzureTokenCredentialsAuthentication(credential);
        return KustoClientFactory.CreateCslQueryProvider(kcsb);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _queryProvider.Dispose();
            _disposed = true;
        }
    }
}
