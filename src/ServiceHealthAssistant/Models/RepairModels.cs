namespace ServiceHealthAssistant.Models;

// ---------------------------------------------------------------------------
// Repair agent – input/output contracts
// ---------------------------------------------------------------------------

/// <summary>
/// Per-capability target states for a bulk Brain capability metadata repair run.
/// A null value means "leave this capability unchanged".
/// </summary>
public sealed record CapabilityTargetStates(
    BrainIntentStatus? BrainAwareness    = null,
    BrainIntentStatus? OutageDeclaration = null,
    BrainIntentStatus? DeploymentStops   = null,
    BrainIntentStatus? AutoComms         = null);

/// <summary>
/// Input to <c>BulkGenevaBrainCapabilityMetadataRepairAgent</c>.
/// </summary>
public sealed record BrainCapabilityRepairRequest(
    /// <summary>Stable service identifier.</summary>
    string ServiceId,
    /// <summary>Human-readable service name (informational).</summary>
    string ServiceName,
    /// <summary>Geneva account ID (e.g. "sherica") – used for metadata reads and writes.</summary>
    string GenevaAccountId,
    /// <summary>Correlation ID for audit tracing.</summary>
    string CorrelationId,
    /// <summary>
    /// Explicit monitor list. When empty/null the agent will use
    /// <see cref="IDashboardMonitorSetProvider"/> to obtain the monitor set.
    /// </summary>
    IReadOnlyList<string>? MonitorIds,
    /// <summary>
    /// Optional filter forwarded to the dashboard provider.
    /// Use "Yes" to restrict to monitors marked "Action Required".
    /// </summary>
    string? DashboardActionRequiredFilter,
    /// <summary>Desired per-capability metadata states to apply.</summary>
    CapabilityTargetStates DesiredStates,
    /// <summary>
    /// When true (default) the agent computes and returns the planned changes
    /// without writing any metadata to Geneva.
    /// </summary>
    bool DryRun = true,
    /// <summary>Number of monitors processed in each batch.</summary>
    int BatchSize = 10,
    /// <summary>Maximum concurrent metadata operations within a batch.</summary>
    int MaxConcurrency = 4,
    /// <summary>Maximum retry attempts per monitor update.</summary>
    int MaxRetry = 3,
    /// <summary>Base back-off interval for retries (exponential).</summary>
    TimeSpan? RetryBackoff = null,
    /// <summary>When true the agent stops the run after the first monitor failure.</summary>
    bool StopOnFirstFailure = false);

/// <summary>Describes the validation status of a single capability metadata key.</summary>
public enum MetadataValidationStatus
{
    /// <summary>Metadata confirmed set correctly.</summary>
    Verified,
    /// <summary>Metadata is set but the value does not match the expected value.</summary>
    Failed,
    /// <summary>Metadata is set, but propagation to IcM / Brain has not yet been confirmed.</summary>
    PendingPropagation,
    /// <summary>Validation was skipped (e.g. dry-run or capability not targeted).</summary>
    Skipped,
    /// <summary>Not applicable for this capability or monitor.</summary>
    NotApplicable
}

/// <summary>Repair outcome for a single monitor.</summary>
public sealed record MonitorRepairResult(
    string MonitorId,
    string MonitorName,
    /// <summary>Key = capability metadata key, Value = intended new value.</summary>
    IReadOnlyDictionary<string, string> AttemptedChanges,
    /// <summary>Key = capability metadata key, Value = value actually confirmed written.</summary>
    IReadOnlyDictionary<string, string> AppliedChanges,
    /// <summary>Indicates whether the metadata is correctly configured on the Geneva monitor.</summary>
    MetadataValidationStatus SetValidationStatus,
    /// <summary>
    /// Indicates whether metadata has propagated end-to-end (Geneva → IcM → Brain).
    /// <see cref="MetadataValidationStatus.PendingPropagation"/> is emitted when
    /// propagation latency prevents immediate confirmation.
    /// </summary>
    MetadataValidationStatus FlowValidationStatus,
    IReadOnlyList<string> Errors);

/// <summary>Audit log entry emitted for every metadata change (or attempted change).</summary>
public sealed record RepairAuditEntry(
    string CorrelationId,
    string ServiceId,
    string MonitorId,
    string Capability,
    string PreviousValue,
    string NewValue,
    string Status,
    string ExecutedBy,
    DateTime Timestamp,
    string Reason);

/// <summary>
/// Aggregate result returned by a single
/// <c>BulkGenevaBrainCapabilityMetadataRepairAgent</c> execution.
/// </summary>
public sealed record BrainCapabilityRepairResult(
    string ServiceId,
    string ServiceName,
    string CorrelationId,
    bool DryRun,
    int TotalTargeted,
    int Succeeded,
    int Failed,
    int PendingPropagation,
    /// <summary>Number of monitors updated per capability key.</summary>
    IReadOnlyDictionary<string, int> PerCapabilityUpdated,
    /// <summary>
    /// Count of monitors that transitioned from ShouldBeEnabled → Enabled during this run.
    /// Tracks the governance KPI defined in the problem statement.
    /// </summary>
    int ShouldBeEnabledCandidatesUpdated,
    IReadOnlyList<MonitorRepairResult> MonitorResults,
    IReadOnlyList<RepairAuditEntry> AuditLog,
    DateTime ExecutionTimestamp);
