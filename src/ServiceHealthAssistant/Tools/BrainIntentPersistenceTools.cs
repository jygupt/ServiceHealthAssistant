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
    private readonly IShericaMonitorFetcher _shericaFetcher;
    private readonly IKustoBrainIntentWriter _writer;

    public BrainIntentPersistenceTools(
        BrainIntentServiceEvaluator evaluator,
        IGenevaMonitorFetcher genevaFetcher,
        IShericaMonitorFetcher shericaFetcher,
        IKustoBrainIntentWriter writer)
    {
        _evaluator = evaluator;
        _genevaFetcher = genevaFetcher;
        _shericaFetcher = shericaFetcher;
        _writer = writer;
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
        "When neither genevaAccountId nor monitorsJson is provided, monitors are fetched automatically " +
        "from cluster('sherica-prod.uksouth.kusto.windows.net').database('Analytics') using " +
        "GetIntegratedMonitorOutageCoverageDrillThrough(_StartTime=now(-365d),_EndTime=now()) " +
        "filtered by serviceId. " +
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
        else if (monitorsJsonEmpty)
        {
            // No monitorsJson and no genevaAccountId – auto-fetch from sherica-prod Analytics cluster
            // using GetIntegratedMonitorOutageCoverageDrillThrough filtered by serviceId.
            try
            {
                monitors = await _shericaFetcher.FetchMonitorsForServiceAsync(serviceId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    error = $"Failed to fetch monitors from sherica-prod for service '{serviceId}': {ex.Message}",
                    serviceId,
                    serviceName
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
                message = !string.IsNullOrWhiteSpace(genevaAccountId)
                    ? $"No monitors found for Geneva account '{genevaAccountId}'."
                    : monitorsJsonEmpty
                        ? $"No monitors found for service '{serviceId}' in GetIntegratedMonitorOutageCoverageDrillThrough."
                        : "No monitors provided. Pass at least one monitor in monitorsJson."
            }, JsonOptions);
        }

        try
        {
            var rows = await _evaluator.EvaluateAsync(
                serviceId, serviceName, monitors,
                evaluationTimestamp: DateTime.UtcNow,
                maxParallelism: maxParallelism);

            // Persist evaluated rows using the same ingestion path as
            // ingest_brain_intent_evaluation_rows.
            for (int i = 0; i < rows.Count; i += batchSize)
            {
                var batch = rows.Skip(i).Take(batchSize).ToList().AsReadOnly();
                await _writer.IngestBatchAsync(batch);
            }

            return JsonSerializer.Serialize(new
            {
                serviceId,
                serviceName,
                evaluatedCount = rows.Count,
                evaluationTimestamp = rows[0].EvaluationTimestamp,
                evaluationSource = rows[0].EvaluationSource,
                ingestionTarget = new
                {
                    cluster  = "https://shm-dev-uksouth-kusto.uksouth.kusto.windows.net",
                    database = "SHMDatabase",
                    table    = "MCP_BrainIntentEvaluation"
                },
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
    // Tool: ingest_brain_intent_evaluation_rows
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "ingest_brain_intent_evaluation_rows")]
    [Description(
        "Directly ingest pre-formed Brain Intent evaluation rows into the ADX table " +
        "SHMDatabase.MCP_BrainIntentEvaluation on " +
        "https://shm-dev-uksouth-kusto.uksouth.kusto.windows.net. " +
        "Use this tool when evaluation results are already available (e.g., produced by " +
        "evaluate_service_brain_intent_and_persist or an external pipeline) and only " +
        "persistence is needed. Each row must include at minimum: " +
        "ServiceId, MonitorId, MonitorName, BrainAwareness, OutageDeclaration, " +
        "DeploymentStops, AutoComms, EvaluationSource, EvaluationTimestamp.")]
    public async Task<string> IngestBrainIntentEvaluationRows(
        [Description(
            "JSON array of evaluation row objects. Required fields per row: " +
            "ServiceId (string), ServiceName (string), MonitorId (string), " +
            "MonitorName (string), MonitorType (string), IsSLI (bool), " +
            "BrainAwareness (Enabled|ShouldBeEnabled|WillNotBeEnabled|NotClassified), " +
            "OutageDeclaration (Enabled|ShouldBeEnabled|WillNotBeEnabled|NotClassified), " +
            "DeploymentStops (Enabled|ShouldBeEnabled|WillNotBeEnabled|NotClassified), " +
            "AutoComms (Enabled|ShouldBeEnabled|WillNotBeEnabled|NotClassified), " +
            "EvaluationSource (string), EvaluationTimestamp (ISO-8601 UTC string). " +
            "Optional fields: CujoJourney, LinkedICMIncidentId, LIDPresent (bool), " +
            "RegionalScopeDetectable (bool), SubscriptionScopeDetectable (bool), " +
            "HistoricalPrecision (High|Medium|Low), " +
            "SignalStability (Stable|Volatile|Unknown), CommunicationRelevant (bool)."
        )] string rowsJson,
        [Description("Number of rows per ADX ingestion batch (default: 200).")] int batchSize = 200)
    {
        if (string.IsNullOrWhiteSpace(rowsJson) || rowsJson.Trim() == "[]")
        {
            return JsonSerializer.Serialize(new
            {
                error = "rowsJson is required and must contain at least one row."
            }, JsonOptions);
        }

        IReadOnlyList<BrainIntentEvaluationRow> rows;
        try
        {
            rows = ParseEvaluationRows(rowsJson);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Failed to parse rowsJson: {ex.Message}"
            }, JsonOptions);
        }

        if (rows.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                error = "No valid rows found in rowsJson."
            }, JsonOptions);
        }

        try
        {
            for (int i = 0; i < rows.Count; i += batchSize)
            {
                var batch = rows.Skip(i).Take(batchSize).ToList().AsReadOnly();
                await _writer.IngestBatchAsync(batch);
            }

            return JsonSerializer.Serialize(new
            {
                ingestedCount = rows.Count,
                ingestionTarget = new
                {
                    cluster  = "https://shm-dev-uksouth-kusto.uksouth.kusto.windows.net",
                    database = "SHMDatabase",
                    table    = "MCP_BrainIntentEvaluation"
                },
                message = $"Successfully submitted {rows.Count} row(s) for ingestion."
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Ingestion failed: {ex.Message}",
                ingestedCount = 0
            }, JsonOptions);
        }
    }

    // -----------------------------------------------------------------------
    // JSON parsing helper
    // -----------------------------------------------------------------------

    private static IReadOnlyList<BrainIntentEvaluationRow> ParseEvaluationRows(string json)
    {
        var raw = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? [];
        return raw.Select(e =>
        {
            string serviceId    = GetString(e, "ServiceId")    ?? "";
            string serviceName  = GetString(e, "ServiceName")  ?? "";
            string monitorId    = GetString(e, "MonitorId")    ?? "";
            string monitorName  = GetString(e, "MonitorName")  ?? monitorId;
            string monitorType  = GetString(e, "MonitorType")  ?? "";
            bool   isSli        = GetBool(e, "IsSLI");

            var brainAwareness    = GetEnum(e, "BrainAwareness",    BrainIntentStatus.NotClassified);
            var outageDeclaration = GetEnum(e, "OutageDeclaration", BrainIntentStatus.NotClassified);
            var deploymentStops   = GetEnum(e, "DeploymentStops",   BrainIntentStatus.NotClassified);
            var autoComms         = GetEnum(e, "AutoComms",         BrainIntentStatus.NotClassified);

            string evalSource = GetString(e, "EvaluationSource") ?? "MCP:ingest_brain_intent_evaluation_rows";
            DateTime evalTimestamp = e.TryGetProperty("EvaluationTimestamp", out var tsProp)
                && DateTime.TryParse(tsProp.GetString(), out var parsedTs)
                    ? DateTime.SpecifyKind(parsedTs, DateTimeKind.Utc)
                    : DateTime.UtcNow;

            return new BrainIntentEvaluationRow(
                ServiceId:                   serviceId,
                ServiceName:                 serviceName,
                MonitorId:                   monitorId,
                MonitorName:                 monitorName,
                MonitorType:                 monitorType,
                IsSLI:                       isSli,
                BrainAwareness:              brainAwareness,
                OutageDeclaration:           outageDeclaration,
                DeploymentStops:             deploymentStops,
                AutoComms:                   autoComms,
                EvaluationSource:            evalSource,
                EvaluationTimestamp:         evalTimestamp,
                CujoJourney:                 GetString(e, "CujoJourney"),
                LinkedICMIncidentId:         GetString(e, "LinkedICMIncidentId"),
                LIDPresent:                  GetNullableBool(e, "LIDPresent"),
                RegionalScopeDetectable:     GetNullableBool(e, "RegionalScopeDetectable"),
                SubscriptionScopeDetectable: GetNullableBool(e, "SubscriptionScopeDetectable"),
                HistoricalPrecision:         GetNullableEnum<HistoricalPrecision>(e, "HistoricalPrecision"),
                SignalStability:             GetNullableEnum<SignalStability>(e, "SignalStability"),
                CommunicationRelevant:       GetNullableBool(e, "CommunicationRelevant"));
        }).ToList().AsReadOnly();
    }

    private static bool? GetNullableBool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? p.GetBoolean()
            : null;

    private static T? GetNullableEnum<T>(JsonElement e, string key) where T : struct, Enum =>
        e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
        && Enum.TryParse<T>(p.GetString(), ignoreCase: true, out var val)
            ? val
            : null;

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
