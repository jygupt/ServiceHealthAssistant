using Microsoft.Extensions.Logging;
using ServiceHealthAssistant.Models;

namespace ServiceHealthAssistant.Repair;

/// <summary>
/// Orchestrates bulk Brain capability metadata updates for Geneva Service Monitors
/// across a service boundary, eliminating per-monitor manual UI edits and enabling
/// dashboard-driven governance at service scale.
///
/// Capabilities:
///   A. Bulk metadata application driven by Brain Intent Dashboard monitor sets.
///   B. Deterministic classification maintenance (drift prevention).
///   C. Promotes ShouldBeEnabled → Enabled transitions.
///   D. Verification: "Set" (Geneva MonitorConfigMetadata) vs "Flowing" (IcM / Brain propagation).
///   E. NextAction alignment (consumed from <see cref="DashboardMonitorDescriptor.NextAction"/>).
///
/// Safety gates:
///   • DryRun = true by default – no writes occur until explicitly opted in.
///   • Idempotent: only delta changes are applied; already-correct metadata is skipped.
///   • Pre-change state is recorded per monitor to support rollback.
///   • BatchSize + MaxConcurrency throttle Geneva API load.
///   • Exponential back-off retry (<see cref="RetryPolicy"/>) handles transient failures.
/// </summary>
public sealed class BulkGenevaBrainCapabilityMetadataRepairAgent
{
    private static readonly TimeSpan DefaultRetryBackoff = TimeSpan.FromSeconds(2);

    private readonly IGenevaMonitorMetadataClient _metadataClient;
    private readonly IDashboardMonitorSetProvider _dashboardProvider;
    private readonly IPropagationValidator        _propagationValidator;
    private readonly ILogger<BulkGenevaBrainCapabilityMetadataRepairAgent> _logger;

    public BulkGenevaBrainCapabilityMetadataRepairAgent(
        IGenevaMonitorMetadataClient metadataClient,
        IDashboardMonitorSetProvider dashboardProvider,
        IPropagationValidator        propagationValidator,
        ILogger<BulkGenevaBrainCapabilityMetadataRepairAgent> logger)
    {
        _metadataClient       = metadataClient;
        _dashboardProvider    = dashboardProvider;
        _propagationValidator = propagationValidator;
        _logger               = logger;
    }

    // -----------------------------------------------------------------------
    // Public entry point
    // -----------------------------------------------------------------------

    /// <summary>
    /// Executes a bulk repair run as described by <paramref name="request"/>.
    /// </summary>
    public async Task<BrainCapabilityRepairResult> ExecuteAsync(
        BrainCapabilityRepairRequest request,
        CancellationToken cancellationToken = default)
    {
        var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId)
            ? Guid.NewGuid().ToString()
            : request.CorrelationId;

        var executionTimestamp = DateTime.UtcNow;

        _logger.LogInformation(
            "BulkRepair [{CorrelationId}] starting. ServiceId={ServiceId} DryRun={DryRun} " +
            "BatchSize={BatchSize} MaxConcurrency={MaxConcurrency}",
            correlationId, request.ServiceId, request.DryRun,
            request.BatchSize, request.MaxConcurrency);

        // ------------------------------------------------------------------
        // 1. Resolve the monitor set.
        // ------------------------------------------------------------------
        var monitorDescriptors = await ResolveMonitorSetAsync(request, cancellationToken);

        _logger.LogInformation(
            "BulkRepair [{CorrelationId}] resolved {Count} monitor(s) for service '{ServiceId}'.",
            correlationId, monitorDescriptors.Count, request.ServiceId);

        if (monitorDescriptors.Count == 0)
        {
            return EmptyResult(request.ServiceId, request.ServiceName, correlationId,
                request.DryRun, executionTimestamp);
        }

        // ------------------------------------------------------------------
        // 2. Process monitors in batches.
        // ------------------------------------------------------------------
        var monitorResults = new List<MonitorRepairResult>(monitorDescriptors.Count);
        var auditLog       = new List<RepairAuditEntry>();
        var perCapUpdated  = new Dictionary<string, int>(BrainCapabilityMetadataKeys.All.Count);
        foreach (var key in BrainCapabilityMetadataKeys.All)
            perCapUpdated[key] = 0;

        // O(1) lookup for transition tracking.
        var descriptorById = monitorDescriptors.ToDictionary(m => m.MonitorId, m => m);

        int shouldBeEnabledCandidatesUpdated = 0;
        bool aborted = false;

        int batchSize = Math.Max(1, request.BatchSize);

        for (int batchStart = 0; batchStart < monitorDescriptors.Count && !aborted; batchStart += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = monitorDescriptors
                .Skip(batchStart)
                .Take(batchSize)
                .ToList();

            _logger.LogInformation(
                "BulkRepair [{CorrelationId}] processing batch {From}–{To} of {Total}.",
                correlationId,
                batchStart + 1,
                Math.Min(batchStart + batchSize, monitorDescriptors.Count),
                monitorDescriptors.Count);

            var batchResults = await ProcessBatchAsync(
                batch, request, correlationId, executionTimestamp,
                perCapUpdated, auditLog, cancellationToken);

            monitorResults.AddRange(batchResults);

            // Count ShouldBeEnabled → Enabled transitions in this batch.
            foreach (var r in batchResults)
            {
                foreach (var (key, newVal) in r.AppliedChanges)
                {
                    // A transition is counted when the dashboard showed ShouldBeEnabled
                    // and we applied Enabled.
                    if (descriptorById.TryGetValue(r.MonitorId, out var descriptor))
                    {
                        var previousStatus = GetCurrentStatus(descriptor, key);
                        if (previousStatus == BrainIntentStatus.ShouldBeEnabled
                            && string.Equals(newVal, nameof(BrainIntentStatus.Enabled),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            shouldBeEnabledCandidatesUpdated++;
                        }
                    }
                }
            }

            // StopOnFirstFailure gate.
            if (request.StopOnFirstFailure && batchResults.Any(r => r.Errors.Count > 0))
            {
                _logger.LogWarning(
                    "BulkRepair [{CorrelationId}] stopping after first failure (StopOnFirstFailure=true).",
                    correlationId);
                aborted = true;
            }
        }

        // ------------------------------------------------------------------
        // 3. Aggregate summary.
        // ------------------------------------------------------------------
        int succeeded          = monitorResults.Count(r => r.Errors.Count == 0 && r.AttemptedChanges.Count > 0);
        int failed             = monitorResults.Count(r => r.Errors.Count > 0);
        int pendingPropagation = monitorResults.Count(r =>
            r.Errors.Count == 0 &&
            r.FlowValidationStatus == MetadataValidationStatus.PendingPropagation);

        _logger.LogInformation(
            "BulkRepair [{CorrelationId}] complete. Targeted={Total} Succeeded={Succeeded} " +
            "Failed={Failed} PendingPropagation={Pending} DryRun={DryRun}",
            correlationId, monitorDescriptors.Count, succeeded, failed,
            pendingPropagation, request.DryRun);

        return new BrainCapabilityRepairResult(
            ServiceId:                         request.ServiceId,
            ServiceName:                       request.ServiceName,
            CorrelationId:                     correlationId,
            DryRun:                            request.DryRun,
            TotalTargeted:                     monitorDescriptors.Count,
            Succeeded:                         succeeded,
            Failed:                            failed,
            PendingPropagation:                pendingPropagation,
            PerCapabilityUpdated:              perCapUpdated,
            ShouldBeEnabledCandidatesUpdated:  shouldBeEnabledCandidatesUpdated,
            MonitorResults:                    monitorResults.AsReadOnly(),
            AuditLog:                          auditLog.AsReadOnly(),
            ExecutionTimestamp:                executionTimestamp);
    }

    // -----------------------------------------------------------------------
    // Monitor set resolution
    // -----------------------------------------------------------------------

    private async Task<IReadOnlyList<DashboardMonitorDescriptor>> ResolveMonitorSetAsync(
        BrainCapabilityRepairRequest request,
        CancellationToken cancellationToken)
    {
        // Explicit monitor ID list takes precedence.
        if (request.MonitorIds is { Count: > 0 })
        {
            return request.MonitorIds
                .Select(id => new DashboardMonitorDescriptor(
                    MonitorId:         id,
                    MonitorName:       id,
                    AccountId:         request.GenevaAccountId,
                    ServiceId:         request.ServiceId,
                    BrainAwareness:    BrainIntentStatus.NotClassified,
                    OutageDeclaration: BrainIntentStatus.NotClassified,
                    DeploymentStops:   BrainIntentStatus.NotClassified,
                    AutoComms:         BrainIntentStatus.NotClassified,
                    NextAction:        null))
                .ToList()
                .AsReadOnly();
        }

        // Fall back to dashboard provider.
        var descriptors = await _dashboardProvider.GetMonitorsForServiceAsync(
            request.ServiceId,
            request.DashboardActionRequiredFilter,
            cancellationToken);

        // Populate AccountId from the request (the dashboard table doesn't store it).
        return descriptors
            .Select(d => d with { AccountId = request.GenevaAccountId })
            .ToList()
            .AsReadOnly();
    }

    // -----------------------------------------------------------------------
    // Batch processing
    // -----------------------------------------------------------------------

    private async Task<IReadOnlyList<MonitorRepairResult>> ProcessBatchAsync(
        IReadOnlyList<DashboardMonitorDescriptor> batch,
        BrainCapabilityRepairRequest request,
        string correlationId,
        DateTime executionTimestamp,
        Dictionary<string, int> perCapUpdated,
        List<RepairAuditEntry> auditLog,
        CancellationToken cancellationToken)
    {
        var results     = new MonitorRepairResult[batch.Count];
        var semaphore   = new SemaphoreSlim(Math.Max(1, request.MaxConcurrency));
        var retryBackoff = request.RetryBackoff ?? DefaultRetryBackoff;

        var tasks = batch.Select((descriptor, idx) => Task.Run(async () =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var result = await ProcessMonitorAsync(
                    descriptor, request, correlationId, executionTimestamp,
                    retryBackoff, perCapUpdated, auditLog, cancellationToken);
                results[idx] = result;
            }
            finally
            {
                semaphore.Release();
            }
        }, cancellationToken)).ToList();

        await Task.WhenAll(tasks);
        return results.AsReadOnly();
    }

    // -----------------------------------------------------------------------
    // Per-monitor processing
    // -----------------------------------------------------------------------

    private async Task<MonitorRepairResult> ProcessMonitorAsync(
        DashboardMonitorDescriptor descriptor,
        BrainCapabilityRepairRequest request,
        string correlationId,
        DateTime executionTimestamp,
        TimeSpan retryBackoff,
        Dictionary<string, int> perCapUpdated,
        List<RepairAuditEntry> auditLog,
        CancellationToken cancellationToken)
    {
        var errors          = new List<string>();
        var attemptedChanges = new Dictionary<string, string>();
        var appliedChanges   = new Dictionary<string, string>();

        // ------------------------------------------------------------------
        // A. Read current metadata (idempotency: only apply deltas).
        // ------------------------------------------------------------------
        IReadOnlyDictionary<string, string> currentMetadata;
        try
        {
            currentMetadata = await RetryPolicy.ExecuteAsync(
                ct => _metadataClient.GetCapabilityMetadataAsync(
                    descriptor.AccountId, descriptor.MonitorId, ct),
                maxAttempts: request.MaxRetry,
                baseDelay: retryBackoff,
                onRetry: (attempt, ex, delay) =>
                    _logger.LogWarning(
                        "BulkRepair [{CorrelationId}] GetMetadata retry {Attempt} for " +
                        "monitor '{MonitorId}' in {Delay:F1}s: {Message}",
                        correlationId, attempt, descriptor.MonitorId, delay.TotalSeconds, ex.Message),
                cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "BulkRepair [{CorrelationId}] failed to read metadata for '{MonitorId}': {Message}",
                correlationId, descriptor.MonitorId, ex.Message);
            errors.Add($"ReadMetadata: {ex.Message}");
            return new MonitorRepairResult(
                descriptor.MonitorId, descriptor.MonitorName,
                attemptedChanges, appliedChanges,
                MetadataValidationStatus.Failed, MetadataValidationStatus.Skipped,
                errors.AsReadOnly());
        }

        // ------------------------------------------------------------------
        // B. Compute delta (only keys that need changing).
        // ------------------------------------------------------------------
        var desiredMetadata = BuildDesiredMetadata(request.DesiredStates);

        foreach (var (key, desired) in desiredMetadata)
        {
            currentMetadata.TryGetValue(key, out var current);
            if (!string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
            {
                attemptedChanges[key] = desired;
            }
        }

        if (attemptedChanges.Count == 0)
        {
            // Already correct – idempotent no-op.
            _logger.LogInformation(
                "BulkRepair [{CorrelationId}] monitor '{MonitorId}' already up-to-date; skipping.",
                correlationId, descriptor.MonitorId);

            EmitAuditNoOp(auditLog, correlationId, request.ServiceId,
                descriptor.MonitorId, desiredMetadata, currentMetadata, executionTimestamp);

            return new MonitorRepairResult(
                descriptor.MonitorId, descriptor.MonitorName,
                attemptedChanges, appliedChanges,
                MetadataValidationStatus.Verified, MetadataValidationStatus.Skipped,
                errors.AsReadOnly());
        }

        // ------------------------------------------------------------------
        // C. Apply changes (skipped in DryRun mode).
        // ------------------------------------------------------------------
        if (!request.DryRun)
        {
            MonitorMetadataUpdateResult updateResult;
            try
            {
                updateResult = await RetryPolicy.ExecuteAsync(
                    ct => _metadataClient.UpdateCapabilityMetadataAsync(
                        descriptor.AccountId, descriptor.MonitorId, attemptedChanges, ct),
                    maxAttempts: request.MaxRetry,
                    baseDelay: retryBackoff,
                    onRetry: (attempt, ex, delay) =>
                        _logger.LogWarning(
                            "BulkRepair [{CorrelationId}] UpdateMetadata retry {Attempt} for " +
                            "monitor '{MonitorId}' in {Delay:F1}s: {Message}",
                            correlationId, attempt, descriptor.MonitorId,
                            delay.TotalSeconds, ex.Message),
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                errors.Add($"UpdateMetadata: {ex.Message}");
                _logger.LogError(
                    "BulkRepair [{CorrelationId}] failed to update metadata for '{MonitorId}': {Message}",
                    correlationId, descriptor.MonitorId, ex.Message);

                EmitAuditEntries(auditLog, correlationId, request.ServiceId,
                    descriptor.MonitorId, attemptedChanges, currentMetadata,
                    "Failed", executionTimestamp);

                return new MonitorRepairResult(
                    descriptor.MonitorId, descriptor.MonitorName,
                    attemptedChanges, appliedChanges,
                    MetadataValidationStatus.Failed, MetadataValidationStatus.Skipped,
                    errors.AsReadOnly());
            }

            if (!updateResult.Succeeded)
            {
                var msg = updateResult.ErrorMessage ?? "Unknown error.";
                errors.Add($"UpdateMetadata: {msg}");
                _logger.LogWarning(
                    "BulkRepair [{CorrelationId}] update not applied for '{MonitorId}': {Message}",
                    correlationId, descriptor.MonitorId, msg);

                EmitAuditEntries(auditLog, correlationId, request.ServiceId,
                    descriptor.MonitorId, attemptedChanges, currentMetadata,
                    "Failed", executionTimestamp);

                return new MonitorRepairResult(
                    descriptor.MonitorId, descriptor.MonitorName,
                    attemptedChanges, appliedChanges,
                    MetadataValidationStatus.Failed, MetadataValidationStatus.Skipped,
                    errors.AsReadOnly());
            }

            foreach (var kv in attemptedChanges)
            {
                appliedChanges[kv.Key] = kv.Value;

                lock (perCapUpdated)
                    perCapUpdated[kv.Key]++;
            }

            EmitAuditEntries(auditLog, correlationId, request.ServiceId,
                descriptor.MonitorId, appliedChanges, currentMetadata,
                "Applied", executionTimestamp);

            // ------------------------------------------------------------------
            // D. Verify: Set and Flowing.
            // ------------------------------------------------------------------
            var propagationResults = await _propagationValidator.ValidateAsync(
                descriptor.AccountId, descriptor.MonitorId, appliedChanges, cancellationToken);

            var setStatus  = propagationResults.All(r => r.IsSet)
                ? MetadataValidationStatus.Verified
                : MetadataValidationStatus.Failed;

            // IsFlowing == null means the downstream confirmation is not yet available.
            bool anyPending  = propagationResults.Any(r => r.IsFlowing is null);
            bool allFlowing  = propagationResults.All(r => r.IsFlowing == true);
            var flowStatus   = allFlowing ? MetadataValidationStatus.Verified
                             : anyPending ? MetadataValidationStatus.PendingPropagation
                                          : MetadataValidationStatus.Failed;

            return new MonitorRepairResult(
                descriptor.MonitorId, descriptor.MonitorName,
                attemptedChanges, appliedChanges,
                setStatus, flowStatus,
                errors.AsReadOnly());
        }
        else
        {
            // DryRun – report what would be applied.
            _logger.LogInformation(
                "BulkRepair [{CorrelationId}] DryRun: would update {Count} key(s) on '{MonitorId}'.",
                correlationId, attemptedChanges.Count, descriptor.MonitorId);

            EmitAuditEntries(auditLog, correlationId, request.ServiceId,
                descriptor.MonitorId, attemptedChanges, currentMetadata,
                "DryRun", executionTimestamp);

            return new MonitorRepairResult(
                descriptor.MonitorId, descriptor.MonitorName,
                attemptedChanges, appliedChanges,   // appliedChanges is empty in DryRun
                MetadataValidationStatus.Skipped,
                MetadataValidationStatus.Skipped,
                errors.AsReadOnly());
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the desired key-value metadata dictionary from the request's
    /// <see cref="CapabilityTargetStates"/>, skipping null (unchanged) capabilities.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildDesiredMetadata(
        CapabilityTargetStates states)
    {
        var dict = new Dictionary<string, string>(4);

        if (states.BrainAwareness.HasValue)
            dict[BrainCapabilityMetadataKeys.BrainAwareness] = states.BrainAwareness.Value.ToString();

        if (states.OutageDeclaration.HasValue)
            dict[BrainCapabilityMetadataKeys.OutageDeclaration] = states.OutageDeclaration.Value.ToString();

        if (states.DeploymentStops.HasValue)
            dict[BrainCapabilityMetadataKeys.DeploymentStops] = states.DeploymentStops.Value.ToString();

        if (states.AutoComms.HasValue)
            dict[BrainCapabilityMetadataKeys.AutoComms] = states.AutoComms.Value.ToString();

        return dict;
    }

    private static BrainIntentStatus GetCurrentStatus(
        DashboardMonitorDescriptor descriptor, string key) => key switch
    {
        BrainCapabilityMetadataKeys.BrainAwareness    => descriptor.BrainAwareness,
        BrainCapabilityMetadataKeys.OutageDeclaration => descriptor.OutageDeclaration,
        BrainCapabilityMetadataKeys.DeploymentStops   => descriptor.DeploymentStops,
        BrainCapabilityMetadataKeys.AutoComms         => descriptor.AutoComms,
        _                                             => BrainIntentStatus.NotClassified
    };

    private static void EmitAuditEntries(
        List<RepairAuditEntry> auditLog,
        string correlationId,
        string serviceId,
        string monitorId,
        IReadOnlyDictionary<string, string> changes,
        IReadOnlyDictionary<string, string> previousMetadata,
        string status,
        DateTime timestamp)
    {
        foreach (var (key, newValue) in changes)
        {
            previousMetadata.TryGetValue(key, out var prev);
            lock (auditLog)
            {
                auditLog.Add(new RepairAuditEntry(
                    CorrelationId: correlationId,
                    ServiceId:     serviceId,
                    MonitorId:     monitorId,
                    Capability:    key,
                    PreviousValue: prev ?? string.Empty,
                    NewValue:      newValue,
                    Status:        status,
                    ExecutedBy:    "ServiceHealthAssistant/BulkRepairAgent",
                    Timestamp:     timestamp,
                    Reason:        "Bulk Brain capability metadata repair"));
            }
        }
    }

    private static void EmitAuditNoOp(
        List<RepairAuditEntry> auditLog,
        string correlationId,
        string serviceId,
        string monitorId,
        IReadOnlyDictionary<string, string> desired,
        IReadOnlyDictionary<string, string> current,
        DateTime timestamp)
    {
        foreach (var key in desired.Keys)
        {
            current.TryGetValue(key, out var val);
            lock (auditLog)
            {
                auditLog.Add(new RepairAuditEntry(
                    CorrelationId: correlationId,
                    ServiceId:     serviceId,
                    MonitorId:     monitorId,
                    Capability:    key,
                    PreviousValue: val ?? string.Empty,
                    NewValue:      val ?? string.Empty,
                    Status:        "NoOp",
                    ExecutedBy:    "ServiceHealthAssistant/BulkRepairAgent",
                    Timestamp:     timestamp,
                    Reason:        "Metadata already at desired state"));
            }
        }
    }

    private static BrainCapabilityRepairResult EmptyResult(
        string serviceId, string serviceName,
        string correlationId, bool dryRun,
        DateTime timestamp)
        => new(
            ServiceId:                        serviceId,
            ServiceName:                      serviceName,
            CorrelationId:                    correlationId,
            DryRun:                           dryRun,
            TotalTargeted:                    0,
            Succeeded:                        0,
            Failed:                           0,
            PendingPropagation:               0,
            PerCapabilityUpdated:             BrainCapabilityMetadataKeys.All
                                                .ToDictionary(k => k, _ => 0),
            ShouldBeEnabledCandidatesUpdated: 0,
            MonitorResults:                   [],
            AuditLog:                         [],
            ExecutionTimestamp:               timestamp);
}
