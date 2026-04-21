using Azure.Identity;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using Microsoft.Extensions.Logging;
using ServiceHealthAssistant.Evaluators;
using ServiceHealthAssistant.Models;

namespace ServiceHealthAssistant.Adx;

/// <summary>
/// Fetches Service Monitor metadata for a given service from the Analytics cluster using
/// the <c>GetIntegratedMonitorOutageCoverageDrillThrough</c> Kusto function.
///
/// Source:
///   cluster('sherica-prod.uksouth.kusto.windows.net').database('Analytics')
///   GetIntegratedMonitorOutageCoverageDrillThrough(
///       _StartTime = now(-365d),
///       _EndTime   = now()
///   )
///
/// Auth: DefaultAzureCredential (Managed Identity → developer credential chain).
/// </summary>
public sealed class ShericaMonitorFetcher : IShericaMonitorFetcher, IDisposable
{
    private const string ClusterUri   = "https://sherica-prod.uksouth.kusto.windows.net";
    private const string DatabaseName = "Analytics";

    private readonly ICslQueryProvider _queryProvider;
    private readonly ILogger<ShericaMonitorFetcher> _logger;
    private bool _disposed;

    public ShericaMonitorFetcher(ILogger<ShericaMonitorFetcher> logger)
    {
        _logger = logger;
        _queryProvider = CreateQueryProvider();
    }

    // Internal constructor for testing – allows injecting a pre-built query provider.
    internal ShericaMonitorFetcher(ICslQueryProvider queryProvider, ILogger<ShericaMonitorFetcher> logger)
    {
        _queryProvider = queryProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MonitorEvaluationInput>> FetchMonitorsForServiceAsync(
        string serviceOid,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceOid))
            return [];

        // Validate serviceOid to prevent KQL injection: allow only alphanumeric, dash, underscore, dot.
        if (!System.Text.RegularExpressions.Regex.IsMatch(serviceOid, @"^[a-zA-Z0-9\-_.]+$"))
        {
            throw new ArgumentException(
                $"serviceOid '{serviceOid}' contains invalid characters. " +
                "Only alphanumeric characters, hyphens, underscores, and dots are allowed.");
        }

        const string kql = """
            GetIntegratedMonitorOutageCoverageDrillThrough(
                _StartTime = now(-365d),
                _EndTime   = now()
            )
            | where ServiceOid == _serviceOid
            | summarize arg_max(Timestamp, *) by tostring(MonitorId)
            | project
                MonitorId                    = tostring(MonitorId),
                ServiceOid                    = tostring(ServiceOid),
                ServiceName                   = tostring(ServiceName),
                AllIncidents                 = tostring(AllIncidents),
                BrainMonitorisAOD             = tobool(column_ifexists('BrainMonitorisAOD', false)),
                AODETA = tostring(column_ifexists('AODETA', '')),
                LIDComplianceReason           = tostring(column_ifexists('LIDComplianceReason', '')),
                AllIncidentsCount              = toint(column_ifexists('AllIncidentsCount', 0)),
                DetectedImpactType           = tostring(column_ifexists('DetectedImpactType', '')),
                IsLIDCompliant               = tobool(column_ifexists('IsLIDCompliant', false)),
                RegionalScopeDetectable      = tobool(column_ifexists('RegionalScopeDetectable', false)),
                SubscriptionScopeDetectable  = tobool(column_ifexists('SubscriptionScopeDetectable', false)),
                HistoricalPrecision          = tostring(column_ifexists('HistoricalPrecision', '')),
                SignalStability              = tostring(column_ifexists('SignalStability', '')),
                LinkedCujoJourney            = tostring(column_ifexists('LinkedCujoJourney', ''))
            """;

        _logger.LogInformation(
            "Fetching monitors for service OID '{ServiceOid}' from {Cluster}/{Database} via GetIntegratedMonitorOutageCoverageDrillThrough.",
            serviceOid, ClusterUri, DatabaseName);

        var props = new ClientRequestProperties
        {
            ClientRequestId = $"ServiceHealthAssistant;FetchShericaMonitors;{Guid.NewGuid()}"
        };
        props.SetParameter("_serviceOid", serviceOid);

        var results = new List<MonitorEvaluationInput>();

        await Task.Run(() =>
        {
            using var reader = _queryProvider.ExecuteQuery(DatabaseName, kql, props);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var monitorId   = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);

                // Skip rows with no usable identifier.
                var id = !string.IsNullOrWhiteSpace(monitorId)   ? monitorId
                       : null;

                if (id is null) continue;

                var isBrainAOD = !reader.IsDBNull(3) && reader.GetBoolean(4);

                var detectedImpactTypeStr = reader.IsDBNull(4) ? null : reader.GetString(8);
                var detectedImpactType = ParseImpactType(detectedImpactTypeStr);
                var allIncidents = reader.IsDBNull(2) ? null : reader.GetString(3);
                var allIncidentsCount = !reader.IsDBNull(5) ? reader.GetInt32(7) : 0;
                var isLidCompliant       = !reader.IsDBNull(5) && reader.GetBoolean(9);
                var regionalScope           = !reader.IsDBNull(6) && reader.GetBoolean(10);
                var subscriptionScope       = !reader.IsDBNull(7) && reader.GetBoolean(11);

                var historicalPrecisionStr  = reader.IsDBNull(8) ? null : reader.GetString(12);
                var historicalPrecision     = ParseHistoricalPrecision(historicalPrecisionStr);

                var signalStabilityStr      = reader.IsDBNull(9) ? null : reader.GetString(13);
                var signalStability         = ParseSignalStability(signalStabilityStr);

                var linkedCujoJourney       = reader.IsDBNull(10) ? null : NullIfEmpty(reader.GetString(14));
                var UsedInOutageDeclarationPreviously = allIncidentsCount > 0 && isBrainAOD ? true : false;
                results.Add(new MonitorEvaluationInput(
                    MonitorId:                       id,
                    LinkedCujoJourney:               linkedCujoJourney,
                    isBrainAOD:                      isBrainAOD,
                    DetectedImpactType:              detectedImpactType,
                    isLIDCompliant:                  isLidCompliant,
                    RegionalScopeDetectable:         regionalScope,
                    SubscriptionScopeDetectable:     subscriptionScope,
                    HistoricalPrecision:             historicalPrecision,
                    SignalStability:                 signalStability,
                    UsedInOutageDeclarationPreviously: UsedInOutageDeclarationPreviously,
                    AllIncidents:                    allIncidents));
            }
        }, cancellationToken);

        _logger.LogInformation(
            "Retrieved {Count} monitor(s) for service OID '{ServiceOid}' from GetIntegratedMonitorOutageCoverageDrillThrough.",
            results.Count, serviceOid);

        return results.AsReadOnly();
    }

    private static DetectedImpactType ParseImpactType(string? value) =>
        Enum.TryParse<DetectedImpactType>(value, ignoreCase: true, out var parsed)
            ? parsed
            : DetectedImpactType.Operational;

    private static HistoricalPrecision ParseHistoricalPrecision(string? value) =>
        Enum.TryParse<HistoricalPrecision>(value, ignoreCase: true, out var parsed)
            ? parsed
            : HistoricalPrecision.Low;

    private static SignalStability ParseSignalStability(string? value) =>
        Enum.TryParse<SignalStability>(value, ignoreCase: true, out var parsed)
            ? parsed
            : SignalStability.Unknown;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

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
