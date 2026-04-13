using ServiceHealthAssistant.Models;

namespace ServiceHealthAssistant.Adx;

/// <summary>
/// Abstraction for writing Brain Intent evaluation rows to ADX.
/// </summary>
public interface IKustoBrainIntentWriter
{
    /// <summary>
    /// Ingest a batch of evaluation rows into the ADX table
    /// SHMDatabase.MCP_BrainIntentEvaluation.
    /// </summary>
    Task IngestBatchAsync(IReadOnlyList<BrainIntentEvaluationRow> rows, CancellationToken cancellationToken = default);
}
