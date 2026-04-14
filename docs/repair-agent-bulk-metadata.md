# Bulk Geneva Brain Capability Metadata Repair Agent

## What exists vs what's missing

### What exists in the repo

| Component | Location | Description |
|---|---|---|
| `BrainIntentStatus` enum | `Models/Enums.cs` | `Enabled`, `ShouldBeEnabled`, `WillNotBeEnabled`, `NotClassified` |
| Metadata key names | `Program.cs` (ServerInstructions) | `BrainIntent.BrainAwareness`, `BrainIntent.OutageDeclaration`, `BrainIntent.DeploymentStops`, `BrainIntent.AutoComms` |
| `MonitorConfigMetadata` read KQL | `Program.cs` & `GenevaMonitorFetcher.cs` | Reads current metadata from `cluster('geneva.kusto.windows.net').database('genevahealthconfigs')` |
| `BrainIntentServiceEvaluator` | `Evaluators/` | Bounded-concurrency parallel evaluator with batched ADX ingestion |
| `IKustoBrainIntentWriter` / `KustoBrainIntentWriter` | `Adx/` | Queued ingestion to `SHMDatabase.MCP_BrainIntentEvaluation` |
| `DefaultAzureCredential` auth | All ADX clients | Managed Identity → developer credential chain |
| `ILogger<T>` telemetry | All services | Structured logging via Microsoft.Extensions.Logging |

### What was added by this PR

| Component | Location | Purpose |
|---|---|---|
| `BrainCapabilityMetadataKeys` | `Repair/BrainCapabilityMetadataKeys.cs` | Canonical metadata key constants (derived from ServerInstructions) |
| `IGenevaMonitorMetadataClient` | `Repair/IGenevaMonitorMetadataClient.cs` | Read/write interface for Brain capability metadata on Geneva monitors |
| `GenevaMonitorMetadataClient` | `Repair/GenevaMonitorMetadataClient.cs` | Kusto-backed read implementation; **write path is a stub** (see Open Questions) |
| `IDashboardMonitorSetProvider` | `Repair/IDashboardMonitorSetProvider.cs` | Interface for Brain Intent Dashboard monitor-set queries |
| `KustoDashboardMonitorSetProvider` | `Repair/KustoDashboardMonitorSetProvider.cs` | Reads from `MCP_BrainIntentEvaluation`; optional `actionRequired=Yes` filter |
| `IPropagationValidator` | `Repair/IPropagationValidator.cs` | "Set vs Flowing" validation interface |
| `KustoPropagationValidator` | `Repair/KustoPropagationValidator.cs` | Set check via Kusto; Flowing is `null` / PendingPropagation (see Open Questions) |
| `RetryPolicy` | `Repair/RetryPolicy.cs` | Exponential backoff retry with jitter and cancellation support |
| `BulkGenevaBrainCapabilityMetadataRepairAgent` | `Repair/BulkGenevaBrainCapabilityMetadataRepairAgent.cs` | Main orchestrator |
| `BrainCapabilityRepairTools` | `Tools/BrainCapabilityRepairTools.cs` | MCP tool: `bulk_repair_brain_capability_metadata` |
| Repair models | `Models/RepairModels.cs` | Input/output contracts |
| Unit tests (24 new) | `tests/.../RepairTests/BulkRepairAgentTests.cs` | Batching, throttling, idempotency, partial failures, retry, propagation |

---

## Open Questions / Assumptions

### 1. Geneva metadata write endpoint (CRITICAL – unblocks production use)

**Missing:** The repository contains no REST/ARM endpoint for writing Brain capability metadata to a Geneva Service Monitor.

**Assumption:** A write API exists (likely Azure Resource Manager or Geneva REST API) that accepts a JSON body of key-value metadata pairs for a monitor identified by account ID + monitor name/GUID.

**Current state:** `GenevaMonitorMetadataClient.UpdateCapabilityMetadataAsync` is a stub that returns `Succeeded: false` with an explanatory error message. The agent will report all changes as `AttemptedChanges` but `AppliedChanges` will be empty until this is wired.

**To unblock:** Confirm the endpoint contract and replace the TODO in `GenevaMonitorMetadataClient.UpdateCapabilityMetadataAsync`.

Expected contract (example – not invented, pending confirmation):
```
PUT https://<geneva-api>/accounts/{accountId}/monitors/{monitorId}/metadata
Authorization: Bearer <token>
Body: { "BrainIntent.BrainAwareness": "Enabled", "BrainIntent.OutageDeclaration": "ShouldBeEnabled" }
```

### 2. Brain ingestion "Flowing" confirmation endpoint

**Missing:** No endpoint or Kusto query surface exists in the repo to confirm that metadata has been ingested by Brain downstream of Geneva → IcM.

**Assumption:** Such a confirmation surface exists or will exist.

**Current state:** `KustoPropagationValidator` reports `IsFlowing = null` (PendingPropagation) for all monitors. The "Set" check (Geneva MonitorConfigMetadata) is fully implemented.

**To unblock:** Wire the Brain ingestion confirmation in `KustoPropagationValidator`.

### 3. Brain Intent Dashboard repair tagging / NextAction surface

**Missing:** No query surface for dashboard repair tags (NextAction, ActionRequired flags beyond the `ShouldBeEnabled` derivation already in `MCP_BrainIntentEvaluation`) is present in the repo.

**Current state:** `DashboardMonitorDescriptor.NextAction` is always `null`. The `ActionRequired=Yes` filter is derived from `BrainAwareness/OutageDeclaration/DeploymentStops/AutoComms == ShouldBeEnabled` in `MCP_BrainIntentEvaluation`.

**To unblock:** Confirm whether the repair tagging surface is a separate Kusto table, a column in `MCP_BrainIntentEvaluation`, or an external API.

### 4. Canonical per-capability metadata allowed values

**Assumption:** Allowed values for all four capabilities map directly to `BrainIntentStatus` enum values:
- `Enabled`
- `ShouldBeEnabled`
- `WillNotBeEnabled`
- `NotClassified`

Source: `Program.cs` ServerInstructions KQL examples use these exact strings.

### 5. "Run as owner" vs service identity

**Assumption:** The agent runs under the same `DefaultAzureCredential` pattern used throughout the repo (Managed Identity in production, developer credential chain locally). No "run as owner" delegation model is implemented as no such pattern was found in the repo.

---

## Usage examples

### Dry run (default) – compute planned changes

```json
// MCP tool call
{
  "tool": "bulk_repair_brain_capability_metadata",
  "arguments": {
    "serviceId": "df36aee8-c644-400b-a0ab-fd0f1191211d",
    "genevaAccountId": "sherica",
    "brainAwareness": "Enabled",
    "outageDeclaration": "ShouldBeEnabled",
    "actionRequiredFilter": "Yes",
    "dryRun": true,
    "batchSize": 20,
    "maxConcurrency": 4
  }
}
```

**What it does:**
1. Fetches all monitors for the service from `MCP_BrainIntentEvaluation` where at least one capability is `ShouldBeEnabled`.
2. For each monitor, reads current metadata from `MonitorConfigMetadata`.
3. Computes the delta (only monitors/keys that differ from desired state).
4. Returns a structured result with `attemptedChanges` per monitor and an audit log.
5. **No writes to Geneva.**

### Apply changes (non-DryRun)

```json
{
  "tool": "bulk_repair_brain_capability_metadata",
  "arguments": {
    "serviceId": "df36aee8-c644-400b-a0ab-fd0f1191211d",
    "genevaAccountId": "sherica",
    "brainAwareness": "Enabled",
    "dryRun": false,
    "batchSize": 10,
    "maxConcurrency": 4,
    "maxRetry": 3,
    "correlationId": "run-2026-04-14-001"
  }
}
```

> **Note:** Until the Geneva write endpoint is wired (Open Question #1), the agent will report `AttemptedChanges` but `AppliedChanges` will be empty and the update result will indicate the stub is active.

### Target explicit monitors

```json
{
  "tool": "bulk_repair_brain_capability_metadata",
  "arguments": {
    "serviceId": "df36aee8-c644-400b-a0ab-fd0f1191211d",
    "genevaAccountId": "sherica",
    "monitorIdsJson": "[\"CheckoutAvailability\", \"ApiLatency\", \"ErrorRate\"]",
    "brainAwareness": "Enabled",
    "outageDeclaration": "Enabled",
    "dryRun": false
  }
}
```

---

## Output structure

```json
{
  "serviceId": "...",
  "serviceName": "...",
  "correlationId": "...",
  "dryRun": true,
  "executionTimestamp": "2026-04-14T09:00:00Z",
  "summary": {
    "totalTargeted": 50,
    "succeeded": 48,
    "failed": 1,
    "pendingPropagation": 48,
    "shouldBeEnabledCandidatesUpdated": 30,
    "perCapabilityUpdated": {
      "BrainIntent.BrainAwareness": 30,
      "BrainIntent.OutageDeclaration": 20,
      "BrainIntent.DeploymentStops": 0,
      "BrainIntent.AutoComms": 15
    }
  },
  "monitorResults": [
    {
      "monitorId": "CheckoutAvailability",
      "monitorName": "CheckoutAvailability",
      "attemptedChanges": { "BrainIntent.BrainAwareness": "Enabled" },
      "appliedChanges": {},
      "setValidationStatus": "Skipped",
      "flowValidationStatus": "Skipped",
      "errors": []
    }
  ],
  "auditLog": [
    {
      "correlationId": "...",
      "serviceId": "...",
      "monitorId": "CheckoutAvailability",
      "capability": "BrainIntent.BrainAwareness",
      "previousValue": "ShouldBeEnabled",
      "newValue": "Enabled",
      "status": "DryRun",
      "executedBy": "ServiceHealthAssistant/BulkRepairAgent",
      "timestamp": "2026-04-14T09:00:00Z",
      "reason": "Bulk Brain capability metadata repair"
    }
  ]
}
```

### Validation status values

| Status | Meaning |
|---|---|
| `Verified` | Metadata confirmed set correctly on Geneva monitor |
| `Failed` | Metadata set but value doesn't match, or update failed |
| `PendingPropagation` | Metadata is Set; downstream Brain propagation not yet confirmed |
| `Skipped` | Validation skipped (DryRun mode or capability not targeted) |
| `NotApplicable` | Not applicable for this monitor/capability |

---

## Operational safeguards

| Safeguard | Mechanism |
|---|---|
| **DryRun by default** | No writes without explicit `dryRun=false` |
| **Idempotency** | Reads current metadata; only delta changes are applied |
| **Retry with backoff** | `RetryPolicy` with exponential back-off + ±10% jitter |
| **Bounded concurrency** | `SemaphoreSlim(MaxConcurrency)` per batch |
| **Batch processing** | Configurable `BatchSize` to throttle Geneva API load |
| **StopOnFirstFailure** | Optional: halt after first batch containing a failure |
| **Pre-change state** | Audit log records `PreviousValue` per capability per monitor |
| **Rollback plan** | Audit log provides complete before/after for manual rollback |
| **Set vs Flowing** | Post-update validation checks metadata is set AND (eventually) flowing |
| **PendingPropagation** | Avoids false negatives for propagation latency |

---

## How results map to dashboard governance

The `summary.shouldBeEnabledCandidatesUpdated` count tracks the key governance KPI:
> **ShouldBeEnabled → Enabled transitions per run**

This value can be surfaced on the Brain Intent Dashboard as a measure of repair throughput and compliance improvement over time.

The `auditLog` provides a complete governance-grade record of every change attempted or applied, including:
- Who: `executedBy = "ServiceHealthAssistant/BulkRepairAgent"`
- What: capability key + previous and new value
- When: UTC timestamp
- Why: `reason` field + `correlationId` for cross-system tracing
- Status: `DryRun` / `Applied` / `Failed` / `NoOp`

---

## Telemetry and observability

- All key operations emit structured `ILogger` log entries (Info for normal flow, Warning for retries and stubs, Error for failures).
- `ClientRequestId` is set on every Kusto query for distributed tracing: `ServiceHealthAssistant;GetCapabilityMetadata;<Guid>`.
- `CorrelationId` threads through all audit entries and log messages.
- Per-capability counts and ShouldBeEnabled transition count are returned in the result summary for dashboard ingestion.
