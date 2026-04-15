namespace ServiceHealthAssistant.Models;

// ---------------------------------------------------------------------------
// ADX row model for MCP_BrainIntentEvaluation table
// ---------------------------------------------------------------------------

/// <summary>
/// One row per monitor evaluation to be persisted into
/// SHMDatabase.MCP_BrainIntentEvaluation on
/// https://shm-dev-uksouth-kusto.uksouth.kusto.windows.net
/// </summary>
public sealed record BrainIntentEvaluationRow(
    string ServiceId,
    string ServiceName,
    string MonitorId,
    string MonitorName,
    string MonitorType,
    bool IsSLI,
    BrainIntentStatus BrainAwareness,
    BrainIntentStatus OutageDeclaration,
    BrainIntentStatus DeploymentStops,
    BrainIntentStatus AutoComms,
    string EvaluationSource,
    DateTime EvaluationTimestamp,
    string? CujoJourney = null,
    string? LinkedICMIncidentId = null,
    bool? LocationIdPresent = null,
    bool? RegionalScopeDetectable = null,
    bool? SubscriptionScopeDetectable = null,
    HistoricalPrecision? HistoricalPrecision = null,
    SignalStability? SignalStability = null,
    bool? CommunicationRelevant = null);
