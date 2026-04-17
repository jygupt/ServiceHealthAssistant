using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using ServiceHealthAssistant.Adx;
using ServiceHealthAssistant.Evaluators;
using ServiceHealthAssistant.Models;

namespace ServiceHealthAssistant.Tools;

/// <summary>
/// MCP tool handler for coverage gap detection.
/// Exposes the <c>detect_coverage_gaps</c> tool, which accepts a serviceId and
/// auto-fetches CUJO data from the Analytics cluster when optional parameters
/// (cujoIds, slisJson, monitorsJson) are not provided.
/// </summary>
[McpServerToolType]
public sealed class CoverageGapTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly CoverageGapAnalyzer _analyzer;

    public CoverageGapTools(CoverageGapAnalyzer analyzer)
    {
        _analyzer = analyzer;
    }

    // -----------------------------------------------------------------------
    // Tool: detect_coverage_gaps
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "detect_coverage_gaps")]
    [Description(
        "Detect coverage gaps for a service. " +
        "When cujoIds is omitted, CUJO data is automatically fetched from " +
        "cluster('sherica-prod.uksouth.kusto.windows.net').database('sherica-prod') " +
        "using CUJOMetadata, CujoToSloRelationship, and CujoToMonitorRelationship tables — " +
        "a CUJO is flagged as a DetectionGap when it has neither an SLO nor a monitor mapping (isCujoMapped = false). " +
        "Optionally supply cujoIds (comma-separated) to skip the auto-fetch and evaluate only those CUJOs. " +
        "slisJson and monitorsJson are accepted for future gap-type evaluations (signal quality, " +
        "automation readiness, SLI promotion) but are not yet evaluated in this iteration.")]
    public async Task<string> DetectCoverageGaps(
        [Description("ServiceTree ID of the service to analyse (e.g. a GUID such as '724c33bf-1ab8-4691-adb1-0e61932919c2').")] string serviceId,
        [Description(
            "Comma-separated CUJO IDs to evaluate. " +
            "When empty (default), all active CUJOs for the service are fetched automatically from the Analytics cluster."
        )] string cujoIds = "",
        [Description(
            "JSON array of SLI objects for future signal-quality and automation-readiness gap detection. " +
            "Not evaluated in the current iteration. " +
            "Format: [{\"Id\":\"...\",\"Name\":\"...\",\"BrainAware\":true,\"CujoIds\":[\"...\"],\"QualityScores\":{\"overall\":0.9}}]"
        )] string slisJson = "[]",
        [Description(
            "JSON array of Service Monitor objects for future SLI-promotion gap detection. " +
            "Not evaluated in the current iteration. " +
            "Format: [{\"Id\":\"...\",\"Name\":\"...\",\"SliPromotionEligible\":true}]"
        )] string monitorsJson = "[]")
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return JsonSerializer.Serialize(new
            {
                error = "serviceId is required."
            }, JsonOptions);
        }

        // Case 1: cujoIds empty — auto-fetch from Analytics cluster.
        var cujoIdsEmpty = string.IsNullOrWhiteSpace(cujoIds);
        if (cujoIdsEmpty)
        {
            try
            {
                var result = await _analyzer.DetectDetectionGapsAsync(
                    serviceId, CancellationToken.None);

                return JsonSerializer.Serialize(new
                {
                    serviceId,
                    dataSource = "cluster('sherica-prod.uksouth.kusto.windows.net').database('sherica-prod') — CUJOMetadata + CujoToSloRelationship + CujoToMonitorRelationship",
                    totalCujos = result.TotalCujos,
                    unmappedCujos = result.UnmappedCujos,
                    gaps = result.Gaps,
                    total = result.Gaps.Count,
                    note = "Signal quality gaps, automation readiness gaps, and SLI promotion candidates will be evaluated in a future iteration."
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    error = $"Failed to fetch CUJO data or analyse gaps: {ex.Message}",
                    serviceId
                }, JsonOptions);
            }
        }

        // Case 2: cujoIds provided — try to parse as JSON array of CujoMappingRow first,
        // then fall back to treating as comma-separated CUJO IDs (fetch from Kusto).
        IReadOnlyList<CujoMappingRow>? parsedRows = null;
        try
        {
            var trimmed = cujoIds.Trim();
            if (trimmed.StartsWith('['))
            {
                parsedRows = JsonSerializer.Deserialize<List<CujoMappingRow>>(trimmed, JsonOptions);
            }
        }
        catch (JsonException)
        {
            // Not a JSON array — fall through to the comma-separated ID path.
        }

        if (parsedRows is not null)
        {
            // Case 2a: caller supplied pre-formed CujoMappingRow objects as JSON.
            try
            {
                var result = _analyzer.AnalyzeFromRows(serviceId, parsedRows);
                return JsonSerializer.Serialize(new
                {
                    serviceId,
                    dataSource = "caller-supplied cujoIds (parsed as CujoMappingRow JSON array)",
                    totalCujos = result.TotalCujos,
                    unmappedCujos = result.UnmappedCujos,
                    gaps = result.Gaps,
                    total = result.Gaps.Count,
                    note = "Signal quality gaps, automation readiness gaps, and SLI promotion candidates will be evaluated in a future iteration."
                }, JsonOptions);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new
                {
                    error = $"Gap analysis failed: {ex.Message}",
                    serviceId
                }, JsonOptions);
            }
        }

        // Case 2b: comma-separated CUJO ID strings — not yet implemented.
        // Future: fetch specific CUJOs from Analytics filtered to these IDs.
        return JsonSerializer.Serialize(new
        {
            serviceId,
            note = "Passing cujoIds as a comma-separated list of ID strings is not yet implemented. " +
                   "Either omit cujoIds (auto-fetch from Analytics) or pass a JSON array of CujoMappingRow objects.",
            cujoIds
        }, JsonOptions);
    }
}
