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
