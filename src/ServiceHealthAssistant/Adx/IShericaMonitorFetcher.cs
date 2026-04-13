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
    /// Returns all monitors found for the given <paramref name="serviceId"/> via
    /// <c>GetIntegratedMonitorOutageCoverageDrillThrough</c>, looking back 365 days.
    /// </summary>
    Task<IReadOnlyList<MonitorEvaluationInput>> FetchMonitorsForServiceAsync(
        string serviceId,
        CancellationToken cancellationToken = default);
}
