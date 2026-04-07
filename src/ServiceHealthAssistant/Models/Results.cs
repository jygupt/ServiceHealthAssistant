namespace ServiceHealthAssistant.Models;

// ---------------------------------------------------------------------------
// Result models (outputs from rule functions / tools)
// ---------------------------------------------------------------------------

/// <summary>Result of signal classification.</summary>
public sealed record SignalClassificationResult(
    SignalType SignalType,
    BrainIntentCategory BrainIntent,
    string Rationale,
    IReadOnlyList<string> Recommendations);

/// <summary>Result of a LID (Latency, Impact, Dependency) compliance check.</summary>
public sealed record LidComplianceResult(
    string SignalId,
    SignalType SignalType,
    ComplianceStatus Status,
    bool LatencyPresent,
    bool ImpactPresent,
    IReadOnlyList<string> DependencyDimensions,
    IReadOnlyList<string> MissingDimensions,
    double Score,
    IReadOnlyList<string> Recommendations);

/// <summary>Result of Brain Intent classification or validation.</summary>
public sealed record BrainIntentResult(
    string SignalId,
    SignalType SignalType,
    BrainIntentCategory ClassifiedIntent,
    double Confidence,
    bool IsCorrect,
    string Rationale,
    IReadOnlyList<string> Recommendations);

/// <summary>Quality assessment for an SLI across measurability, sensitivity, relevance.</summary>
public sealed record SliQualityScore(
    string SliId,
    double OverallScore,
    IReadOnlyDictionary<string, double> DimensionScores,
    string NoiseLevel,
    string CoverageEstimate,
    string PrecisionEstimate,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> Recommendations,
    bool PublishSafe);

/// <summary>Result of pre-publish validation.</summary>
public sealed record PreFlightValidationResult(
    string SignalId,
    bool Passed,
    IReadOnlyList<string> BlockingIssues,
    IReadOnlyList<string> Warnings,
    LidComplianceResult? LidResult,
    BrainIntentResult? BrainIntentResult,
    SliQualityScore? QualityScore,
    IReadOnlyList<string> RecommendedFixes);

/// <summary>Evaluation of a signal's readiness for Brain/AOD automation.</summary>
public sealed record AutomationReadinessResult(
    string SignalId,
    SignalType SignalType,
    AutomationReadinessLevel ReadinessLevel,
    bool BrainIntegrationValid,
    bool AodEligible,
    bool AutoCommsReady,
    bool DeploymentStopReady,
    IReadOnlyList<string> BlockingCriteria,
    IReadOnlyList<string> RemediationSteps,
    string Rationale);

/// <summary>Service health summary.</summary>
public sealed record ServiceHealthSummary(
    string ServiceName,
    int TotalSlis,
    int TotalMonitors,
    int LidCompliantCount,
    int BrainAwareCount,
    int AodEnabledCount,
    int OpenRepairItems,
    int OpenS360Actions,
    int CoverageGaps,
    int AutomationReadySignals,
    double OverallHealthScore,
    IReadOnlyList<string> TopPriorities,
    IReadOnlyList<string> CujosWithoutSli);
