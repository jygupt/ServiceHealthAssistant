using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using ServiceHealthAssistant.Adx;
using ServiceHealthAssistant.Evaluators;
using ServiceHealthAssistant.Models;

namespace ServiceHealthAssistant.Tools;

/// <summary>
/// MCP tool handler for service-level Brain Intent evaluation and ADX persistence.
/// </summary>
[McpServerToolType]
public sealed class BrainIntentPersistenceTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly BrainIntentServiceEvaluator _evaluator;
    private readonly IGenevaMonitorFetcher _genevaFetcher;

    public BrainIntentPersistenceTools(
        BrainIntentServiceEvaluator evaluator,
        IGenevaMonitorFetcher genevaFetcher)
    {
        _evaluator = evaluator;
        _genevaFetcher = genevaFetcher;
    }

    // -----------------------------------------------------------------------
    // Tool: evaluate_service_brain_intent_and_persist
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "evaluate_service_brain_intent_and_persist")]
    [Description(
        "Evaluate Brain Intent readiness for every Geneva Service Monitor belonging to a service, " +
        "then persist one result row per monitor into the ADX table " +
        "SHMDatabase.MCP_BrainIntentEvaluation on " +
        "https://shm-dev-uksouth-kusto.uksouth.kusto.windows.net. " +
        "When genevaAccountId is supplied the tool fetches all monitors automatically from " +
        "cluster('geneva.kusto.windows.net').database('genevahealthconfigs').MonitorConfigMetadata " +
        "(rows where Time_Fetched > ago(1h) and either monitor_name or MonitorGuid is non-empty). " +
        "Alternatively, pass monitor metadata explicitly in monitorsJson. " +
        "Each monitor in monitorsJson must include at minimum: Id, Name. " +
        "Optional fields mirror evaluate_monitor_brain_integration parameters.")]
    public async Task<string> EvaluateServiceBrainIntentAndPersist(
        [Description("Stable service identifier (e.g., GUID or well-known service ID).")] string serviceId,
        [Description(
            "JSON array of monitor objects. Each object supports: " +
            "Id (string, required), Name (string, required), MonitorType (string), " +
            "LinkedCujoJourney (string), OutageDrivingIcmMapping (bool), " +
            "DetectedImpactType (Customer|Platform|Deployment|Operational), " +
            "LidPresence (bool), RegionalScopeDetectable (bool), " +
            "SubscriptionScopeDetectable (bool), " +
            "HistoricalPrecision (High|Medium|Low), " +
            "SignalStability (Stable|Volatile|Unknown), " +
            "UsedInOutageDeclarationPreviously (bool), " +
            "CommunicationRelevantImpact (bool), LinkedICMIncidentId (string). " +
            "May be omitted or empty when genevaAccountId is provided."
        )] string monitorsJson = "[]",
        [Description("Human-readable service name (optional).")] string serviceName = "",
        [Description(
            "Geneva account ID used to auto-fetch all monitors for this service from " +
            "MonitorConfigMetadata (e.g. 'sherica'). " +
            "When supplied and monitorsJson is empty, monitors are fetched automatically."
        )] string genevaAccountId = "",
        [Description("Maximum concurrent monitor evaluations (default: 8).")] int maxParallelism = 8,
        [Description("Number of rows per ADX ingestion batch (default: 200).")] int batchSize = 200)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return JsonSerializer.Serialize(new
            {
                error = "serviceId is required."
            }, JsonOptions);
        }

        IReadOnlyList<MonitorEvaluationInput> monitors;

        var monitorsJsonEmpty = string.IsNullOrWhiteSpace(monitorsJson) || monitorsJson.Trim() == "[]";

        if (monitorsJsonEmpty && !string.IsNullOrWhiteSpace(genevaAccountId))
        {
            // Auto-fetch monitors from Geneva MonitorConfigMetadata.
            try
            {
                monitors = await _genevaFetcher.FetchMonitorsForAccountAsync(genevaAccountId);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    error = $"Failed to fetch monitors from Geneva for account '{genevaAccountId}': {ex.Message}",
                    serviceId,
                    serviceName,
                    genevaAccountId
                }, JsonOptions);
            }
        }
        else
        {
            try
            {
                monitors = ParseMonitorInputs(monitorsJson);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    error = $"Failed to parse monitorsJson: {ex.Message}"
                }, JsonOptions);
            }
        }

        if (monitors.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                serviceId,
                serviceName,
                genevaAccountId = string.IsNullOrWhiteSpace(genevaAccountId) ? null : genevaAccountId,
                evaluatedCount = 0,
                message = string.IsNullOrWhiteSpace(genevaAccountId)
                    ? "No monitors provided. Pass at least one monitor in monitorsJson."
                    : $"No monitors found for Geneva account '{genevaAccountId}'."
            }, JsonOptions);
        }

        try
        {
            var rows = await _evaluator.EvaluateAndPersistAsync(
                serviceId, serviceName, monitors,
                evaluationTimestamp: DateTime.UtcNow,
                batchSize: batchSize,
                maxParallelism: maxParallelism);

            return JsonSerializer.Serialize(new
            {
                serviceId,
                serviceName,
                evaluatedCount = rows.Count,
                evaluationTimestamp = rows[0].EvaluationTimestamp,
                evaluationSource = rows[0].EvaluationSource,
                summary = rows.Select(r => new
                {
                    monitorId          = r.MonitorId,
                    monitorName        = r.MonitorName,
                    brainAwareness     = r.BrainAwareness.ToString(),
                    outageDeclaration  = r.OutageDeclaration.ToString(),
                    deploymentStops    = r.DeploymentStops.ToString(),
                    autoComms          = r.AutoComms.ToString()
                }).ToList()
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Evaluation or ingestion failed: {ex.Message}",
                serviceId,
                serviceName
            }, JsonOptions);
        }
    }

    // -----------------------------------------------------------------------
    // JSON parsing helper
    // -----------------------------------------------------------------------

    private static IReadOnlyList<MonitorEvaluationInput> ParseMonitorInputs(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return [];

        var raw = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? [];
        return raw.Select(e =>
        {
            string id   = GetString(e, "Id")   ?? GetString(e, "id")   ?? "";
            string name = GetString(e, "Name") ?? GetString(e, "name") ?? id;

            return new MonitorEvaluationInput(
                MonitorId:                       id,
                MonitorName:                     name,
                MonitorType:                     GetString(e, "MonitorType"),
                LinkedCujoJourney:               GetString(e, "LinkedCujoJourney"),
                OutageDrivingIcmMapping:         GetBool(e, "OutageDrivingIcmMapping"),
                DetectedImpactType:              GetEnum(e, "DetectedImpactType", DetectedImpactType.Operational),
                LidPresence:                     GetBool(e, "LidPresence"),
                RegionalScopeDetectable:         GetBool(e, "RegionalScopeDetectable"),
                SubscriptionScopeDetectable:     GetBool(e, "SubscriptionScopeDetectable"),
                HistoricalPrecision:             GetEnum(e, "HistoricalPrecision", HistoricalPrecision.Low),
                SignalStability:                 GetEnum(e, "SignalStability", SignalStability.Unknown),
                UsedInOutageDeclarationPreviously: GetBool(e, "UsedInOutageDeclarationPreviously"),
                CommunicationRelevantImpact:     GetBool(e, "CommunicationRelevantImpact"),
                LinkedICMIncidentId:             GetString(e, "LinkedICMIncidentId"));
        }).ToList().AsReadOnly();
    }

    private static string? GetString(JsonElement e, string key) =>
        e.TryGetProperty(key, out var p) ? p.GetString() : null;

    private static bool GetBool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;

    private static T GetEnum<T>(JsonElement e, string key, T defaultValue) where T : struct, Enum =>
        e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String &&
        Enum.TryParse<T>(p.GetString(), ignoreCase: true, out var val)
            ? val
            : defaultValue;
}
