namespace ServiceHealthAssistant.Repair;

/// <summary>
/// Result model returned by a single monitor metadata update attempt.
/// </summary>
public sealed record MonitorMetadataUpdateResult(
    bool Succeeded,
    string? ErrorMessage = null);

/// <summary>
/// Abstraction for reading and writing Geneva monitor capability metadata.
///
/// Read path:  cluster('geneva.kusto.windows.net').database('genevahealthconfigs').MonitorConfigMetadata
///             – queries the Metadata dynamic column for the four BrainIntent.* keys.
///
/// Write path: Geneva metadata update REST/ARM endpoint.
///             TODO: contract not present in repo – see docs/repair-agent-bulk-metadata.md
///             "Open Questions / Assumptions" section for the missing contract detail.
///             The stub implementation (<see cref="StubGenevaMonitorMetadataClient"/>) is wired
///             by default and must be replaced once the real endpoint is confirmed.
/// </summary>
public interface IGenevaMonitorMetadataClient
{
    /// <summary>
    /// Returns the current Brain capability metadata values for <paramref name="monitorId"/>
    /// within <paramref name="accountId"/>, keyed by <see cref="BrainCapabilityMetadataKeys"/>.
    /// Returns an empty dictionary when no metadata is set.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetCapabilityMetadataAsync(
        string accountId,
        string monitorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the supplied <paramref name="metadataUpdates"/> to the Geneva monitor
    /// identified by <paramref name="monitorId"/> within <paramref name="accountId"/>.
    /// Implementations are expected to be idempotent: re-applying the same values is safe.
    /// </summary>
    Task<MonitorMetadataUpdateResult> UpdateCapabilityMetadataAsync(
        string accountId,
        string monitorId,
        IReadOnlyDictionary<string, string> metadataUpdates,
        CancellationToken cancellationToken = default);
}
