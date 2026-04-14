using System.Text.RegularExpressions;
using ServiceHealthAssistant.Models;

namespace ServiceHealthAssistant.Rules;

/// <summary>
/// Deterministic rule engine for Service Health governance.
/// All rules are deterministic — no speculative reasoning. Each rule returns a
/// structured result that can be audited and traced back to a specific policy.
/// </summary>
public static class ServiceHealthRules
{
    // -----------------------------------------------------------------------
    // LID (Location ID) dimension patterns
    // -----------------------------------------------------------------------

    // Detects dimension names that represent geographic Location ID fields (ARM region fields).
    // These are the fields that should emit ARM region names such as 'eastus' or 'westeurope'.
    private static readonly Regex LocationDimensionNamePattern = new(
        @"\b(location|locationid|region|regionname|armregion|geo)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Detects DCMT region codes (e.g. DM-USEA-1, DC-USWA-3), cluster names, node names,
    // pod names, and other non-ARM resource identifiers that must NOT be used as Location ID values.
    private static readonly Regex InvalidLocationPattern = new(
        @"\b(cluster(name|id)?|node(name|id)?|pod(name|id)?|host(name|id)?|rack(id)?|shard|partition)\b|\bDM-[A-Z]{2,6}\b|\bDC-[A-Z]{2,6}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Minimum quality score to be considered publish-safe
    private const double MinPublishScore = 0.70;

    // -----------------------------------------------------------------------
    // Signal classification
    // -----------------------------------------------------------------------

    /// <summary>
    /// Deterministically decide whether a signal should be an SLI or a Service Monitor,
    /// and classify its Brain Intent.
    /// Rules:
    /// - SLI if: customer-facing impact OR has availability/latency/error-rate metrics.
    /// - Service Monitor if: infrastructure-only signal with no direct CUJO link.
    /// - Brain Intent = CustomerImpact if HasCustomerFacingImpact or has_availability/error_rate.
    /// - Brain Intent = OperationalInfrastructure if IsInfrastructureSignal.
    /// </summary>
    public static SignalClassificationResult ClassifySignal(SignalClassificationRequest req)
    {
        var rationale = new List<string>();
        var recommendations = new List<string>();

        bool isCustomerSignal =
            req.HasCustomerFacingImpact ||
            req.HasAvailabilityMetric ||
            req.HasErrorRateMetric ||
            req.HasLatencyMetric;

        SignalType signalType;
        if (isCustomerSignal && req.CujoId != null)
        {
            signalType = SignalType.SLI;
            rationale.Add("Signal has customer-facing metrics and is linked to a CUJO — use SLI.");
        }
        else if (isCustomerSignal)
        {
            signalType = SignalType.SLI;
            rationale.Add("Signal has customer-facing metrics but no CUJO. SLI recommended; link a CUJO before publishing.");
            recommendations.Add("Provide a CUJO ID to satisfy LID compliance and coverage requirements.");
        }
        else if (req.IsInfrastructureSignal)
        {
            signalType = SignalType.ServiceMonitor;
            rationale.Add("Infrastructure-only signal with no direct customer impact — use Service Monitor.");
        }
        else
        {
            signalType = SignalType.SLI;
            rationale.Add("Signal type is ambiguous. Defaulting to SLI for governance completeness. Review with service owner.");
            recommendations.Add("Clarify whether signal directly measures customer experience or is infrastructure-only.");
        }

        // Brain Intent classification — latency alone is insufficient; API latency
        // may be customer-facing, so only infrastructure-flagged signals are classified
        // as OperationalInfrastructure without an explicit customer-impact flag.
        BrainIntentCategory brainIntent;
        if (req.HasCustomerFacingImpact || req.HasAvailabilityMetric || req.HasErrorRateMetric)
        {
            brainIntent = BrainIntentCategory.CustomerImpact;
            rationale.Add("Customer-impact signals map to Brain Intent: CustomerImpact.");
        }
        else if (req.IsInfrastructureSignal)
        {
            brainIntent = BrainIntentCategory.OperationalInfrastructure;
            rationale.Add("Infrastructure-only signal maps to Brain Intent: OperationalInfrastructure.");
        }
        else
        {
            brainIntent = BrainIntentCategory.Unclassified;
            rationale.Add("Brain Intent could not be determined from provided metadata.");
            recommendations.Add("Manually classify Brain Intent as CustomerImpact or OperationalInfrastructure before publishing.");
        }

        if (req.BrainOutageModeRequired && brainIntent != BrainIntentCategory.CustomerImpact)
            recommendations.Add("Outage-mode is required but Brain Intent is not CustomerImpact. Re-evaluate signal scope.");

        if (signalType == SignalType.ServiceMonitor)
            recommendations.Add("Evaluate this Service Monitor for SLI promotion eligibility once metric quality is confirmed.");

        return new SignalClassificationResult(
            signalType,
            brainIntent,
            string.Join(" ", rationale),
            recommendations.AsReadOnly());
    }

    // -----------------------------------------------------------------------
    // LID compliance
    // -----------------------------------------------------------------------

    /// <summary>
    /// Enforce LID (Location ID) compliance.
    /// LID Compliance measures whether a signal reliably emits accurate, constructable,
    /// standardized Location IDs (ARM region names) across telemetry, enabling correct
    /// regional attribution and trustworthy rollups. It is a safety prerequisite for
    /// Brain/AOD automation, routing, correlation, and policy enforcement.
    ///
    /// A signal is compliant when:
    ///   1. It exposes at least one Location ID dimension (e.g. Region, LocationId, ArmRegion).
    ///   2. That dimension represents a valid ARM region name — NOT a DCMT region code
    ///      (e.g. DM-USEA-1), cluster ID, node ID, or other resource string.
    ///
    /// Score: 0.5 per satisfied check → 0.0 (NonCompliant) / 0.5 (Partial) / 1.0 (Compliant).
    /// </summary>
    public static LidComplianceResult EvaluateLidCompliance(
        string signalId,
        SignalType signalType,
        IReadOnlyList<MetricDimension>? dimensions,
        string kqlQuery = "")
    {
        var dims = dimensions ?? [];
        var dimNames = dims.Select(d => d.Name).ToList();

        // 1. Location ID presence: any dimension name or KQL column reference is a location field.
        var locationDims = dimNames
            .Where(n => LocationDimensionNamePattern.IsMatch(n))
            .ToList();
        bool locationIdPresent = locationDims.Count > 0 || LocationDimensionNamePattern.IsMatch(kqlQuery);

        // 2. ARM region validity: when a location field is present, verify it is not backed by
        //    DCMT region codes, cluster names, node names, or other non-ARM resource identifiers.
        bool locationIdValidArmRegion = false;
        if (locationIdPresent)
        {
            bool invalidInDimNames = locationDims.Any(n => InvalidLocationPattern.IsMatch(n));
            bool invalidInKql = InvalidLocationPattern.IsMatch(kqlQuery);
            locationIdValidArmRegion = !invalidInDimNames && !invalidInKql;
        }

        var missing = new List<string>();
        if (!locationIdPresent)
            missing.Add("Location ID dimension (e.g. Region, LocationId, ArmRegion)");
        else if (!locationIdValidArmRegion)
            missing.Add("Valid ARM region name — avoid DCMT region codes (e.g. DM-USEA-1) and cluster/node identifiers");

        double score = Math.Round(
            (locationIdPresent ? 0.5 : 0.0) +
            (locationIdValidArmRegion ? 0.5 : 0.0), 2);

        ComplianceStatus status =
            score == 1.0 ? ComplianceStatus.Compliant :
            score > 0.0  ? ComplianceStatus.Partial :
                           ComplianceStatus.NonCompliant;

        var recommendations = new List<string>();
        foreach (var m in missing)
            recommendations.Add($"Add required LID element: {m}");
        if (status != ComplianceStatus.Compliant)
            recommendations.Add("LID compliance is required for Brain/AOD automation, routing, correlation, and policy enforcement.");

        return new LidComplianceResult(
            signalId, signalType, status,
            locationIdPresent, locationIdValidArmRegion,
            locationDims.AsReadOnly(), missing.AsReadOnly(),
            score, recommendations.AsReadOnly());
    }

    // -----------------------------------------------------------------------
    // Brain Intent validation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Validate that the declared Brain Intent is correct given signal metadata.
    /// Returns confidence and correctness assessment.
    /// </summary>
    public static BrainIntentResult ValidateBrainIntent(
        string signalId,
        SignalType signalType,
        BrainIntentCategory declaredIntent,
        bool hasCustomerFacingImpact = false,
        bool isInfrastructureSignal = false,
        string description = "")
    {
        BrainIntentCategory expectedIntent;
        if (hasCustomerFacingImpact)
        {
            expectedIntent = BrainIntentCategory.CustomerImpact;
        }
        else if (isInfrastructureSignal)
        {
            expectedIntent = BrainIntentCategory.OperationalInfrastructure;
        }
        else
        {
            // Heuristic from description text
            string desc = description.ToLowerInvariant();
            string[] customerWords = ["customer", "user", "availability", "latency", "error", "failure"];
            string[] infraWords    = ["cpu", "memory", "disk", "host", "pod", "node", "infra", "infrastructure"];

            if (customerWords.Any(w => desc.Contains(w)))
                expectedIntent = BrainIntentCategory.CustomerImpact;
            else if (infraWords.Any(w => desc.Contains(w)))
                expectedIntent = BrainIntentCategory.OperationalInfrastructure;
            else
                expectedIntent = BrainIntentCategory.Unclassified;
        }

        bool isCorrect = declaredIntent == expectedIntent && declaredIntent != BrainIntentCategory.Unclassified;
        double confidence = isCorrect ? 1.0
            : expectedIntent == BrainIntentCategory.Unclassified ? 0.5
            : 0.0;

        var recommendations = new List<string>();
        if (declaredIntent == BrainIntentCategory.Unclassified)
            recommendations.Add("Brain Intent is Unclassified. Classify as CustomerImpact or OperationalInfrastructure before publishing.");
        if (!isCorrect && expectedIntent != BrainIntentCategory.Unclassified)
            recommendations.Add($"Declared intent '{declaredIntent}' does not match expected '{expectedIntent}' based on signal metadata. Correct it to align with Brain outage-mode principles.");
        if (declaredIntent == BrainIntentCategory.CustomerImpact)
            recommendations.Add("Ensure Brain outage-mode is enabled for CustomerImpact signals to support AOD and auto-comms.");

        string rationale = $"Expected intent: {expectedIntent}. Declared intent: {declaredIntent}. Correct: {isCorrect}.";

        return new BrainIntentResult(
            signalId, signalType, expectedIntent, confidence, isCorrect, rationale,
            recommendations.AsReadOnly());
    }

    // -----------------------------------------------------------------------
    // SLI quality scoring
    // -----------------------------------------------------------------------

    /// <summary>
    /// Score SLI quality across Measurability, Sensitivity, and Relevance.
    /// Measurability: metric namespace/name present, KQL non-empty, MDM-available dimensions.
    /// Sensitivity:   threshold set, reasonable window, low noise estimate.
    /// Relevance:     LID compliant, Brain Intent correct.
    /// </summary>
    public static SliQualityScore ScoreSliQuality(
        string sliId,
        string metricNamespace,
        string metricName,
        string kqlQuery,
        IReadOnlyList<MetricDimension>? dimensions,
        double? threshold,
        int windowMinutes,
        LidComplianceResult lidResult,
        BrainIntentResult brainIntentResult)
    {
        var dims = dimensions ?? [];
        var blockingIssues = new List<string>();
        var recommendations = new List<string>();

        // Measurability (max 1.0)
        double measurability = 0.0;
        if (!string.IsNullOrEmpty(metricNamespace)) measurability += 0.33;
        else blockingIssues.Add("Metric namespace is missing.");
        if (!string.IsNullOrEmpty(metricName)) measurability += 0.33;
        else blockingIssues.Add("Metric name is missing.");
        if (!string.IsNullOrEmpty(kqlQuery)) measurability += 0.34;
        else recommendations.Add("KQL query is empty; provide a validated query.");

        if (dims.Count > 0 && !dims.Any(d => d.PresentInMdm))
            recommendations.Add("No dimensions are confirmed present in MDM. Validate dimensional readiness.");

        // Sensitivity (baseline 0.5)
        double sensitivity = 0.5;
        if (threshold is null)
        {
            sensitivity -= 0.25;
            recommendations.Add("No threshold defined. Set an appropriate SLO threshold.");
        }
        if (windowMinutes < 1 || windowMinutes > 1440)
        {
            sensitivity -= 0.25;
            recommendations.Add("Evaluation window is outside recommended range (1–1440 minutes).");
        }
        string noiseLevel = sensitivity >= 0.5 ? "LOW" : "MEDIUM";

        // Relevance
        double relevance = lidResult.Score * 0.6;
        if (brainIntentResult.IsCorrect)
            relevance += 0.4;
        else
            recommendations.Add("Brain Intent is incorrect or unclassified, reducing relevance score.");

        double overall = Math.Round((measurability + sensitivity + relevance) / 3.0, 2);

        string coverageEstimate = overall >= 0.8 ? "HIGH" : overall >= 0.5 ? "MEDIUM" : "LOW";
        string precisionEstimate = threshold is not null && lidResult.Score >= 0.67 ? "HIGH" : "LOW";

        bool publishSafe = overall >= MinPublishScore && blockingIssues.Count == 0;

        return new SliQualityScore(
            sliId, overall,
            new Dictionary<string, double>
            {
                ["measurability"] = Math.Round(measurability, 2),
                ["sensitivity"]   = Math.Round(sensitivity, 2),
                ["relevance"]     = Math.Round(relevance, 2)
            },
            noiseLevel, coverageEstimate, precisionEstimate,
            blockingIssues.AsReadOnly(), recommendations.AsReadOnly(), publishSafe);
    }

    // -----------------------------------------------------------------------
    // Pre-flight validation
    // -----------------------------------------------------------------------

    /// <summary>Run all pre-publish validation checks for a signal.</summary>
    public static PreFlightValidationResult RunPreFlightValidation(PreFlightValidationRequest req)
    {
        var blockingIssues = new List<string>();
        var warnings       = new List<string>();
        var recommendedFixes = new List<string>();

        var lidResult = EvaluateLidCompliance(req.SignalId, req.SignalType, req.Dimensions, req.KqlQuery);

        var brainResult = ValidateBrainIntent(
            req.SignalId, req.SignalType, req.BrainIntent, description: req.KqlQuery);

        SliQualityScore? qualityScore = null;
        if (req.SignalType == SignalType.SLI)
        {
            qualityScore = ScoreSliQuality(
                req.SignalId, req.MetricNamespace, req.MetricName,
                req.KqlQuery, req.Dimensions, threshold: null, windowMinutes: 60,
                lidResult, brainResult);
            blockingIssues.AddRange(qualityScore.BlockingIssues);
            recommendedFixes.AddRange(qualityScore.Recommendations);
        }

        if (lidResult.Status == ComplianceStatus.NonCompliant)
        {
            blockingIssues.Add($"LID compliance failure: missing {string.Join(", ", lidResult.MissingDimensions)}.");
            recommendedFixes.AddRange(lidResult.Recommendations);
        }
        else if (lidResult.Status == ComplianceStatus.Partial)
        {
            warnings.Add($"Partial LID compliance (score={lidResult.Score:F2}): missing {string.Join(", ", lidResult.MissingDimensions)}.");
        }

        if (!brainResult.IsCorrect)
        {
            warnings.Add($"Brain Intent issue: {brainResult.Rationale}");
            recommendedFixes.AddRange(brainResult.Recommendations);
        }

        if (req.BrainIntent == BrainIntentCategory.Unclassified)
            blockingIssues.Add("Brain Intent is UNCLASSIFIED. Classify before publishing.");

        if (string.IsNullOrEmpty(req.Owner))
            warnings.Add("No owner specified. Assign an owner for auditability.");

        // Deduplicate recommended fixes
        var dedupedFixes = recommendedFixes.Distinct().ToList();

        return new PreFlightValidationResult(
            req.SignalId,
            Passed: blockingIssues.Count == 0,
            blockingIssues.AsReadOnly(), warnings.AsReadOnly(),
            lidResult, brainResult, qualityScore,
            dedupedFixes.AsReadOnly());
    }

    // -----------------------------------------------------------------------
    // Coverage gap detection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Identify CUJOs without SLI coverage, low-quality signals, and
    /// automation readiness gaps.
    /// </summary>
    public static IReadOnlyList<CoverageGap> DetectCoverageGaps(
        string serviceName,
        IReadOnlyList<string> cujoIds,
        IReadOnlyList<Sli> slis,
        IReadOnlyList<ServiceMonitor> monitors)
    {
        var gaps = new List<CoverageGap>();
        var sliCujos = slis.SelectMany(s => s.CujoIds ?? []).ToHashSet();

        // Detection gaps — CUJOs with no SLI
        foreach (var cujo in cujoIds)
        {
            if (!sliCujos.Contains(cujo))
            {
                gaps.Add(new CoverageGap(
                    Id: $"gap-detect-{cujo}",
                    ServiceName: serviceName,
                    GapType: GapType.DetectionGap,
                    Description: $"CUJO '{cujo}' has no SLI coverage.",
                    CujoId: cujo,
                    Severity: RepairPriority.High,
                    RecommendedActions: new[]
                    {
                        $"Author an SLI for CUJO '{cujo}'.",
                        "Validate metric availability in MDM before authoring."
                    }));
            }
        }

        // Signal quality gaps — SLIs with poor quality score
        foreach (var sli in slis)
        {
            double? qScore = sli.QualityScores?.TryGetValue("overall", out var v) == true ? v : null;
            if (qScore is not null && qScore < MinPublishScore)
            {
                gaps.Add(new CoverageGap(
                    Id: $"gap-quality-{sli.Id}",
                    ServiceName: serviceName,
                    GapType: GapType.SignalQualityGap,
                    Description: $"SLI '{sli.Name}' has quality score {qScore:F2} (below threshold {MinPublishScore}).",
                    CujoId: sli.CujoIds?.Count > 0 ? sli.CujoIds[0] : null,
                    AffectedSignals: new[] { sli.Id },
                    Severity: qScore < 0.5 ? RepairPriority.High : RepairPriority.Medium,
                    RecommendedActions: new[]
                    {
                        "Improve LID compliance.",
                        "Validate and correct Brain Intent.",
                        "Set a measurable threshold."
                    }));
            }
        }

        // Automation readiness gaps — SLIs without Brain awareness
        foreach (var sli in slis)
        {
            if (!sli.BrainAware)
            {
                gaps.Add(new CoverageGap(
                    Id: $"gap-auto-{sli.Id}",
                    ServiceName: serviceName,
                    GapType: GapType.AutomationReadinessGap,
                    Description: $"SLI '{sli.Name}' is not Brain-aware, blocking AOD and auto-comms.",
                    CujoId: sli.CujoIds?.Count > 0 ? sli.CujoIds[0] : null,
                    AffectedSignals: new[] { sli.Id },
                    Severity: RepairPriority.Medium,
                    RecommendedActions: new[]
                    {
                        "Enable Brain integration for this SLI.",
                        "Validate Brain Intent is CustomerImpact before enabling AOD."
                    }));
            }
        }

        // Monitors eligible for SLI promotion
        foreach (var monitor in monitors)
        {
            if (monitor.SliPromotionEligible)
            {
                gaps.Add(new CoverageGap(
                    Id: $"gap-promote-{monitor.Id}",
                    ServiceName: serviceName,
                    GapType: GapType.SignalQualityGap,
                    Description: $"Service Monitor '{monitor.Name}' is eligible for SLI promotion but has not been promoted.",
                    AffectedSignals: new[] { monitor.Id },
                    Severity: RepairPriority.Low,
                    RecommendedActions: new[]
                    {
                        $"Promote Service Monitor '{monitor.Name}' to an SLI.",
                        "Validate LID compliance and Brain Intent after promotion."
                    }));
            }
        }

        return gaps.AsReadOnly();
    }

    // -----------------------------------------------------------------------
    // Repair item generation
    // -----------------------------------------------------------------------

    /// <summary>Translate coverage gaps and compliance violations into repair items.</summary>
    public static IReadOnlyList<RepairItem> GenerateRepairItems(IReadOnlyList<CoverageGap> gaps)
    {
        var repairs = new List<RepairItem>();
        foreach (var gap in gaps)
        {
            S360KpiCategory kpiCategory = gap.GapType switch
            {
                GapType.DetectionGap          => S360KpiCategory.Coverage,
                GapType.SignalQualityGap       => S360KpiCategory.Quality,
                GapType.AutomationReadinessGap => S360KpiCategory.Automation,
                _                              => S360KpiCategory.Coverage
            };

            string title = $"[{gap.GapType}] " +
                (gap.Description.Length > 80 ? gap.Description[..80] : gap.Description);

            repairs.Add(new RepairItem(
                Id: $"repair-{gap.Id}",
                Title: title,
                Description: gap.Description,
                GapId: gap.Id,
                ServiceName: gap.ServiceName,
                Priority: gap.Severity,
                Status: RepairStatus.Open,
                S360KpiCategory: kpiCategory,
                WhyRequired: "This gap directly impacts S360 KPI health and may block automation enablement (AOD, auto-comms, deployment stops).",
                OutcomeUnblocked: "Resolving this repair improves reliability coverage, signal quality, and governance compliance.",
                Owner: gap.Owner,
                Steps: gap.RecommendedActions));
        }
        return repairs.AsReadOnly();
    }

    // -----------------------------------------------------------------------
    // S360 KPI action generation
    // -----------------------------------------------------------------------

    /// <summary>Group repair items into S360 KPI actions by category.</summary>
    public static IReadOnlyList<S360KpiAction> GenerateS360KpiActions(IReadOnlyList<RepairItem> repairItems)
    {
        var categoryMap = repairItems
            .Where(r => r.S360KpiCategory.HasValue)
            .GroupBy(r => r.S360KpiCategory!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var actions = new List<S360KpiAction>();
        foreach (var (category, items) in categoryMap)
        {
            var services = items.Select(i => i.ServiceName)
                               .Where(s => !string.IsNullOrEmpty(s))
                               .Distinct()
                               .ToList();
            string serviceName = services.Count == 1 ? services[0] : "multiple-services";

            actions.Add(new S360KpiAction(
                Id: $"s360-{category.ToString().ToLowerInvariant()}-{serviceName}",
                Title: $"S360 KPI: {category} improvement for {serviceName}",
                Category: category,
                Description: $"Address {items.Count} repair item(s) in the {category} category to improve S360 KPI health.",
                RepairItemIds: items.Select(i => i.Id).ToList().AsReadOnly(),
                ServiceName: serviceName,
                Status: RepairStatus.Open,
                WhyRequired: $"Unresolved {category} gaps directly lower the S360 KPI score and may block governance approvals.",
                OutcomeUnblocked: "Closing this KPI action will improve the service's reliability posture and unblock automation readiness milestones."));
        }
        return actions.AsReadOnly();
    }

    // -----------------------------------------------------------------------
    // Automation readiness evaluation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluate a signal's readiness for Brain/AOD automation.
    /// Safety gates (all must pass for READY):
    ///   1. Brain-aware
    ///   2. Brain Intent = CustomerImpact (for AOD)
    ///   3. LID fully compliant (valid Location ID emitting ARM region names)
    ///   4. Quality score >= 0.70
    /// </summary>
    public static AutomationReadinessResult EvaluateAutomationReadiness(
        string signalId,
        SignalType signalType,
        bool brainAware,
        BrainIntentCategory brainIntent,
        ComplianceStatus lidStatus,
        double qualityScore,
        bool aodEnabled = false)
    {
        var blocking   = new List<string>();
        var remediation = new List<string>();

        if (!brainAware)
        {
            blocking.Add("Signal is not Brain-aware.");
            remediation.Add("Enable Brain integration for this signal.");
        }

        if (brainIntent != BrainIntentCategory.CustomerImpact)
        {
            blocking.Add($"Brain Intent is '{brainIntent}', not CustomerImpact. AOD requires CustomerImpact.");
            remediation.Add("Set Brain Intent to CustomerImpact and validate correctness.");
        }

        if (lidStatus != ComplianceStatus.Compliant)
        {
            blocking.Add($"LID compliance status is '{lidStatus}', not Compliant.");
            remediation.Add("Achieve full LID compliance: add a valid Location ID dimension (e.g. Region, LocationId) emitting ARM region names such as 'eastus' or 'westeurope'.");
        }

        if (qualityScore < MinPublishScore)
        {
            blocking.Add($"Quality score {qualityScore:F2} is below the minimum {MinPublishScore}.");
            remediation.Add("Improve SLI quality score to at least 0.70 before enabling AOD.");
        }

        bool aodEligible      = blocking.Count == 0;
        bool autoCommsReady   = aodEligible;
        bool deploymentStop   = aodEligible && aodEnabled;

        // Brain awareness is a fundamental prerequisite — always BLOCKED without it,
        // regardless of how many other criteria are satisfied.
        AutomationReadinessLevel readinessLevel =
            blocking.Count == 0   ? AutomationReadinessLevel.Ready :
            !brainAware           ? AutomationReadinessLevel.Blocked :
            blocking.Count == 1   ? AutomationReadinessLevel.ConditionallyReady :
                                    AutomationReadinessLevel.NotReady;

        return new AutomationReadinessResult(
            signalId, signalType, readinessLevel,
            BrainIntegrationValid: brainAware,
            AodEligible: aodEligible,
            AutoCommsReady: autoCommsReady,
            DeploymentStopReady: deploymentStop,
            blocking.AsReadOnly(), remediation.AsReadOnly(),
            Rationale: $"Signal has {blocking.Count} blocking issue(s). All safety criteria must pass before enabling automation.");
    }

    // -----------------------------------------------------------------------
    // Monitor Brain Integration evaluation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Evaluate a Geneva Service Monitor's suitability for Brain integration
    /// independently across four automation capabilities:
    /// BrainAwareness, OutageDeclaration, DeploymentStops, AutoComms.
    ///
    /// Each capability is assigned one of:
    ///   Enabled, ShouldBeEnabled, WillNotBeEnabled, NotClassified.
    /// </summary>
    public static MonitorBrainIntegrationResult EvaluateMonitorBrainIntegration(
        MonitorBrainIntegrationRequest req)
    {
        return new MonitorBrainIntegrationResult(
            req.MonitorName,
            new MonitorBrainIntentClassification(
                BrainAwareness:    EvaluateBrainAwareness(req),
                OutageDeclaration: EvaluateOutageDeclaration(req),
                DeploymentStops:   EvaluateDeploymentStops(req),
                AutoComms:         EvaluateAutoComms(req)));
    }

    // BrainAwareness:
    //   Enabled          – customer-impacting conditions + ICM mapping + CUJO journey linked.
    //   ShouldBeEnabled  – customer impact detected but missing ICM mapping or CUJO journey.
    //   WillNotBeEnabled – internal/operational/platform conditions only.
    //   NotClassified    – cannot be determined from provided metadata.
    private static BrainIntentStatus EvaluateBrainAwareness(MonitorBrainIntegrationRequest req)
    {
        bool customerImpact = req.DetectedImpactType == DetectedImpactType.Customer;
        bool hasCujoJourney = !string.IsNullOrEmpty(req.LinkedCujoJourney);

        if (customerImpact && req.OutageDrivingIcmMapping && hasCujoJourney)
            return BrainIntentStatus.Enabled;

        if (customerImpact)
            return BrainIntentStatus.ShouldBeEnabled;

        if (req.DetectedImpactType == DetectedImpactType.Platform ||
            req.DetectedImpactType == DetectedImpactType.Operational)
            return BrainIntentStatus.WillNotBeEnabled;

        return BrainIntentStatus.NotClassified;
    }

    // OutageDeclaration:
    //   Enabled          – LID-compliant + regionally scoped + stable + high precision.
    //   ShouldBeEnabled  – outage-relevant (ICM mapped or previously used) but missing LID or stability.
    //   WillNotBeEnabled – regional scope not detectable.
    //   NotClassified    – regional scope present but insufficient outage-relevance signals.
    private static BrainIntentStatus EvaluateOutageDeclaration(MonitorBrainIntegrationRequest req)
    {
        if (!req.RegionalScopeDetectable)
            return BrainIntentStatus.WillNotBeEnabled;

        bool stableAndPrecise =
            req.SignalStability == SignalStability.Stable &&
            req.HistoricalPrecision == HistoricalPrecision.High;

        if (req.LidPresence && stableAndPrecise)
            return BrainIntentStatus.Enabled;

        bool outageRelevant = req.OutageDrivingIcmMapping || req.UsedInOutageDeclarationPreviously;
        if (outageRelevant)
            return BrainIntentStatus.ShouldBeEnabled;

        return BrainIntentStatus.NotClassified;
    }

    // DeploymentStops:
    //   Enabled          – deployment-induced customer impact detected + subscription scope available.
    //   ShouldBeEnabled  – deployment signal exists but subscription scope not available.
    //   WillNotBeEnabled – monitor unrelated to deployment safety.
    private static BrainIntentStatus EvaluateDeploymentStops(MonitorBrainIntegrationRequest req)
    {
        bool deploymentSignal = req.DetectedImpactType == DetectedImpactType.Deployment;

        if (deploymentSignal && req.SubscriptionScopeDetectable)
            return BrainIntentStatus.Enabled;

        if (deploymentSignal)
            return BrainIntentStatus.ShouldBeEnabled;

        return BrainIntentStatus.WillNotBeEnabled;
    }

    // AutoComms:
    //   Enabled          – communication-relevant customer impact + stable + high precision signal.
    //   ShouldBeEnabled  – communication-relevant impact but not stable or not high precision.
    //   WillNotBeEnabled – platform or operational impact only.
    //   NotClassified    – communication relevance cannot be determined.
    private static BrainIntentStatus EvaluateAutoComms(MonitorBrainIntegrationRequest req)
    {
        if (req.DetectedImpactType == DetectedImpactType.Platform ||
            req.DetectedImpactType == DetectedImpactType.Operational)
            return BrainIntentStatus.WillNotBeEnabled;

        bool highPrecisionStable =
            req.HistoricalPrecision == HistoricalPrecision.High &&
            req.SignalStability == SignalStability.Stable;

        if (req.CommunicationRelevantImpact && highPrecisionStable)
            return BrainIntentStatus.Enabled;

        if (req.CommunicationRelevantImpact)
            return BrainIntentStatus.ShouldBeEnabled;

        return BrainIntentStatus.NotClassified;
    }

    // -----------------------------------------------------------------------
    // SLI template generation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Generate a starter SLI or Service Monitor template with KQL query,
    /// required dimensions, threshold placeholder, and LID compliance guidance.
    /// Auto-adds a missing Location ID dimension if none is present.
    /// </summary>
    public static Dictionary<string, object?> GenerateSliTemplate(
        string serviceName,
        string? cujoId,
        string metricNamespace,
        string metricName,
        SignalType signalType,
        BrainIntentCategory brainIntent,
        IReadOnlyList<string> dimensions,
        double? suggestedThreshold,
        int windowMinutes)
    {
        var dimClauses = string.Join("\n", dimensions.Select(d => $"| where {d} == \"<{d}_value>\""));
        string op = signalType == SignalType.SLI ? "<" : ">";
        string thresholdStr = suggestedThreshold.HasValue ? suggestedThreshold.Value.ToString("F1") : "<threshold>";

        string kql =
            $"{metricNamespace}\n" +
            $"| where MetricName == \"{metricName}\"\n" +
            (dimClauses.Length > 0 ? dimClauses + "\n" : "") +
            $"| summarize Value = avg(Value) by bin(Timestamp, {windowMinutes}m)\n" +
            $"| where Value {op} {thresholdStr}";

        var lidAdded = new List<string>();
        if (!dimensions.Any(d => LocationDimensionNamePattern.IsMatch(d))) lidAdded.Add("LocationId");

        var allDims = dimensions.Concat(lidAdded).ToList();

        return new Dictionary<string, object?>
        {
            ["type"]                = signalType.ToString(),
            ["name"]                = $"{serviceName}_{metricName}_{signalType.ToString().ToLowerInvariant()}",
            ["service_name"]        = serviceName,
            ["cujo_id"]             = (object?)cujoId ?? "<REQUIRED>",
            ["brain_intent"]        = brainIntent.ToString(),
            ["metric_namespace"]    = metricNamespace,
            ["metric_name"]         = metricName,
            ["kql_query"]           = kql,
            ["dimensions"]          = allDims,
            ["threshold"]           = (object?)(suggestedThreshold.HasValue ? suggestedThreshold.Value : "<set_threshold>"),
            ["window_minutes"]      = windowMinutes,
            ["owner"]               = "<team_alias>",
            ["notes"]               = "Review and validate all <placeholder> values before publishing. Ensure dimensions are confirmed present in MDM. Run pre-flight validation before onboarding.",
            ["lid_dimensions_added"] = lidAdded
        };
    }

    // -----------------------------------------------------------------------
    // Service health summary
    // -----------------------------------------------------------------------

    /// <summary>Produce a scored health overview for a service with top priorities.</summary>
    public static ServiceHealthSummary GetServiceHealthSummary(
        string serviceName,
        IReadOnlyList<Sli> slis,
        IReadOnlyList<ServiceMonitor> monitors,
        IReadOnlyList<string> cujoIds,
        int openRepairItems,
        int openS360Actions)
    {
        var sliCujos = slis.SelectMany(s => s.CujoIds ?? []).ToHashSet();
        var cujosWithoutSli = cujoIds.Where(c => !sliCujos.Contains(c)).ToList();

        int lidCount   = slis.Count(s => s.LidCompliant);
        int brainCount = slis.Count(s => s.BrainAware);
        int aodCount   = slis.Count(s => s.AodEnabled);
        int qualityReady = slis.Count(s =>
            s.QualityScores?.TryGetValue("overall", out var q) == true && q >= 0.7);

        int total = slis.Count + monitors.Count;

        double[] components =
        [
            slis.Count > 0 ? (double)lidCount   / slis.Count : 0.0,
            slis.Count > 0 ? (double)brainCount  / slis.Count : 0.0,
            slis.Count > 0 ? (double)qualityReady / slis.Count : 0.0,
            1.0 - Math.Min(1.0, (double)(openRepairItems + openS360Actions) / Math.Max(1, total))
        ];
        double healthScore = Math.Round(components.Average(), 2);

        var priorities = new List<string>();
        if (cujosWithoutSli.Count > 0)
        {
            string sample = string.Join(", ", cujosWithoutSli.Take(3));
            string ellipsis = cujosWithoutSli.Count > 3 ? "..." : "";
            priorities.Add($"Author SLIs for {cujosWithoutSli.Count} uncovered CUJO(s): {sample}{ellipsis}");
        }
        if (lidCount < slis.Count)
            priorities.Add($"Fix LID compliance for {slis.Count - lidCount} SLI(s).");
        if (brainCount < slis.Count)
            priorities.Add($"Enable Brain awareness for {slis.Count - brainCount} SLI(s).");
        if (openRepairItems > 0)
            priorities.Add($"Resolve {openRepairItems} open repair item(s).");
        if (openS360Actions > 0)
            priorities.Add($"Close {openS360Actions} open S360 KPI action(s).");

        return new ServiceHealthSummary(
            serviceName, slis.Count, monitors.Count,
            lidCount, brainCount, aodCount,
            openRepairItems, openS360Actions,
            cujosWithoutSli.Count, qualityReady, healthScore,
            priorities.AsReadOnly(), cujosWithoutSli.AsReadOnly());
    }
}
