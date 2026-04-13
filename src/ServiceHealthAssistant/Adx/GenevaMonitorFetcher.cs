using Azure.Identity;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using Microsoft.Extensions.Logging;
using ServiceHealthAssistant.Evaluators;

namespace ServiceHealthAssistant.Adx;

/// <summary>
/// Fetches Service Monitor metadata from the Geneva health-configs Kusto cluster.
///
/// Source:
///   cluster('geneva.kusto.windows.net')
///     .database('genevahealthconfigs')
///     .MonitorConfigMetadata
///   | where Time_Fetched &gt; ago(1h)
///   | where isnotempty(monitor_name) or isnotempty(MonitorGuid)
///
/// Auth: DefaultAzureCredential (Managed Identity → developer credential chain).
/// </summary>
public sealed class GenevaMonitorFetcher : IGenevaMonitorFetcher, IDisposable
{
    private const string ClusterUri   = "https://geneva.kusto.windows.net";
    private const string DatabaseName = "genevahealthconfigs";

    private readonly ICslQueryProvider _queryProvider;
    private readonly ILogger<GenevaMonitorFetcher> _logger;
    private bool _disposed;

    public GenevaMonitorFetcher(ILogger<GenevaMonitorFetcher> logger)
    {
        _logger = logger;
        _queryProvider = CreateQueryProvider();
    }

    // Internal constructor for testing – allows injecting a pre-built query provider.
    internal GenevaMonitorFetcher(ICslQueryProvider queryProvider, ILogger<GenevaMonitorFetcher> logger)
    {
        _queryProvider = queryProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MonitorEvaluationInput>> FetchMonitorsForAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return [];

        // Validate accountId to prevent KQL injection: allow only alphanumeric, dash, underscore, dot.
        if (!System.Text.RegularExpressions.Regex.IsMatch(accountId, @"^[a-zA-Z0-9\-_.]+$"))
        {
            throw new ArgumentException(
                $"accountId '{accountId}' contains invalid characters. " +
                "Only alphanumeric characters, hyphens, underscores, and dots are allowed.");
        }

        var kql = $"""
            let MonitorConfigMetadata_1h_raw =
                MonitorConfigMetadata
                | where Time_Fetched > ago(1h)
                | where isnotempty(monitor_name) or isnotempty(MonitorGuid);
            MonitorConfigMetadata_1h_raw
            | where account_id == '{accountId}'
            | summarize arg_max(Time_Fetched, *) by tostring(monitor_name), tostring(MonitorGuid)
            | project
                monitor_name   = tostring(monitor_name),
                monitor_guid   = tostring(MonitorGuid),
                account_id     = tostring(account_id),
                monitor_type   = tostring(MonitorType)
            """;

        _logger.LogInformation(
            "Fetching monitors for account '{AccountId}' from {Cluster}/{Database}.",
            accountId, ClusterUri, DatabaseName);

        var props = new ClientRequestProperties
        {
            ClientRequestId = $"ServiceHealthAssistant;FetchMonitors;{Guid.NewGuid()}"
        };

        var results = new List<MonitorEvaluationInput>();

        await Task.Run(() =>
        {
            using var reader = _queryProvider.ExecuteQuery(DatabaseName, kql, props);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var monitorName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var monitorGuid = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var monType     = reader.IsDBNull(3) ? null         : (string?)reader.GetString(3);

                // Use GUID as ID when name is absent; skip rows with neither.
                var id = !string.IsNullOrWhiteSpace(monitorName) ? monitorName
                       : !string.IsNullOrWhiteSpace(monitorGuid) ? monitorGuid
                       : null;

                if (id is null) continue;

                results.Add(new MonitorEvaluationInput(
                    MonitorId:   id,
                    MonitorName: !string.IsNullOrWhiteSpace(monitorName) ? monitorName : monitorGuid,
                    MonitorType: string.IsNullOrWhiteSpace(monType) ? null : monType));
            }
        }, cancellationToken);

        _logger.LogInformation(
            "Retrieved {Count} monitor(s) for account '{AccountId}'.", results.Count, accountId);

        return results.AsReadOnly();
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
