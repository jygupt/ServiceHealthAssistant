namespace ServiceHealthAssistant.Models;

// ---------------------------------------------------------------------------
// Request models (inputs to tools / rule functions)
// ---------------------------------------------------------------------------

/// <summary>Input for the ClassifySignal tool.</summary>
public sealed record SignalClassificationRequest(
    string ServiceName,
    string Description,
    string? CujoId = null,
    string TelemetryType = "",
    bool HasCustomerFacingImpact = false,
    bool HasLatencyMetric = false,
    bool HasAvailabilityMetric = false,
    bool HasErrorRateMetric = false,
    bool IsInfrastructureSignal = false,
    int ExistingSliCount = 0,
    bool BrainOutageModeRequired = false);

/// <summary>Input for evaluating a Geneva Service Monitor's Brain integration capabilities.</summary>
public sealed record MonitorBrainIntegrationRequest(
    string MonitorName,
    string? MonitorType = null,
    string? LinkedCujoJourney = null,
    bool OutageDrivingIcmMapping = false,
    DetectedImpactType DetectedImpactType = DetectedImpactType.Operational,
    bool LocationIdPresent = false,
    bool RegionalScopeDetectable = false,
    bool SubscriptionScopeDetectable = false,
    HistoricalPrecision HistoricalPrecision = HistoricalPrecision.Low,
    SignalStability SignalStability = SignalStability.Unknown,
    bool UsedInOutageDeclarationPreviously = false,
    bool CommunicationRelevantImpact = false);

/// <summary>Input for pre-publish validation.</summary>
public sealed record PreFlightValidationRequest(
    string SignalId,
    SignalType SignalType,
    string MetricNamespace,
    string MetricName,
    string KqlQuery = "",
    IReadOnlyList<MetricDimension>? Dimensions = null,
    BrainIntentCategory BrainIntent = BrainIntentCategory.Unclassified,
    string Owner = "");
