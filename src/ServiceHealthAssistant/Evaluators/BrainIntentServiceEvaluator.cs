using Microsoft.Extensions.Logging;
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
    string? LinkedCujoJourney = null,
    bool isBrainAOD = false,
    DetectedImpactType DetectedImpactType = DetectedImpactType.Operational,
    bool isLIDCompliant = false,
    bool RegionalScopeDetectable = false,
    bool SubscriptionScopeDetectable = false,
    HistoricalPrecision HistoricalPrecision = HistoricalPrecision.Low,
    SignalStability SignalStability = SignalStability.Unknown,
    bool UsedInOutageDeclarationPreviously = false,
    bool CommunicationRelevantImpact = false,
    string? AllIncidents = null);

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
    private readonly IShericaMonitorFetcher _shericaFetcher;
    private readonly ILogger<BrainIntentServiceEvaluator> _logger;

    public BrainIntentServiceEvaluator(
        IKustoBrainIntentWriter writer,
        IShericaMonitorFetcher shericaFetcher,
        ILogger<BrainIntentServiceEvaluator> logger)
    {
        _writer = writer;
        _shericaFetcher = shericaFetcher;
        _logger = logger;
    }

    /// <summary>
    /// Evaluate all monitors for a service without persisting results.
    /// </summary>
    /// <param name="serviceId">Stable service identifier.</param>
    /// <param name="serviceName">Human-readable service name (may be empty).</param>
    /// <param name="monitors">Monitor descriptors fetched from Geneva or provided by the caller.</param>
    /// <param name="evaluationTimestamp">
    ///   Stable UTC timestamp for the whole evaluation run (idempotency anchor).
    ///   Defaults to <see cref="DateTime.UtcNow"/> when not provided.
    /// </param>
    /// <param name="maxParallelism">Maximum concurrent evaluations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All evaluated rows.</returns>
    public async Task<IReadOnlyList<BrainIntentEvaluationRow>> EvaluateAsync(
        string serviceId,
        string serviceName,
        IReadOnlyList<MonitorEvaluationInput> monitors,
        DateTime? evaluationTimestamp = null,
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
                results[index] = Evaluate(serviceId, serviceName, monitor, timestamp);
            }
            finally
            {
                semaphore.Release();
            }
        }, cancellationToken));

        await Task.WhenAll(tasks);
        return results.AsReadOnly();
    }

    /// <summary>
    /// Fetch all monitors for a service from
    /// <c>GetIntegratedMonitorOutageCoverageDrillThrough</c>, evaluate every monitor
    /// across all four Brain Intent capability keys, and persist the results to ADX.
    /// Monitors are evaluated regardless of CUJO mapping; missing signals influence the
    /// resulting classification (e.g. <see cref="BrainIntentStatus.ShouldBeEnabled"/>
    /// vs <see cref="BrainIntentStatus.NotClassified"/>) but do not exclude the monitor.
    /// </summary>
    /// <param name="serviceOid">
    ///   Service OID used to filter
    ///   <c>GetIntegratedMonitorOutageCoverageDrillThrough</c> and to label result rows.
    /// </param>
    /// <param name="serviceName">Human-readable service name (may be empty).</param>
    /// <param name="evaluationTimestamp">
    ///   Stable UTC timestamp for the whole evaluation run (idempotency anchor).
    ///   Defaults to <see cref="DateTime.UtcNow"/> when not provided.
    /// </param>
    /// <param name="batchSize">Number of rows per ADX ingestion call.</param>
    /// <param name="maxParallelism">Maximum concurrent evaluations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///   All successfully evaluated rows (useful for callers that need the results in-process).
    ///   Per-monitor failures are logged and skipped; the overall call does not fail because
    ///   of individual monitor errors.
    /// </returns>
    public async Task<IReadOnlyList<BrainIntentEvaluationRow>> EvaluateAndPersistAsync(
        string serviceOid,
        string serviceName,
        DateTime? evaluationTimestamp = null,
        int batchSize = DefaultBatchSize,
        int maxParallelism = DefaultMaxParallelism,
        CancellationToken cancellationToken = default)
    {
        var timestamp = evaluationTimestamp ?? DateTime.UtcNow;

        // 1. Fetch ALL monitors for this service from GetIntegratedMonitorOutageCoverageDrillThrough.
        //    No CUJO filter is applied; every monitor associated with the ServiceOid is included.
        IReadOnlyList<MonitorEvaluationInput> fetched;
        try
        {
            fetched = await _shericaFetcher.FetchMonitorsForServiceAsync(serviceOid, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to fetch monitors from sherica-prod for service '{ServiceOid}'.", serviceOid);
            throw new InvalidOperationException(
                $"Failed to fetch monitors from sherica-prod for service '{serviceOid}': {ex.Message}", ex);
        }

        // 2. Deduplicate by MonitorId early to avoid redundant evaluations.
        var monitors = fetched
            .GroupBy(m => m.MonitorId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        _logger.LogInformation(
            "Brain Intent evaluation starting for service '{ServiceOid}': " +
            "{TotalFetched} monitors fetched, {Unique} unique.",
            serviceOid, fetched.Count, monitors.Count);

        if (monitors.Count == 0)
            return [];

        // 3. Evaluate each monitor with bounded concurrency and per-monitor fault isolation.
        var results = new BrainIntentEvaluationRow?[monitors.Count];
        var failureCount = 0;

        using var semaphore = new SemaphoreSlim(maxParallelism, maxParallelism);

        var tasks = monitors.Select((monitor, index) => Task.Run(async () =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                results[index] = Evaluate(serviceOid, serviceName, monitor, timestamp);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failureCount);
                _logger.LogWarning(ex,
                    "Brain Intent evaluation failed for monitor '{MonitorId}' (service '{ServiceOid}').",
                    monitor.MonitorId, serviceOid);
            }
            finally
            {
                semaphore.Release();
            }
        }, cancellationToken));

        await Task.WhenAll(tasks);

        var evaluated = results.Where(r => r is not null).Select(r => r!).ToList();

        _logger.LogInformation(
            "Brain Intent evaluation complete for service '{ServiceOid}': " +
            "{Evaluated} evaluated, {Failed} failed.",
            serviceOid, evaluated.Count, failureCount);

        // 4. Persist in batches to avoid per-row ingestion overhead.
        for (int i = 0; i < evaluated.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = evaluated.Skip(i).Take(batchSize).ToList().AsReadOnly();
            await _writer.IngestBatchAsync(batch, cancellationToken);
        }

        _logger.LogInformation(
            "Brain Intent persistence complete for service '{ServiceOid}': {Persisted} rows persisted.",
            serviceOid, evaluated.Count);

        return evaluated.AsReadOnly();
    }

    /// <summary>
    /// Normalize a single monitor evaluation result into an ADX row.
    /// Exposed internally so unit tests can verify the mapping logic.
    /// </summary>
    internal static BrainIntentEvaluationRow Evaluate(
        string serviceId,
        string serviceName,
        MonitorEvaluationInput monitor,
        DateTime evaluationTimestamp)
    {
        var req = new MonitorBrainIntegrationRequest(
            LinkedCujoJourney:               monitor.LinkedCujoJourney,
            isBrainAOD:                      monitor.isBrainAOD,
            DetectedImpactType:              monitor.DetectedImpactType,
            isLIDCompliant:                  monitor.isLIDCompliant,
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
            IsSLI:                      false,
            BrainAwareness:             result.BrainIntent.BrainAwareness,
            OutageDeclaration:          result.BrainIntent.OutageDeclaration,
            DeploymentStops:            result.BrainIntent.DeploymentStops,
            AutoComms:                  result.BrainIntent.AutoComms,
            EvaluationSource:           EvaluationSource,
            EvaluationTimestamp:        evaluationTimestamp,
            CujoJourney:                monitor.LinkedCujoJourney,
            LinkedICMIncidentId:        monitor.AllIncidents,
            LIDCompliant:               monitor.isLIDCompliant,
            RegionalScopeDetectable:    monitor.RegionalScopeDetectable,
            SubscriptionScopeDetectable: monitor.SubscriptionScopeDetectable,
            HistoricalPrecision:        monitor.HistoricalPrecision,
            SignalStability:            monitor.SignalStability,
            CommunicationRelevant:      monitor.CommunicationRelevantImpact);
    }
}
