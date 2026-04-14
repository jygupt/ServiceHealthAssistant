using ServiceHealthAssistant.Evaluators;

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
/// </summary>
public interface IShericaMonitorFetcher
{
    /// <summary>
    /// Returns all monitors found for the given <paramref name="serviceOid"/> via
    /// <c>GetIntegratedMonitorOutageCoverageDrillThrough</c>, looking back 365 days.
    /// Monitors are returned regardless of CUJO mapping; missing signals influence the
    /// resulting Brain Intent classification but do not exclude the monitor.
    /// </summary>
    Task<IReadOnlyList<MonitorEvaluationInput>> FetchMonitorsForServiceAsync(
        string serviceOid,
        CancellationToken cancellationToken = default);
}
