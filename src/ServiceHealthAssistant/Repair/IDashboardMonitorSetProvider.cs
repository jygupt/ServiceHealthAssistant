using ServiceHealthAssistant.Models;

namespace ServiceHealthAssistant.Repair;

/// <summary>
/// Descriptor for a monitor returned by the Brain Intent Dashboard monitor-set query.
/// </summary>
public sealed record DashboardMonitorDescriptor(
    string MonitorId,
    string MonitorName,
    string AccountId,
    string ServiceId,
    BrainIntentStatus BrainAwareness,
    BrainIntentStatus OutageDeclaration,
    BrainIntentStatus DeploymentStops,
    BrainIntentStatus AutoComms,
    /// <summary>
    /// NextAction tag from the repair-tagging system (e.g. "Apply", "Review", "Exempt").
    /// Null when no tag is present.
    /// </summary>
    string? NextAction = null);

/// <summary>
/// Abstraction that surfaces the Brain Intent Dashboard monitor set.
///
/// Implementation reads from:
///   cluster('shm-dev-uksouth-kusto.uksouth.kusto.windows.net')
///     .database('SHMDatabase')
///     .MCP_BrainIntentEvaluation
///
/// …filtering the latest evaluation row per monitor for the given service,
/// optionally filtered by ActionRequired state (monitors with at least one
/// capability in <c>ShouldBeEnabled</c>).
///
/// TODO: The Brain Intent Dashboard repair tagging / NextAction surface
///       is not present in the repo.  The <see cref="NextAction"/> field
///       on <see cref="DashboardMonitorDescriptor"/> will be null until the
///       contract is confirmed.  See docs/repair-agent-bulk-metadata.md.
/// </summary>
public interface IDashboardMonitorSetProvider
{
    /// <summary>
    /// Returns monitors for <paramref name="serviceId"/> from the Brain Intent Dashboard.
    /// </summary>
    /// <param name="serviceId">Stable service identifier.</param>
    /// <param name="actionRequiredFilter">
    ///   Pass "Yes" to return only monitors that have at least one capability in
    ///   <see cref="BrainIntentStatus.ShouldBeEnabled"/> ("Action Required = Yes").
    ///   Pass null or empty to return all monitors.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<DashboardMonitorDescriptor>> GetMonitorsForServiceAsync(
        string serviceId,
        string? actionRequiredFilter,
        CancellationToken cancellationToken = default);
}
