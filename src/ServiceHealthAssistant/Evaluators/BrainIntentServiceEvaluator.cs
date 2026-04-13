using ServiceHealthAssistant.Adx;
using ServiceHealthAssistant.Models;
using ServiceHealthAssistant.Rules;

namespace ServiceHealthAssistant.Evaluators;

/// <summary>
/// Input descriptor for a single monitor to be evaluated.
/// Extends <see cref="MonitorBrainIntegrationRequest"/> with an explicit
/// <see cref="MonitorId"/> and optional <see cref="LinkedICMIncidentId"/>.
/// </summary>
public sealed record MonitorEvaluationInput(
    string MonitorId,
    string MonitorName,
    string? MonitorType = null,
    string? LinkedCujoJourney = null,
    bool OutageDrivingIcmMapping = false,
    DetectedImpactType DetectedImpactType = DetectedImpactType.Operational,
    bool LidPresence = false,
    bool RegionalScopeDetectable = false,
    bool SubscriptionScopeDetectable = false,
    HistoricalPrecision HistoricalPrecision = HistoricalPrecision.Low,
    SignalStability SignalStability = SignalStability.Unknown,
    bool UsedInOutageDeclarationPreviously = false,
    bool CommunicationRelevantImpact = false,
    string? LinkedICMIncidentId = null);

/// <summary>
/// Orchestrates per-monitor Brain Intent evaluation for an entire service and
/// persists the results in batches to ADX.
/// </summary>
public sealed class BrainIntentServiceEvaluator
{
    private const string EvaluationSource = "MCP:evaluate_monitor_brain_integration";
    private const int DefaultBatchSize = 200;
    private const int DefaultMaxParallelism = 8;

    private readonly IKustoBrainIntentWriter _writer;

    public BrainIntentServiceEvaluator(IKustoBrainIntentWriter writer)
    {
        _writer = writer;
    }

    /// <summary>
    /// Evaluate all monitors for a service and persist the rows to ADX.
    /// </summary>
    /// <param name="serviceId">Stable service identifier.</param>
    /// <param name="serviceName">Human-readable service name (may be empty).</param>
    /// <param name="monitors">Monitor descriptors fetched from Geneva or provided by the caller.</param>
    /// <param name="evaluationTimestamp">
    ///   Stable UTC timestamp for the whole evaluation run (idempotency anchor).
    ///   Defaults to <see cref="DateTime.UtcNow"/> when not provided.
    /// </param>
    /// <param name="batchSize">Number of rows per ADX ingestion call.</param>
    /// <param name="maxParallelism">Maximum concurrent evaluations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All evaluated rows (useful for callers that need the results in-process).</returns>
    public async Task<IReadOnlyList<BrainIntentEvaluationRow>> EvaluateAndPersistAsync(
        string serviceId,
        string serviceName,
        IReadOnlyList<MonitorEvaluationInput> monitors,
        DateTime? evaluationTimestamp = null,
        int batchSize = DefaultBatchSize,
        int maxParallelism = DefaultMaxParallelism,
        CancellationToken cancellationToken = default)
    {
        var timestamp = evaluationTimestamp ?? DateTime.UtcNow;
        var results = new BrainIntentEvaluationRow[monitors.Count];

        using var semaphore = new SemaphoreSlim(maxParallelism, maxParallelism);

        var tasks = monitors.Select((monitor, index) => Task.Run(async () =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var row = Evaluate(serviceId, serviceName, monitor, timestamp);
                results[index] = row;
            }
            finally
            {
                semaphore.Release();
            }
        }, cancellationToken));

        await Task.WhenAll(tasks);

        // Persist in batches to avoid per-row ingestion overhead.
        for (int i = 0; i < results.Length; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = results.Skip(i).Take(batchSize).ToList().AsReadOnly();
            await _writer.IngestBatchAsync(batch, cancellationToken);
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Normalise a single monitor evaluation result into an ADX row.
    /// Exposed internally so unit tests can verify the mapping logic.
    /// </summary>
    internal static BrainIntentEvaluationRow Evaluate(
        string serviceId,
        string serviceName,
        MonitorEvaluationInput monitor,
        DateTime evaluationTimestamp)
    {
        var req = new MonitorBrainIntegrationRequest(
            MonitorName:                     monitor.MonitorName,
            MonitorType:                     monitor.MonitorType,
            LinkedCujoJourney:               monitor.LinkedCujoJourney,
            OutageDrivingIcmMapping:         monitor.OutageDrivingIcmMapping,
            DetectedImpactType:              monitor.DetectedImpactType,
            LidPresence:                     monitor.LidPresence,
            RegionalScopeDetectable:         monitor.RegionalScopeDetectable,
            SubscriptionScopeDetectable:     monitor.SubscriptionScopeDetectable,
            HistoricalPrecision:             monitor.HistoricalPrecision,
            SignalStability:                 monitor.SignalStability,
            UsedInOutageDeclarationPreviously: monitor.UsedInOutageDeclarationPreviously,
            CommunicationRelevantImpact:     monitor.CommunicationRelevantImpact);

        var result = ServiceHealthRules.EvaluateMonitorBrainIntegration(req);

        return new BrainIntentEvaluationRow(
            ServiceId:                  serviceId,
            ServiceName:                serviceName,
            MonitorId:                  monitor.MonitorId,
            MonitorName:                monitor.MonitorName,
            MonitorType:                monitor.MonitorType ?? string.Empty,
            IsSLI:                      false,
            BrainAwareness:             result.BrainIntent.BrainAwareness,
            OutageDeclaration:          result.BrainIntent.OutageDeclaration,
            DeploymentStops:            result.BrainIntent.DeploymentStops,
            AutoComms:                  result.BrainIntent.AutoComms,
            EvaluationSource:           EvaluationSource,
            EvaluationTimestamp:        evaluationTimestamp,
            CujoJourney:                monitor.LinkedCujoJourney,
            LinkedICMIncidentId:        monitor.LinkedICMIncidentId,
            LIDPresent:                 monitor.LidPresence,
            RegionalScopeDetectable:    monitor.RegionalScopeDetectable,
            SubscriptionScopeDetectable: monitor.SubscriptionScopeDetectable,
            HistoricalPrecision:        monitor.HistoricalPrecision,
            SignalStability:            monitor.SignalStability,
            CommunicationRelevant:      monitor.CommunicationRelevantImpact);
    }
}
