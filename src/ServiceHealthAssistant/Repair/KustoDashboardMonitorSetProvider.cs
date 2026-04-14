using Azure.Identity;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using Microsoft.Extensions.Logging;
using ServiceHealthAssistant.Models;

namespace ServiceHealthAssistant.Repair;

/// <summary>
/// <see cref="IDashboardMonitorSetProvider"/> implementation backed by the
/// <c>MCP_BrainIntentEvaluation</c> ADX table on the SHM Dev cluster.
///
/// Source:
///   cluster('shm-dev-uksouth-kusto.uksouth.kusto.windows.net')
///     .database('SHMDatabase')
///     .MCP_BrainIntentEvaluation
///
/// Returns the latest evaluation row per monitor for the given service.
/// When <c>actionRequiredFilter == "Yes"</c> only monitors with at least one
/// capability in <see cref="BrainIntentStatus.ShouldBeEnabled"/> are returned.
///
/// Auth: DefaultAzureCredential (Managed Identity → developer credential chain).
/// </summary>
public sealed class KustoDashboardMonitorSetProvider : IDashboardMonitorSetProvider, IDisposable
{
    private const string ClusterUri   = "https://shm-dev-uksouth-kusto.uksouth.kusto.windows.net";
    private const string DatabaseName = "SHMDatabase";

    private readonly ICslQueryProvider _queryProvider;
    private readonly ILogger<KustoDashboardMonitorSetProvider> _logger;
    private bool _disposed;

    public KustoDashboardMonitorSetProvider(ILogger<KustoDashboardMonitorSetProvider> logger)
    {
        _logger = logger;
        _queryProvider = CreateQueryProvider();
    }

    // Internal constructor for testing.
    internal KustoDashboardMonitorSetProvider(
        ICslQueryProvider queryProvider,
        ILogger<KustoDashboardMonitorSetProvider> logger)
    {
        _queryProvider = queryProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DashboardMonitorDescriptor>> GetMonitorsForServiceAsync(
        string serviceId,
        string? actionRequiredFilter,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
            return [];

        bool actionRequiredOnly = string.Equals(
            actionRequiredFilter, "Yes", StringComparison.OrdinalIgnoreCase);

        var props = new ClientRequestProperties
        {
            ClientRequestId = $"ServiceHealthAssistant;GetDashboardMonitors;{Guid.NewGuid()}"
        };
        props.SetParameter("_serviceId", serviceId);

        // The ActionRequired flag is derived by the query itself; the parameter-bound
        // filter avoids per-row client-side filtering.
        var kql = $"""
            MCP_BrainIntentEvaluation
            | where ServiceId == _serviceId
            | summarize arg_max(EvaluationTimestamp, *) by ServiceId, MonitorId
            | extend ActionRequired = (
                  BrainAwareness    == "ShouldBeEnabled"
               or OutageDeclaration == "ShouldBeEnabled"
               or DeploymentStops   == "ShouldBeEnabled"
               or AutoComms         == "ShouldBeEnabled")
            {(actionRequiredOnly ? "| where ActionRequired == true" : string.Empty)}
            | project MonitorId, MonitorName, ServiceId,
                      BrainAwareness, OutageDeclaration, DeploymentStops, AutoComms
            """;

        _logger.LogInformation(
            "Fetching dashboard monitor set for service '{ServiceId}' (actionRequired={ActionRequired}).",
            serviceId, actionRequiredOnly);

        var results = new List<DashboardMonitorDescriptor>();

        await Task.Run(() =>
        {
            using var reader = _queryProvider.ExecuteQuery(DatabaseName, kql, props);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var monitorId   = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                var monitorName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var svcId       = reader.IsDBNull(2) ? serviceId    : reader.GetString(2);

                if (string.IsNullOrWhiteSpace(monitorId)) continue;

                results.Add(new DashboardMonitorDescriptor(
                    MonitorId:         monitorId,
                    MonitorName:       monitorName,
                    AccountId:         string.Empty, // populated by caller from GenevaAccountId
                    ServiceId:         svcId,
                    BrainAwareness:    ParseStatus(reader, 3),
                    OutageDeclaration: ParseStatus(reader, 4),
                    DeploymentStops:   ParseStatus(reader, 5),
                    AutoComms:         ParseStatus(reader, 6),
                    NextAction:        null)); // TODO: repair tagging contract missing
            }
        }, cancellationToken);

        _logger.LogInformation(
            "Retrieved {Count} dashboard monitor(s) for service '{ServiceId}'.",
            results.Count, serviceId);

        return results.AsReadOnly();
    }

    private static BrainIntentStatus ParseStatus(System.Data.IDataReader reader, int col)
    {
        if (reader.IsDBNull(col)) return BrainIntentStatus.NotClassified;
        var raw = reader.GetString(col);
        return Enum.TryParse<BrainIntentStatus>(raw, ignoreCase: true, out var val)
            ? val
            : BrainIntentStatus.NotClassified;
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
