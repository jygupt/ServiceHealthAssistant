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
            | summarize arg_max(Timestamp, *) by tostring(MonitorId), tostring(MonitorName)
            | project
                MonitorId                    = tostring(MonitorId),
                MonitorName                  = tostring(MonitorName),
                MonitorType                  = tostring(MonitorType),
                IsOutageDriving              = tobool(column_ifexists('IsOutageDriving', false)),
                DetectedImpactType           = tostring(column_ifexists('DetectedImpactType', '')),
                LocationIdPresent            = tobool(column_ifexists('IsLIDCompliant', false)),
                RegionalScopeDetectable      = tobool(column_ifexists('RegionalScopeDetectable', false)),
                SubscriptionScopeDetectable  = tobool(column_ifexists('SubscriptionScopeDetectable', false)),
                HistoricalPrecision          = tostring(column_ifexists('HistoricalPrecision', '')),
                SignalStability              = tostring(column_ifexists('SignalStability', '')),
                LinkedCujoJourney            = tostring(column_ifexists('LinkedCujoJourney', '')),
                LinkedICMIncidentId          = tostring(column_ifexists('LinkedICMIncidentId', ''))
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
                var monitorName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var monitorType = reader.IsDBNull(2) ? null         : (string?)reader.GetString(2);

                // Skip rows with no usable identifier.
                var id = !string.IsNullOrWhiteSpace(monitorId)   ? monitorId
                       : !string.IsNullOrWhiteSpace(monitorName) ? monitorName
                       : null;

                if (id is null) continue;

                var isOutageDriving = !reader.IsDBNull(3) && reader.GetBoolean(3);

                var detectedImpactTypeStr = reader.IsDBNull(4) ? null : reader.GetString(4);
                var detectedImpactType = ParseImpactType(detectedImpactTypeStr);

                var locationIdPresent       = !reader.IsDBNull(5) && reader.GetBoolean(5);
                var regionalScope           = !reader.IsDBNull(6) && reader.GetBoolean(6);
                var subscriptionScope       = !reader.IsDBNull(7) && reader.GetBoolean(7);

                var historicalPrecisionStr  = reader.IsDBNull(8) ? null : reader.GetString(8);
                var historicalPrecision     = ParseHistoricalPrecision(historicalPrecisionStr);

                var signalStabilityStr      = reader.IsDBNull(9) ? null : reader.GetString(9);
                var signalStability         = ParseSignalStability(signalStabilityStr);

                var linkedCujoJourney       = reader.IsDBNull(10) ? null : NullIfEmpty(reader.GetString(10));
                var linkedIcmIncidentId     = reader.IsDBNull(11) ? null : NullIfEmpty(reader.GetString(11));

                results.Add(new MonitorEvaluationInput(
                    MonitorId:                       id,
                    MonitorName:                     !string.IsNullOrWhiteSpace(monitorName) ? monitorName : monitorId,
                    MonitorType:                     string.IsNullOrWhiteSpace(monitorType) ? null : monitorType,
                    LinkedCujoJourney:               linkedCujoJourney,
                    OutageDrivingIcmMapping:         isOutageDriving,
                    DetectedImpactType:              detectedImpactType,
                    LocationIdPresent:               locationIdPresent,
                    RegionalScopeDetectable:         regionalScope,
                    SubscriptionScopeDetectable:     subscriptionScope,
                    HistoricalPrecision:             historicalPrecision,
                    SignalStability:                 signalStability,
                    LinkedICMIncidentId:             linkedIcmIncidentId));
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
