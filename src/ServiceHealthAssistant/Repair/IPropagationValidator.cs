using ServiceHealthAssistant.Models;

namespace ServiceHealthAssistant.Repair;

/// <summary>
/// Describes whether a metadata value has propagated through the
/// Geneva → IcM → Brain ingestion pipeline ("Flowing" state).
/// </summary>
public sealed record PropagationValidationResult(
    string MonitorId,
    string Capability,
    /// <summary>Expected value that should appear in MonitorConfigMetadata.</summary>
    string ExpectedValue,
    /// <summary>Actual value observed in MonitorConfigMetadata (null if not found).</summary>
    string? ObservedValue,
    /// <summary>
    /// True when the Geneva MonitorConfigMetadata confirms the metadata is set.
    /// This is the "Set" signal in the "Set vs Flowing" concept.
    /// </summary>
    bool IsSet,
    /// <summary>
    /// True when downstream Brain ingestion has confirmed the value.
    /// False / null means propagation has not completed yet.
    ///
    /// TODO: Brain ingestion confirmation endpoint not present in repo.
    ///       This field is always false until the contract is confirmed.
    ///       See docs/repair-agent-bulk-metadata.md.
    /// </summary>
    bool? IsFlowing);

/// <summary>
/// Validates that Brain capability metadata applied to a Geneva monitor has
/// propagated end-to-end (Geneva → IcM → Brain ingestion).
///
/// Implements the "Set vs Flowing" verification step described in the problem statement.
///
/// "Set" check:  Query <c>MonitorConfigMetadata</c> at the Geneva Kusto cluster.
/// "Flowing" check: TODO – Brain ingestion confirmation surface not in repo.
///                  Returns <c>null</c> (PendingPropagation) until the contract is confirmed.
/// </summary>
public interface IPropagationValidator
{
    /// <summary>
    /// Validates whether the <paramref name="expectedMetadata"/> values are set on the
    /// monitor and (if available) confirmed as flowing through to Brain.
    /// </summary>
    /// <param name="accountId">Geneva account ID.</param>
    /// <param name="monitorId">Monitor name or GUID.</param>
    /// <param name="expectedMetadata">
    ///   Key = <see cref="BrainCapabilityMetadataKeys"/> constant,
    ///   Value = expected string representation of <see cref="BrainIntentStatus"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per expected metadata key.</returns>
    Task<IReadOnlyList<PropagationValidationResult>> ValidateAsync(
        string accountId,
        string monitorId,
        IReadOnlyDictionary<string, string> expectedMetadata,
        CancellationToken cancellationToken = default);
}
