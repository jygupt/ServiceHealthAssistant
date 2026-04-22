namespace ServiceHealthAssistant.Adx;

/// <summary>
/// Fetches CUJO metadata for a given service from the Analytics cluster using the
/// <c>CUJOMetadata</c> and <c>CujoToSloRelationship</c> / <c>CujoToMonitorRelationship</c>
/// tables.
///
/// Source:
///   cluster('sherica-prod.uksouth.kusto.windows.net').database('Analytics')
/// </summary>
public interface IShericaCujoFetcher
{
    /// <summary>
    /// Returns all active CUJOs for the given <paramref name="serviceTreeId"/> with
    /// their SLO and monitor mapping counts, indicating whether each CUJO is covered.
    /// </summary>
    Task<IReadOnlyList<CujoMappingRow>> FetchCujosForServiceAsync(
        string serviceTreeId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A single CUJO row returned from the Analytics cluster, including SLO/monitor mapping counts.
/// </summary>
public sealed record CujoMappingRow(
    string InternalCujoId,
    string CujoName,
    string? CujoDescription,
    string ServiceTreeId,
    string ServiceName,
    string? OwningContactId,
    string? ImplementationEta,
    bool HasExceptionForSloCreation,
    bool IsImplementationBlocked,
    int SloMappingCount,
    int MonitorMappingCount,
    bool IsSloMapped,
    bool IsMonitorMapped,
    bool IsCujoMapped);
