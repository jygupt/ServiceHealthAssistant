namespace ServiceHealthAssistant.Models;

// ---------------------------------------------------------------------------
// Core domain models
// ---------------------------------------------------------------------------

/// <summary>A single metric dimension.</summary>
public sealed record MetricDimension(
    string Name,
    IReadOnlyList<string>? Values = null,
    bool Required = false,
    bool PresentInMdm = false);

/// <summary>Represents a Service Level Indicator.</summary>
public sealed record Sli(
    string Id,
    string Name,
    string ServiceName,
    string Description = "",
    string MetricNamespace = "",
    string MetricName = "",
    string KqlQuery = "",
    IReadOnlyList<MetricDimension>? Dimensions = null,
    double? Threshold = null,
    int WindowMinutes = 60,
    BrainIntentCategory BrainIntent = BrainIntentCategory.Unclassified,
    bool BrainAware = false,
    bool AodEnabled = false,
    bool LidCompliant = false,
    IReadOnlyList<string>? CujoIds = null,
    IReadOnlyDictionary<string, string>? Tags = null,
    string Owner = "",
    IReadOnlyDictionary<string, double>? QualityScores = null);

/// <summary>Represents a Service Monitor (lower-fidelity than SLI).</summary>
public sealed record ServiceMonitor(
    string Id,
    string Name,
    string ServiceName,
    string Description = "",
    string MetricNamespace = "",
    string MetricName = "",
    string KqlQuery = "",
    IReadOnlyList<MetricDimension>? Dimensions = null,
    double? Threshold = null,
    int WindowMinutes = 5,
    BrainIntentCategory BrainIntent = BrainIntentCategory.Unclassified,
    bool BrainAware = false,
    bool SliPromotionEligible = false,
    string Owner = "",
    IReadOnlyDictionary<string, string>? Tags = null);

/// <summary>A detected coverage gap tied to a CUJO or service area.</summary>
public sealed record CoverageGap(
    string Id,
    string ServiceName,
    GapType GapType,
    string Description,
    string? CujoId = null,
    IReadOnlyList<string>? AffectedSignals = null,
    RepairPriority Severity = RepairPriority.Medium,
    string Owner = "",
    IReadOnlyList<string>? RecommendedActions = null);

/// <summary>An actionable repair item derived from a gap or compliance violation.</summary>
public sealed record RepairItem(
    string Id,
    string Title,
    string Description,
    string? GapId = null,
    string? SignalId = null,
    string ServiceName = "",
    RepairPriority Priority = RepairPriority.Medium,
    RepairStatus Status = RepairStatus.Open,
    S360KpiCategory? S360KpiCategory = null,
    string WhyRequired = "",
    string OutcomeUnblocked = "",
    string Owner = "",
    string? DueDate = null,
    IReadOnlyList<string>? Steps = null);

/// <summary>An S360 KPI action mapped from one or more repair items.</summary>
public sealed record S360KpiAction(
    string Id,
    string Title,
    S360KpiCategory Category,
    string Description,
    IReadOnlyList<string>? RepairItemIds = null,
    string ServiceName = "",
    RepairStatus Status = RepairStatus.Open,
    string WhyRequired = "",
    string OutcomeUnblocked = "",
    string Owner = "",
    string? DueDate = null);
