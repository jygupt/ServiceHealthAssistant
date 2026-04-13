using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using ServiceHealthAssistant.Models;
using ServiceHealthAssistant.Rules;

namespace ServiceHealthAssistant.Tools;

/// <summary>
/// MCP tool handlers for the Service Health Assistant.
/// Each public static method decorated with [McpServerTool] is exposed as an MCP tool.
/// All methods return JSON-serialized results for maximum interoperability.
/// </summary>
[McpServerToolType]
public sealed class ServiceHealthTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private const string SliqDatasource = "cluster(\"sherica-prod.uksouth.kusto.windows.net\").database('sherica-prod').SLIQualityScore";

    // -----------------------------------------------------------------------
    // Tool: classify_signal
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "classify_signal")]
    [Description("Decide whether a signal should be an SLI or a Service Monitor, and classify its Brain Intent (CustomerImpact or OperationalInfrastructure). Use this as the first step when onboarding a new signal.")]
    public static string ClassifySignal(
        [Description("Name of the service.")] string serviceName,
        [Description("Description of the signal and what it measures.")] string description,
        [Description("CUJO ID this signal covers (if known).")] string? cujoId = null,
        [Description("Type of telemetry (e.g., metric, log, trace).")] string telemetryType = "",
        [Description("True if the signal directly measures customer experience.")] bool hasCustomerFacingImpact = false,
        [Description("True if the signal includes a latency metric.")] bool hasLatencyMetric = false,
        [Description("True if the signal includes an availability metric.")] bool hasAvailabilityMetric = false,
        [Description("True if the signal includes an error-rate metric.")] bool hasErrorRateMetric = false,
        [Description("True if the signal only measures infrastructure health.")] bool isInfrastructureSignal = false,
        [Description("Number of existing SLIs for this service.")] int existingSliCount = 0,
        [Description("True if outage-mode Brain integration is required.")] bool brainOutageModeRequired = false)
    {
        var req = new SignalClassificationRequest(
            serviceName, description, cujoId, telemetryType,
            hasCustomerFacingImpact, hasLatencyMetric, hasAvailabilityMetric,
            hasErrorRateMetric, isInfrastructureSignal, existingSliCount, brainOutageModeRequired);

        var result = ServiceHealthRules.ClassifySignal(req);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Tool: validate_lid_compliance
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "validate_lid_compliance")]
    [Description("Validate LID (Latency, Impact, Dependency) compliance for a signal. Returns compliance status, score (0.0–1.0), missing dimensions, and remediation recommendations.")]
    public static string ValidateLidCompliance(
        [Description("Signal identifier.")] string signalId,
        [Description("Signal type: SLI or ServiceMonitor.")] SignalType signalType,
        [Description("JSON array of metric dimension objects: [{\"Name\":\"...\",\"PresentInMdm\":true}]")] string dimensionsJson = "[]",
        [Description("KQL query text (used for supplemental LID detection).")] string kqlQuery = "")
    {
        var dims = ParseDimensions(dimensionsJson);
        var result = ServiceHealthRules.EvaluateLidCompliance(signalId, signalType, dims, kqlQuery);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Tool: validate_brain_intent
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "validate_brain_intent")]
    [Description("Validate or classify the Brain Intent of a signal. Checks whether the declared intent matches the expected intent based on signal metadata. Required for Brain outage-mode and AOD eligibility.")]
    public static string ValidateBrainIntent(
        [Description("Signal identifier.")] string signalId,
        [Description("Signal type: SLI or ServiceMonitor.")] SignalType signalType,
        [Description("Declared Brain Intent: CustomerImpact, OperationalInfrastructure, or Unclassified.")] BrainIntentCategory declaredIntent,
        [Description("True if the signal directly measures customer experience.")] bool hasCustomerFacingImpact = false,
        [Description("True if the signal only measures infrastructure health.")] bool isInfrastructureSignal = false,
        [Description("Signal description (used for heuristic intent detection).")] string description = "")
    {
        var result = ServiceHealthRules.ValidateBrainIntent(
            signalId, signalType, declaredIntent,
            hasCustomerFacingImpact, isInfrastructureSignal, description);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Tool: score_sli_quality
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "score_sli_quality")]
    [Description("Score an SLI's quality across Measurability, Sensitivity, and Relevance. Returns per-dimension scores, noise estimate, and publish-safety determination. A score below 0.70 blocks publishing.")]
    public static string ScoreSliQuality(
        [Description("SLI identifier.")] string sliId,
        [Description("Metric namespace (e.g., Azure.MyService).")] string metricNamespace,
        [Description("Metric name.")] string metricName,
        [Description("KQL query text.")] string kqlQuery = "",
        [Description("JSON array of metric dimensions: [{\"Name\":\"...\",\"PresentInMdm\":true}]")] string dimensionsJson = "[]",
        [Description("SLO threshold value.")] double? threshold = null,
        [Description("Evaluation window in minutes (1–1440).")] int windowMinutes = 60,
        [Description("Declared Brain Intent: CustomerImpact, OperationalInfrastructure, or Unclassified.")] BrainIntentCategory brainIntent = BrainIntentCategory.Unclassified,
        [Description("True if the signal directly measures customer experience.")] bool hasCustomerFacingImpact = false)
    {
        var dims = ParseDimensions(dimensionsJson);
        var lidResult = ServiceHealthRules.EvaluateLidCompliance(sliId, SignalType.SLI, dims, kqlQuery);
        var brainResult = ServiceHealthRules.ValidateBrainIntent(
            sliId, SignalType.SLI, brainIntent,
            hasCustomerFacingImpact, description: kqlQuery);
        var result = ServiceHealthRules.ScoreSliQuality(
            sliId, metricNamespace, metricName, kqlQuery, dims,
            threshold, windowMinutes, lidResult, brainResult);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Tool: run_preflight_validation
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "run_preflight_validation")]
    [Description("Run all pre-publish validation checks for a signal: LID compliance, Brain Intent correctness, and SLI quality scoring. Returns a pass/fail verdict with blocking issues and recommended fixes. Unsafe or low-quality signals are blocked from onboarding.")]
    public static string RunPreFlightValidation(
        [Description("Signal identifier.")] string signalId,
        [Description("Signal type: SLI or ServiceMonitor.")] SignalType signalType,
        [Description("Metric namespace.")] string metricNamespace,
        [Description("Metric name.")] string metricName,
        [Description("KQL query text.")] string kqlQuery = "",
        [Description("JSON array of metric dimensions: [{\"Name\":\"...\",\"PresentInMdm\":true}]")] string dimensionsJson = "[]",
        [Description("Declared Brain Intent: CustomerImpact, OperationalInfrastructure, or Unclassified.")] BrainIntentCategory brainIntent = BrainIntentCategory.Unclassified,
        [Description("Signal owner (team alias).")] string owner = "")
    {
        var dims = ParseDimensions(dimensionsJson);
        var req = new PreFlightValidationRequest(
            signalId, signalType, metricNamespace, metricName,
            kqlQuery, dims, brainIntent, owner);
        var result = ServiceHealthRules.RunPreFlightValidation(req);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Tool: detect_coverage_gaps
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "detect_coverage_gaps")]
    [Description("Detect coverage gaps for a service: CUJOs without SLI coverage, low-quality signals, and automation readiness gaps. Classifies each gap as DetectionGap, SignalQualityGap, or AutomationReadinessGap.")]
    public static string DetectCoverageGaps(
        [Description("Service name.")] string serviceName,
        [Description("Comma-separated list of CUJO IDs the service should cover.")] string cujoIds = "",
        [Description("JSON array of SLI objects: [{\"Id\":\"...\",\"Name\":\"...\",\"BrainAware\":true,\"CujoIds\":[\"...\"],\"QualityScores\":{\"overall\":0.9}}]")] string slisJson = "[]",
        [Description("JSON array of Service Monitor objects: [{\"Id\":\"...\",\"Name\":\"...\",\"SliPromotionEligible\":true}]")] string monitorsJson = "[]")
    {
        var cujoList = string.IsNullOrWhiteSpace(cujoIds)
            ? []
            : cujoIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .ToList();

        var slis     = ParseSlis(slisJson, serviceName);
        var monitors = ParseMonitors(monitorsJson, serviceName);

        var gaps = ServiceHealthRules.DetectCoverageGaps(serviceName, cujoList, slis, monitors);
        return JsonSerializer.Serialize(new { gaps, total = gaps.Count }, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Tool: generate_repair_items
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "generate_repair_items")]
    [Description("Generate actionable repair items from coverage gaps. Each repair item includes a description, priority, S360 KPI category, why it is required, what outcome it unblocks, and remediation steps.")]
    public static string GenerateRepairItems(
        [Description("JSON array of coverage gap objects produced by detect_coverage_gaps.")] string gapsJson)
    {
        var gaps = ParseGaps(gapsJson);
        var repairs = ServiceHealthRules.GenerateRepairItems(gaps);
        return JsonSerializer.Serialize(new { repair_items = repairs, total = repairs.Count }, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Tool: generate_s360_kpi_actions
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "generate_s360_kpi_actions")]
    [Description("Group repair items into S360 KPI actions by category (Lid, Quality, Coverage, Automation). Returns KPI actions with traceability back to individual repair items.")]
    public static string GenerateS360KpiActions(
        [Description("JSON array of repair item objects produced by generate_repair_items.")] string repairItemsJson)
    {
        var items = ParseRepairItems(repairItemsJson);
        var actions = ServiceHealthRules.GenerateS360KpiActions(items);
        return JsonSerializer.Serialize(new { s360_actions = actions, total = actions.Count }, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Tool: evaluate_automation_readiness
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "evaluate_automation_readiness")]
    [Description("Evaluate whether a signal is ready for Brain/AOD automation. Checks Brain awareness, Brain Intent correctness, LID compliance, and quality score. Returns readiness level and blocking criteria. Never enables automation unless all safety criteria are met.")]
    public static string EvaluateAutomationReadiness(
        [Description("Signal identifier.")] string signalId,
        [Description("Signal type: SLI or ServiceMonitor.")] SignalType signalType,
        [Description("True if the signal has Brain integration enabled.")] bool brainAware = false,
        [Description("Brain Intent: CustomerImpact, OperationalInfrastructure, or Unclassified.")] BrainIntentCategory brainIntent = BrainIntentCategory.Unclassified,
        [Description("LID compliance status: Compliant, NonCompliant, Partial, or Unknown.")] ComplianceStatus lidStatus = ComplianceStatus.Unknown,
        [Description("Overall SLI quality score (0.0–1.0).")] double qualityScore = 0.0,
        [Description("True if AOD is already enabled on this signal.")] bool aodEnabled = false)
    {
        var result = ServiceHealthRules.EvaluateAutomationReadiness(
            signalId, signalType, brainAware, brainIntent, lidStatus, qualityScore, aodEnabled);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Tool: generate_sli_template
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "generate_sli_template")]
    [Description("Generate a starter SLI or Service Monitor template with KQL query, required dimensions, threshold placeholder, and LID compliance guidance. Always validate before publishing.")]
    public static string GenerateSliTemplate(
        [Description("Service name.")] string serviceName,
        [Description("Metric namespace (e.g., Azure.MyService).")] string metricNamespace,
        [Description("Metric name.")] string metricName,
        [Description("CUJO ID this signal covers.")] string? cujoId = null,
        [Description("Signal type: SLI or ServiceMonitor. Defaults to SLI.")] SignalType signalType = SignalType.SLI,
        [Description("Brain Intent: CustomerImpact, OperationalInfrastructure, or Unclassified.")] BrainIntentCategory brainIntent = BrainIntentCategory.Unclassified,
        [Description("Comma-separated dimension names to include.")] string dimensions = "",
        [Description("Suggested SLO threshold value.")] double? suggestedThreshold = null,
        [Description("Evaluation window in minutes.")] int windowMinutes = 60)
    {
        var dimList = string.IsNullOrWhiteSpace(dimensions)
            ? Array.Empty<string>()
            : dimensions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var template = ServiceHealthRules.GenerateSliTemplate(
            serviceName, cujoId, metricNamespace, metricName,
            signalType, brainIntent, dimList, suggestedThreshold, windowMinutes);
        return JsonSerializer.Serialize(template, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Tool: get_sliq_quality_score
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "get_sliq_quality_score")]
    [Description("Fetch SLIQ (SLI Quality) score data from the Kusto streaming data source for a given SLI. Only applicable for SLI signal type — returns an error for Service Monitors. Returns the KQL query to execute against the SLIQ Kusto table. Do not assume metadata values; always retrieve from the data source.")]
    public static string GetSliqQualityScore(
        [Description("SLI identifier to fetch quality score for.")] string sliId,
        [Description("Signal type. Must be SLI — SLIQ data is only available for SLI signals, not Service Monitors.")] SignalType signalType)
    {
        if (string.IsNullOrWhiteSpace(sliId))
        {
            return JsonSerializer.Serialize(new
            {
                error = "SLI identifier is required to fetch SLIQ quality score.",
                sliId
            }, JsonOptions);
        }

        if (signalType != SignalType.SLI)
        {
            return JsonSerializer.Serialize(new
            {
                error = "SLIQ quality score data is only available for SLI signals. Service Monitors are not tracked in the SLIQ data source.",
                signalType = signalType.ToString(),
                sliId
            }, JsonOptions);
        }

        // Escape any embedded double quotes in sliId to prevent KQL injection
        var safeSliId = sliId.Replace("\"", "\\\"");
        var kqlQuery = $"{SliqDatasource}\n| where SliId == \"{safeSliId}\"\n| order by Timestamp desc\n| take 1";

        return JsonSerializer.Serialize(new
        {
            sliId,
            signalType = signalType.ToString(),
            datasource = SliqDatasource,
            kqlQuery,
            instructions = "Execute this KQL query against the SLIQ Kusto data source using streaming ingestion. Use the returned quality score values directly — do not assume or infer metadata values from context."
        }, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Tool: get_service_health_summary
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "get_service_health_summary")]
    [Description("Get a high-level reliability health summary for a service, including SLI/monitor counts, LID compliance rate, Brain awareness, open repair items, S360 KPI actions, and top priorities.")]
    public static string GetServiceHealthSummary(
        [Description("Service name.")] string serviceName,
        [Description("JSON array of SLI objects.")] string slisJson = "[]",
        [Description("JSON array of Service Monitor objects.")] string monitorsJson = "[]",
        [Description("Comma-separated list of all CUJO IDs the service should cover.")] string cujoIds = "",
        [Description("Number of currently open repair items.")] int openRepairItems = 0,
        [Description("Number of currently open S360 KPI actions.")] int openS360Actions = 0)
    {
        var cujoList = string.IsNullOrWhiteSpace(cujoIds)
            ? []
            : cujoIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .ToList();

        var slis     = ParseSlis(slisJson, serviceName);
        var monitors = ParseMonitors(monitorsJson, serviceName);

        var summary = ServiceHealthRules.GetServiceHealthSummary(
            serviceName, slis, monitors, cujoList, openRepairItems, openS360Actions);
        return JsonSerializer.Serialize(summary, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Private JSON parsing helpers
    // -----------------------------------------------------------------------

    private static IReadOnlyList<MetricDimension> ParseDimensions(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
        try
        {
            var raw = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? [];
            return raw.Select(e => new MetricDimension(
                Name: e.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                PresentInMdm: e.TryGetProperty("PresentInMdm", out var p) && p.GetBoolean()
            )).ToList().AsReadOnly();
        }
        catch { return []; }
    }

    private static IReadOnlyList<Sli> ParseSlis(string json, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
        try
        {
            var raw = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? [];
            return raw.Select(e =>
            {
                string id = e.TryGetProperty("Id", out var idP) ? idP.GetString() ?? "" : "";
                string name = e.TryGetProperty("Name", out var nameP) ? nameP.GetString() ?? id : id;
                bool brainAware = e.TryGetProperty("BrainAware", out var ba) && ba.GetBoolean();
                bool lidCompliant = e.TryGetProperty("LidCompliant", out var lc) && lc.GetBoolean();
                bool aodEnabled = e.TryGetProperty("AodEnabled", out var ae) && ae.GetBoolean();
                List<string> cujos = e.TryGetProperty("CujoIds", out var cj)
                    ? cj.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : [];
                Dictionary<string, double> scores = e.TryGetProperty("QualityScores", out var qs)
                    ? qs.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble())
                    : [];
                return new Sli(id, name, serviceName,
                    BrainAware: brainAware, LidCompliant: lidCompliant, AodEnabled: aodEnabled,
                    CujoIds: cujos, QualityScores: scores);
            }).ToList().AsReadOnly();
        }
        catch { return []; }
    }

    private static IReadOnlyList<ServiceMonitor> ParseMonitors(string json, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
        try
        {
            var raw = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? [];
            return raw.Select(e =>
            {
                string id = e.TryGetProperty("Id", out var idP) ? idP.GetString() ?? "" : "";
                string name = e.TryGetProperty("Name", out var nameP) ? nameP.GetString() ?? id : id;
                bool eligible = e.TryGetProperty("SliPromotionEligible", out var sp) && sp.GetBoolean();
                return new ServiceMonitor(id, name, serviceName, SliPromotionEligible: eligible);
            }).ToList().AsReadOnly();
        }
        catch { return []; }
    }

    private static IReadOnlyList<CoverageGap> ParseGaps(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
        try
        {
            var raw = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? [];
            return raw.Select(e =>
            {
                string id = e.TryGetProperty("id", out var idP) ? idP.GetString() ?? "" : "";
                string svcName = e.TryGetProperty("service_name", out var snP) ? snP.GetString() ?? "" : "";
                GapType gapType = e.TryGetProperty("gap_type", out var gtP)
                    ? Enum.TryParse<GapType>(gtP.GetString(), ignoreCase: true, out var gt) ? gt : GapType.DetectionGap
                    : GapType.DetectionGap;
                string desc = e.TryGetProperty("description", out var dP) ? dP.GetString() ?? "" : "";
                RepairPriority severity = e.TryGetProperty("severity", out var svP)
                    ? Enum.TryParse<RepairPriority>(svP.GetString(), ignoreCase: true, out var sv) ? sv : RepairPriority.Medium
                    : RepairPriority.Medium;
                string owner = e.TryGetProperty("owner", out var owP) ? owP.GetString() ?? "" : "";
                List<string> actions = e.TryGetProperty("recommended_actions", out var raP)
                    ? raP.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : [];
                return new CoverageGap(id, svcName, gapType, desc,
                    Severity: severity, Owner: owner, RecommendedActions: actions);
            }).ToList().AsReadOnly();
        }
        catch { return []; }
    }

    private static IReadOnlyList<RepairItem> ParseRepairItems(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
        try
        {
            var raw = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? [];
            return raw.Select(e =>
            {
                string id = e.TryGetProperty("Id", out var idP) ? idP.GetString() ?? "" : "";
                string title = e.TryGetProperty("Title", out var tP) ? tP.GetString() ?? "" : "";
                string desc = e.TryGetProperty("Description", out var dP) ? dP.GetString() ?? "" : "";
                string svc = e.TryGetProperty("ServiceName", out var snP) ? snP.GetString() ?? "" : "";
                S360KpiCategory? kpi = e.TryGetProperty("S360KpiCategory", out var kpiP)
                    ? Enum.TryParse<S360KpiCategory>(kpiP.GetString(), ignoreCase: true, out var kpiVal) ? kpiVal : null
                    : null;
                return new RepairItem(id, title, desc, ServiceName: svc, S360KpiCategory: kpi);
            }).ToList().AsReadOnly();
        }
        catch { return []; }
    }
}
