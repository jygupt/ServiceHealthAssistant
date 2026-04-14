using Microsoft.Extensions.Logging;

namespace ServiceHealthAssistant.Repair;

/// <summary>
/// <see cref="IPropagationValidator"/> implementation that uses the
/// existing <see cref="IGenevaMonitorMetadataClient"/> to verify the "Set" signal
/// (metadata is present on the Geneva monitor) and reports "Flowing" as
/// <c>null</c> / pending until the Brain ingestion confirmation endpoint is wired.
///
/// Set check:     cluster('geneva.kusto.windows.net').database('genevahealthconfigs').MonitorConfigMetadata
/// Flowing check: TODO – Brain ingestion confirmation surface not present in repo.
///                See docs/repair-agent-bulk-metadata.md "Open Questions / Assumptions".
/// </summary>
public sealed class KustoPropagationValidator : IPropagationValidator
{
    private readonly IGenevaMonitorMetadataClient _metadataClient;
    private readonly ILogger<KustoPropagationValidator> _logger;

    public KustoPropagationValidator(
        IGenevaMonitorMetadataClient metadataClient,
        ILogger<KustoPropagationValidator> logger)
    {
        _metadataClient = metadataClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PropagationValidationResult>> ValidateAsync(
        string accountId,
        string monitorId,
        IReadOnlyDictionary<string, string> expectedMetadata,
        CancellationToken cancellationToken = default)
    {
        if (expectedMetadata.Count == 0)
            return [];

        IReadOnlyDictionary<string, string> current;
        try
        {
            current = await _metadataClient.GetCapabilityMetadataAsync(
                accountId, monitorId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Propagation validation failed for monitor '{MonitorId}': {Message}",
                monitorId, ex.Message);

            // Return all keys as unverified if the read fails.
            return expectedMetadata
                .Select(kv => new PropagationValidationResult(
                    MonitorId:     monitorId,
                    Capability:    kv.Key,
                    ExpectedValue: kv.Value,
                    ObservedValue: null,
                    IsSet:         false,
                    IsFlowing:     null))
                .ToList()
                .AsReadOnly();
        }

        var results = new List<PropagationValidationResult>(expectedMetadata.Count);

        foreach (var (key, expected) in expectedMetadata)
        {
            current.TryGetValue(key, out var observed);
            bool isSet = string.Equals(observed, expected, StringComparison.OrdinalIgnoreCase);

            // TODO: "Flowing" check (Brain ingestion confirmation) not yet implemented.
            //       Emit null to indicate "pending propagation" rather than a false negative.
            results.Add(new PropagationValidationResult(
                MonitorId:     monitorId,
                Capability:    key,
                ExpectedValue: expected,
                ObservedValue: observed,
                IsSet:         isSet,
                IsFlowing:     null));
        }

        return results.AsReadOnly();
    }
}
