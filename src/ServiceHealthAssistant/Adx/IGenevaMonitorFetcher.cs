using ServiceHealthAssistant.Evaluators;

namespace ServiceHealthAssistant.Adx;

/// <summary>
/// Fetches all Service Monitor metadata for a Geneva account from
/// cluster('geneva.kusto.windows.net').database('genevahealthconfigs').MonitorConfigMetadata.
/// </summary>
public interface IGenevaMonitorFetcher
{
    /// <summary>
    /// Returns all monitors found in MonitorConfigMetadata for the given
    /// Geneva <paramref name="accountId"/>, looking back 1 hour.
    /// </summary>
    Task<IReadOnlyList<MonitorEvaluationInput>> FetchMonitorsForAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default);
}
