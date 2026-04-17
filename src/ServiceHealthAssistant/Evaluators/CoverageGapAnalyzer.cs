using Microsoft.Extensions.Logging;
using ServiceHealthAssistant.Adx;
using ServiceHealthAssistant.Models;

namespace ServiceHealthAssistant.Evaluators;

/// <summary>
/// Orchestrates coverage gap detection for a service.
/// Currently implements detection gap analysis (CUJOs with no SLI/monitor mapping).
/// Additional gap types (signal quality, automation readiness, SLI promotion) will be
/// added in future iterations.
/// </summary>
public sealed class CoverageGapAnalyzer
{
    private readonly IShericaCujoFetcher _cujoFetcher;
    private readonly ILogger<CoverageGapAnalyzer> _logger;

    public CoverageGapAnalyzer(
        IShericaCujoFetcher cujoFetcher,
        ILogger<CoverageGapAnalyzer> logger)
    {
        _cujoFetcher = cujoFetcher;
        _logger = logger;
    }

    /// <summary>
    /// Detects detection gaps (CUJOs without any SLO or monitor mapping) for a service
    /// by auto-fetching CUJO data from the Analytics cluster.
    /// </summary>
    /// <param name="serviceId">ServiceTree ID used to filter CUJOs from Analytics.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    ///   A <see cref="CoverageGapAnalysisResult"/> containing the fetched CUJO rows and
    ///   the list of <see cref="CoverageGap"/> items for unmapped CUJOs.
    /// </returns>
    public async Task<CoverageGapAnalysisResult> DetectDetectionGapsAsync(
        string serviceId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Fetching CUJOs for service '{ServiceId}' to detect coverage gaps.", serviceId);

        var cujos = await _cujoFetcher.FetchCujosForServiceAsync(serviceId, cancellationToken);

        _logger.LogInformation(
            "Fetched {Total} CUJOs for service '{ServiceId}'. Evaluating detection gaps.",
            cujos.Count, serviceId);

        var result = AnalyzeFromRows(serviceId, cujos);

        _logger.LogInformation(
            "Detection gap analysis complete for service '{ServiceId}': {GapCount} gap(s) from {Total} CUJO(s).",
            serviceId, result.Gaps.Count, cujos.Count);

        return result;
    }

    /// <summary>
    /// Runs detection gap analysis against a pre-loaded set of <see cref="CujoMappingRow"/> objects.
    /// Use this when the caller has already fetched or parsed CUJO rows (e.g. from a JSON parameter).
    /// </summary>
    /// <param name="serviceId">ServiceTree ID used to label the result.</param>
    /// <param name="rows">Pre-loaded CUJO mapping rows to evaluate.</param>
    /// <returns>A <see cref="CoverageGapAnalysisResult"/> for the provided rows.</returns>
    public CoverageGapAnalysisResult AnalyzeFromRows(string serviceId, IReadOnlyList<CujoMappingRow> rows)
    {
        var gaps = BuildDetectionGaps(rows);
        return new CoverageGapAnalysisResult(
            ServiceId: serviceId,
            TotalCujos: rows.Count,
            UnmappedCujos: gaps.Count,
            Cujos: rows,
            Gaps: gaps);
    }

    /// <summary>
    /// Core gap-detection loop: flags every CUJO where <see cref="CujoMappingRow.IsCujoMapped"/> is false.
    /// Shared by both the auto-fetch and the caller-supplied-rows code paths.
    /// </summary>
    private static IReadOnlyList<CoverageGap> BuildDetectionGaps(IReadOnlyList<CujoMappingRow> cujos)
    {
        var gaps = new List<CoverageGap>();

        foreach (var cujo in cujos)
        {
            if (!cujo.IsCujoMapped)
            {
                gaps.Add(new CoverageGap(
                    Id: $"gap-detect-{cujo.InternalCujoId}",
                    ServiceName: cujo.ServiceName,
                    GapType: GapType.DetectionGap,
                    Description: $"CUJO '{cujo.CujoName}' (id: {cujo.InternalCujoId}) has no SLO or monitor mapping.",
                    CujoId: cujo.InternalCujoId,
                    Severity: RepairPriority.High,
                    RecommendedActions: new[]
                    {
                        $"Author an SLI for CUJO '{cujo.CujoName}'.",
                        "Validate metric availability in MDM before authoring.",
                        cujo.HasExceptionForSloCreation
                            ? "Note: This CUJO has an exception for SLO creation on record."
                            : "No SLO creation exception on record — SLO authoring is expected.",
                        cujo.IsImplementationBlocked
                            ? "Implementation is currently blocked — review blockers before proceeding."
                            : string.Empty
                    }.Where(s => !string.IsNullOrEmpty(s)).ToArray()));
            }
        }

        return gaps.AsReadOnly();
    }
}

/// <summary>
/// Result of a coverage gap analysis run, including raw CUJO rows and detected gaps.
/// </summary>
public sealed record CoverageGapAnalysisResult(
    string ServiceId,
    int TotalCujos,
    int UnmappedCujos,
    IReadOnlyList<CujoMappingRow> Cujos,
    IReadOnlyList<CoverageGap> Gaps);
