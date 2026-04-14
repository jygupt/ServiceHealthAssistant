using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using ServiceHealthAssistant.Models;
using ServiceHealthAssistant.Repair;

namespace ServiceHealthAssistant.Tools;

/// <summary>
/// MCP tool surface for the <see cref="BulkGenevaBrainCapabilityMetadataRepairAgent"/>.
/// Exposes a single tool: <c>bulk_repair_brain_capability_metadata</c>.
/// </summary>
[McpServerToolType]
public sealed class BrainCapabilityRepairTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly BulkGenevaBrainCapabilityMetadataRepairAgent _agent;

    public BrainCapabilityRepairTools(BulkGenevaBrainCapabilityMetadataRepairAgent agent)
    {
        _agent = agent;
    }

    // -----------------------------------------------------------------------
    // Tool: bulk_repair_brain_capability_metadata
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "bulk_repair_brain_capability_metadata")]
    [Description(
        "Performs a bulk Brain capability metadata repair for Geneva Service Monitors belonging to a " +
        "service, eliminating per-monitor manual UI edits and enabling dashboard-driven governance " +
        "at service scale. " +
        "Supported capabilities: BrainAwareness, OutageDeclaration, DeploymentStops, AutoComms. " +
        "The agent applies only delta changes (idempotent) and tracks ShouldBeEnabled → Enabled " +
        "transitions as a governance KPI. " +
        "DryRun=true (default) computes and returns planned changes without writing to Geneva. " +
        "Set DryRun=false to apply changes. " +
        "Post-update the agent validates both 'Set' (metadata configured on the Geneva monitor) " +
        "and 'Flowing' (metadata propagated to IcM / Brain) status per monitor. " +
        "When Flowing is not yet confirmed due to propagation latency, a structured " +
        "'pendingPropagation' status is emitted instead of failing. " +
        "When monitorIds is empty the monitor set is fetched automatically from the Brain Intent " +
        "Dashboard (MCP_BrainIntentEvaluation), optionally filtered to actionRequired='Yes' " +
        "(monitors with at least one capability in ShouldBeEnabled). " +
        "Results include per-monitor detail, per-capability counts, and a full audit log.")]
    public async Task<string> BulkRepairBrainCapabilityMetadata(
        [Description("Stable service identifier (e.g., GUID or well-known service ID).")] string serviceId,
        [Description("Geneva account ID (e.g. 'sherica') used for metadata reads and writes.")] string genevaAccountId,
        [Description("Desired state for BrainAwareness capability. " +
                     "Allowed values: Enabled | ShouldBeEnabled | WillNotBeEnabled | NotClassified. " +
                     "Omit or pass empty to leave unchanged.")] string brainAwareness = "",
        [Description("Desired state for OutageDeclaration capability. " +
                     "Allowed values: Enabled | ShouldBeEnabled | WillNotBeEnabled | NotClassified. " +
                     "Omit or pass empty to leave unchanged.")] string outageDeclaration = "",
        [Description("Desired state for DeploymentStops capability. " +
                     "Allowed values: Enabled | ShouldBeEnabled | WillNotBeEnabled | NotClassified. " +
                     "Omit or pass empty to leave unchanged.")] string deploymentStops = "",
        [Description("Desired state for AutoComms capability. " +
                     "Allowed values: Enabled | ShouldBeEnabled | WillNotBeEnabled | NotClassified. " +
                     "Omit or pass empty to leave unchanged.")] string autoComms = "",
        [Description("JSON array of explicit monitor IDs to target (e.g. [\"mon-001\",\"mon-002\"]). " +
                     "When empty, monitors are fetched from the Brain Intent Dashboard.")] string monitorIdsJson = "[]",
        [Description("Human-readable service name (informational).")] string serviceName = "",
        [Description("Correlation ID for audit tracing. Auto-generated when empty.")] string correlationId = "",
        [Description("Filter monitors from the Brain Intent Dashboard. " +
                     "Pass 'Yes' to target only 'Action Required' monitors (ShouldBeEnabled). " +
                     "Pass empty to target all monitors.")] string actionRequiredFilter = "Yes",
        [Description("When true (default) compute planned changes without writing to Geneva.")] bool dryRun = true,
        [Description("Number of monitors processed per batch (default: 10).")] int batchSize = 10,
        [Description("Maximum concurrent metadata operations within a batch (default: 4).")] int maxConcurrency = 4,
        [Description("Maximum retry attempts per monitor update (default: 3).")] int maxRetry = 3,
        [Description("When true, stop the entire run after the first monitor failure (default: false).")] bool stopOnFirstFailure = false)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return JsonSerializer.Serialize(new { error = "serviceId is required." }, JsonOptions);
        }

        if (string.IsNullOrWhiteSpace(genevaAccountId))
        {
            return JsonSerializer.Serialize(new { error = "genevaAccountId is required." }, JsonOptions);
        }

        // Parse desired capability states.
        CapabilityTargetStates desiredStates;
        try
        {
            desiredStates = new CapabilityTargetStates(
                BrainAwareness:    ParseOptionalStatus(brainAwareness),
                OutageDeclaration: ParseOptionalStatus(outageDeclaration),
                DeploymentStops:   ParseOptionalStatus(deploymentStops),
                AutoComms:         ParseOptionalStatus(autoComms));
        }
        catch (ArgumentException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions);
        }

        if (!desiredStates.BrainAwareness.HasValue
            && !desiredStates.OutageDeclaration.HasValue
            && !desiredStates.DeploymentStops.HasValue
            && !desiredStates.AutoComms.HasValue)
        {
            return JsonSerializer.Serialize(new
            {
                error = "At least one capability target state must be specified " +
                        "(brainAwareness, outageDeclaration, deploymentStops, or autoComms)."
            }, JsonOptions);
        }

        // Parse explicit monitor IDs.
        IReadOnlyList<string>? monitorIds = null;
        try
        {
            monitorIds = ParseMonitorIds(monitorIdsJson);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Failed to parse monitorIdsJson: {ex.Message}"
            }, JsonOptions);
        }

        var request = new BrainCapabilityRepairRequest(
            ServiceId:                    serviceId,
            ServiceName:                  serviceName,
            GenevaAccountId:              genevaAccountId,
            CorrelationId:                correlationId,
            MonitorIds:                   monitorIds is { Count: > 0 } ? monitorIds : null,
            DashboardActionRequiredFilter: actionRequiredFilter,
            DesiredStates:                desiredStates,
            DryRun:                       dryRun,
            BatchSize:                    batchSize,
            MaxConcurrency:               maxConcurrency,
            MaxRetry:                     maxRetry,
            RetryBackoff:                 null,
            StopOnFirstFailure:           stopOnFirstFailure);

        BrainCapabilityRepairResult result;
        try
        {
            result = await _agent.ExecuteAsync(request);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error      = $"BulkRepair execution failed: {ex.Message}",
                serviceId,
                serviceName,
                correlationId
            }, JsonOptions);
        }

        return JsonSerializer.Serialize(new
        {
            serviceId              = result.ServiceId,
            serviceName            = result.ServiceName,
            correlationId          = result.CorrelationId,
            dryRun                 = result.DryRun,
            executionTimestamp     = result.ExecutionTimestamp,
            summary = new
            {
                totalTargeted                    = result.TotalTargeted,
                succeeded                        = result.Succeeded,
                failed                           = result.Failed,
                pendingPropagation               = result.PendingPropagation,
                shouldBeEnabledCandidatesUpdated = result.ShouldBeEnabledCandidatesUpdated,
                perCapabilityUpdated             = result.PerCapabilityUpdated
            },
            monitorResults = result.MonitorResults.Select(r => new
            {
                monitorId            = r.MonitorId,
                monitorName          = r.MonitorName,
                attemptedChanges     = r.AttemptedChanges,
                appliedChanges       = r.AppliedChanges,
                setValidationStatus  = r.SetValidationStatus.ToString(),
                flowValidationStatus = r.FlowValidationStatus.ToString(),
                errors               = r.Errors
            }),
            auditLog = result.AuditLog.Select(e => new
            {
                correlationId = e.CorrelationId,
                serviceId     = e.ServiceId,
                monitorId     = e.MonitorId,
                capability    = e.Capability,
                previousValue = e.PreviousValue,
                newValue      = e.NewValue,
                status        = e.Status,
                executedBy    = e.ExecutedBy,
                timestamp     = e.Timestamp,
                reason        = e.Reason
            })
        }, JsonOptions);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static BrainIntentStatus? ParseOptionalStatus(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (Enum.TryParse<BrainIntentStatus>(raw, ignoreCase: true, out var val))
            return val;

        var allowed = string.Join(", ", Enum.GetNames<BrainIntentStatus>());
        throw new ArgumentException(
            $"Invalid BrainIntentStatus value '{raw}'. Allowed values: {allowed}.");
    }

    private static IReadOnlyList<string> ParseMonitorIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() is "[]" or "null")
            return [];

        var elements = JsonSerializer.Deserialize<List<JsonElement>>(json) ?? [];
        return elements
            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList()
            .AsReadOnly();
    }
}
