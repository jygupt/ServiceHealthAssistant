using Azure.Identity;
using Kusto.Data;
using Kusto.Data.Common;
using Kusto.Data.Net.Client;
using Microsoft.Extensions.Logging;

namespace ServiceHealthAssistant.Adx;

/// <summary>
/// Fetches CUJO metadata for a given service from the Analytics cluster.
///
/// Source:
///   cluster('sherica-prod.uksouth.kusto.windows.net').database('Analytics')
///   Tables: CUJOMetadata, CujoToSloRelationship, CujoToMonitorRelationship
///
/// Auth: DefaultAzureCredential (Managed Identity → developer credential chain).
/// </summary>
public sealed class ShericaCujoFetcher : IShericaCujoFetcher, IDisposable
{
    private const string ClusterUri   = "https://sherica-prod.uksouth.kusto.windows.net";
    private const string DatabaseName = "sherica-prod";

    private readonly ICslQueryProvider _queryProvider;
    private readonly ILogger<ShericaCujoFetcher> _logger;
    private bool _disposed;

    public ShericaCujoFetcher(ILogger<ShericaCujoFetcher> logger)
    {
        _logger = logger;
        _queryProvider = CreateQueryProvider();
    }

    // Internal constructor for testing — allows injecting a pre-built query provider.
    internal ShericaCujoFetcher(ICslQueryProvider queryProvider, ILogger<ShericaCujoFetcher> logger)
    {
        _queryProvider = queryProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CujoMappingRow>> FetchCujosForServiceAsync(
        string serviceTreeId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceTreeId))
            return [];

        // Validate serviceTreeId to prevent KQL injection: allow only alphanumeric, dash, underscore, dot.
        if (!System.Text.RegularExpressions.Regex.IsMatch(serviceTreeId, @"^[a-zA-Z0-9\-_.]+$"))
        {
            throw new ArgumentException(
                $"serviceTreeId '{serviceTreeId}' contains invalid characters. " +
                "Only alphanumeric characters, hyphens, underscores, and dots are allowed.");
        }

        const string kql = """
            declare query_parameters(_serviceTreeId:string);
            let LatestCujos = CUJOMetadata
            | summarize arg_max(IngestionTimestamp, *) by InternalCujoId
            | where IsActive == true and ServiceTreeId == _serviceTreeId
            | project InternalCujoId, CujoName, CujoDescription, ServiceTreeId, ServiceName,
                      CujoCreationDate, CujoLastModificationDate, OwningContactId,
                      ImplementationETA, HasExceptionForSLOCreation, IsImplementationBlocked,
                      CujoComments, IngestionTimestamp;
            let LatestCujoToSlo = CujoToSloRelationship
            | summarize arg_max(IngestionTimestamp, *) by InternalCujoId, InternalSloId
            | where IsActive == true;
            let SloAgg = LatestCujoToSlo
            | summarize SloMappingCount = dcount(InternalSloId) by InternalCujoId;
            let LatestCujoToMonitor = CujoToMonitorRelationship
            | summarize arg_max(IngestionTimestamp, *) by InternalCujoId, InternalMonitorId
            | where IsActive == true;
            let MonitorAgg = LatestCujoToMonitor
            | summarize MonitorMappingCount = dcount(InternalMonitorId) by InternalCujoId;
            LatestCujos
            | join kind=leftouter SloAgg on InternalCujoId
            | join kind=leftouter MonitorAgg on InternalCujoId
            | project InternalCujoId, CujoName, CujoDescription, ServiceTreeId, ServiceName,
                      OwningContactId, ImplementationETA, HasExceptionForSLOCreation, IsImplementationBlocked,
                      SloMappingCount    = coalesce(SloMappingCount, 0),
                      MonitorMappingCount = coalesce(MonitorMappingCount, 0),
                      isSloMapped        = coalesce(SloMappingCount, 0) > 0,
                      isMonitorMapped    = coalesce(MonitorMappingCount, 0) > 0
            | extend isCujoMapped = isSloMapped or isMonitorMapped
            | order by CujoName asc
            """;

        _logger.LogInformation(
            "Fetching CUJOs for service '{ServiceTreeId}' from {Cluster}/{Database}.",
            serviceTreeId, ClusterUri, DatabaseName);

        var props = new ClientRequestProperties
        {
            ClientRequestId = $"ServiceHealthAssistant;FetchCujos;{Guid.NewGuid()}"
        };
        props.SetParameter("_serviceTreeId", serviceTreeId);

        var results = new List<CujoMappingRow>();

        await Task.Run(() =>
        {
            using var reader = _queryProvider.ExecuteQuery(DatabaseName, kql, props);
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var internalCujoId            = reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0)) ?? string.Empty;
                var cujoName                  = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader.GetValue(1)) ?? string.Empty;
                var cujoDescription           = reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2));
                var serviceTreeIdVal          = reader.IsDBNull(3) ? string.Empty : Convert.ToString(reader.GetValue(3)) ?? string.Empty;
                var serviceName               = reader.IsDBNull(4) ? string.Empty : Convert.ToString(reader.GetValue(4)) ?? string.Empty;
                var owningContactId           = reader.IsDBNull(5) ? null : Convert.ToString(reader.GetValue(5));
                var implementationEta         = reader.IsDBNull(6) ? null : Convert.ToString(reader.GetValue(6));
                var hasExceptionForSloCreation = !reader.IsDBNull(7) && Convert.ToBoolean(reader.GetValue(7));
                var isImplementationBlocked   = !reader.IsDBNull(8) && Convert.ToBoolean(reader.GetValue(8));
                var sloMappingCount           = reader.IsDBNull(9)  ? 0 : Convert.ToInt32(reader.GetValue(9));
                var monitorMappingCount       = reader.IsDBNull(10) ? 0 : Convert.ToInt32(reader.GetValue(10));
                var isSloMapped               = !reader.IsDBNull(11) && Convert.ToBoolean(reader.GetValue(11));
                var isMonitorMapped           = !reader.IsDBNull(12) && Convert.ToBoolean(reader.GetValue(12));
                var isCujoMapped              = !reader.IsDBNull(13) && Convert.ToBoolean(reader.GetValue(13));

                if (string.IsNullOrWhiteSpace(internalCujoId)) continue;

                results.Add(new CujoMappingRow(
                    InternalCujoId:            internalCujoId,
                    CujoName:                  cujoName,
                    CujoDescription:           NullIfEmpty(cujoDescription),
                    ServiceTreeId:             serviceTreeIdVal,
                    ServiceName:               serviceName,
                    OwningContactId:           NullIfEmpty(owningContactId),
                    ImplementationEta:         NullIfEmpty(implementationEta),
                    HasExceptionForSloCreation: hasExceptionForSloCreation,
                    IsImplementationBlocked:   isImplementationBlocked,
                    SloMappingCount:           sloMappingCount,
                    MonitorMappingCount:       monitorMappingCount,
                    IsSloMapped:               isSloMapped,
                    IsMonitorMapped:           isMonitorMapped,
                    IsCujoMapped:              isCujoMapped));
            }
        }, cancellationToken);

        _logger.LogInformation(
            "Retrieved {Count} CUJO(s) for service '{ServiceTreeId}' from sherica-prod.",
            results.Count, serviceTreeId);

        return results.AsReadOnly();
    }

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
