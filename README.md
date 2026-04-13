# ServiceHealthAssistant

**MCP Agent for Service Health** — a C# .NET 8 intelligent automation layer that connects service owners with Service Health SRE platforms to author, validate, monitor, repair, and continuously improve SLIs and Service Monitors.

---

## Overview

The Service Health Assistant operationalises reliability best practices through a set of deterministic, auditable MCP tools:

| Domain | What it does |
|---|---|
| **Signal Design & Classification** | Decides SLI vs Service Monitor, classifies Brain Intent |
| **SLI & Monitor Authoring** | Generates starter templates with KQL, dimensions, thresholds |
| **Pre-Flight Validation** | Validates LID compliance, Brain Intent, and quality before publishing |
| **Governance & Compliance** | Scores LID compliance, enforces Brain Intent correctness |
| **Coverage & Gap Analysis** | Detects missing CUJO coverage, quality gaps, automation readiness gaps |
| **Repairs & S360 KPI Actions** | Translates gaps into repair items mapped to S360 KPI categories |
| **Automation Readiness** | Evaluates Brain/AOD eligibility with strict safety gates |
| **Brain Intent Evaluation** | Per-capability Brain Intent classification for Geneva Service Monitors |
| **ADX Persistence** | Ingests Brain Intent evaluation results to ADX via queued ingestion |
| **Service Health Summary** | Produces a scored health overview with top priorities |

---

## Project Structure

```
ServiceHealthAssistant.slnx
src/
  ServiceHealthAssistant/
    Models/
      Enums.cs                    # SignalType, BrainIntentCategory, BrainIntentStatus,
                                  #   DetectedImpactType, HistoricalPrecision, SignalStability, …
      Domain.cs                   # Sli, ServiceMonitor, CoverageGap, RepairItem, S360KpiAction
      Requests.cs                 # SignalClassificationRequest, MonitorBrainIntegrationRequest, …
      Results.cs                  # All result record types
      BrainIntentEvaluationRow.cs # ADX row schema for MCP_BrainIntentEvaluation
    Rules/
      ServiceHealthRules.cs       # Deterministic rule engine (all governance logic)
    Adx/
      IGenevaMonitorFetcher.cs    # Interface: fetch monitors from Geneva MonitorConfigMetadata
      GenevaMonitorFetcher.cs     # Kusto client → cluster('geneva.kusto.windows.net')
      IShericaMonitorFetcher.cs   # Interface: fetch monitors via GetIntegratedMonitorOutageCoverageDrillThrough
      ShericaMonitorFetcher.cs    # Kusto client → cluster('sherica-prod.uksouth.kusto.windows.net')
      IKustoBrainIntentWriter.cs  # Interface: ingest evaluation rows to ADX
      KustoBrainIntentWriter.cs   # Queued ADX ingestion → shm-dev-uksouth-kusto / SHMDatabase
    Evaluators/
      BrainIntentServiceEvaluator.cs  # Orchestrates parallel per-monitor evaluation
    Tools/
      ServiceHealthTools.cs           # MCP tool handlers (12 tools)
      BrainIntentPersistenceTools.cs  # MCP tool handlers (3 tools: evaluation + persistence)
    Program.cs                    # MCP server entry point (stdio transport)
tests/
  ServiceHealthAssistant.Tests/
    RulesTests/
      AllRulesTests.cs            # 32 xUnit tests covering the rule engine
    BrainIntentTests/
      BrainIntentPersistenceTests.cs  # 28 tests: field normalisation + batching logic
      ShericaAutoFetchTests.cs        # 6 tests: auto-fetch from sherica-prod Analytics cluster
```

---

## Tools Exposed via MCP (15 total)

### `classify_signal`
Determines whether a signal should be an **SLI** or a **Service Monitor** and classifies its **Brain Intent**.

**Parameters:** `serviceName`, `description`, `cujoId`, `hasCustomerFacingImpact`, `hasLatencyMetric`, `hasAvailabilityMetric`, `hasErrorRateMetric`, `isInfrastructureSignal`, `brainOutageModeRequired`
**Returns:** `SignalType`, `BrainIntent`, `Rationale`, `Recommendations`

---

### `validate_lid_compliance`
Checks **LID** (Latency, Impact, Dependency) compliance for a signal.

**Parameters:** `signalId`, `signalType`, `dimensionsJson`, `kqlQuery`
**Returns:** Compliance status, score (0.0–1.0), missing dimensions, recommendations

---

### `validate_brain_intent`
Validates or classifies the **Brain Intent** of a signal.

**Parameters:** `signalId`, `signalType`, `declaredIntent`, `hasCustomerFacingImpact`, `isInfrastructureSignal`, `description`
**Returns:** Correctness assessment, confidence, rationale, recommendations

---

### `score_sli_quality`
Scores an SLI across **Measurability**, **Sensitivity**, and **Relevance**.

**Parameters:** `sliId`, `metricNamespace`, `metricName`, `kqlQuery`, `dimensionsJson`, `threshold`, `windowMinutes`, `brainIntent`
**Returns:** Dimension scores, noise level, coverage/precision estimates, `publishSafe` flag

---

### `run_preflight_validation`
Runs **all pre-publish checks** in one call: LID compliance, Brain Intent, quality scoring.

**Parameters:** `signalId`, `signalType`, `metricNamespace`, `metricName`, `kqlQuery`, `dimensionsJson`, `brainIntent`, `owner`
**Returns:** `Passed` (bool), blocking issues, warnings, recommended fixes

---

### `detect_coverage_gaps`
Detects **CUJO coverage gaps**, signal quality gaps, and automation readiness gaps.

**Parameters:** `serviceName`, `cujoIds` (comma-separated), `slisJson`, `monitorsJson`
**Returns:** List of typed gaps (`DetectionGap`, `SignalQualityGap`, `AutomationReadinessGap`)

---

### `generate_repair_items`
Translates gaps into **actionable repair items** with priority, S360 KPI category, and remediation steps.

**Parameters:** `gapsJson` (output from `detect_coverage_gaps`)
**Returns:** Repair items with `WhyRequired` and `OutcomeUnblocked`

---

### `generate_s360_kpi_actions`
Groups repair items into **S360 KPI actions** by category (Lid, Quality, Coverage, Automation).

**Parameters:** `repairItemsJson` (output from `generate_repair_items`)
**Returns:** S360 actions with traceability back to repair items

---

### `evaluate_automation_readiness`
Evaluates whether a signal is **ready for Brain/AOD automation**. Never enables automation unless all safety criteria are met.

**Safety gates:** Brain awareness, CustomerImpact intent, full LID compliance, quality score ≥ 0.70
**Returns:** Readiness level (Ready / ConditionallyReady / NotReady / Blocked), blocking criteria, remediation steps

---

### `generate_sli_template`
Generates a **starter SLI or Service Monitor template** with KQL, dimensions, and threshold placeholder. Auto-adds missing LID dimensions.

**Parameters:** `serviceName`, `metricNamespace`, `metricName`, `cujoId`, `signalType`, `brainIntent`, `dimensions`, `suggestedThreshold`, `windowMinutes`
**Returns:** Template JSON ready to review and publish

---

### `get_service_health_summary`
Produces a **scored health overview** for a service with top priorities.

**Returns:** SLI/monitor counts, LID compliance rate, Brain awareness, open repairs, S360 actions, CUJOs without SLI, overall health score

---

### `get_sliq_quality_score`
Returns a **KQL query** targeting the SLIQ data source for the agent to execute via a connected Kusto MCP tool. Gated to **SLI signals only** — returns an error immediately for `ServiceMonitor` or `Unknown` signal types.

**Parameters:** `sliId`, `signalType`
**Returns:** `datasource`, `kqlQuery` (ready to execute), `instructions`

**Data source:**
```
cluster("sherica-prod.uksouth.kusto.windows.net").database('sherica-prod').SLIQualityScore
```

---

### `evaluate_monitor_brain_integration`
Classifies a Geneva Service Monitor's Brain integration eligibility across **four capabilities** independently.

**Parameters:** `monitorName`, `monitorType`, `linkedCujoJourney`, `outageDrivingIcmMapping`, `detectedImpactType`, `lidPresence`, `regionalScopeDetectable`, `subscriptionScopeDetectable`, `historicalPrecision`, `signalStability`, `usedInOutageDeclarationPreviously`, `communicationRelevantImpact`

**Returns:**

```json
{
  "MonitorName": "MyMonitor",
  "BrainIntent": {
    "BrainAwareness":     "Enabled | ShouldBeEnabled | WillNotBeEnabled | NotClassified",
    "OutageDeclaration":  "Enabled | ShouldBeEnabled | WillNotBeEnabled | NotClassified",
    "DeploymentStops":    "Enabled | ShouldBeEnabled | WillNotBeEnabled | NotClassified",
    "AutoComms":          "Enabled | ShouldBeEnabled | WillNotBeEnabled | NotClassified"
  }
}
```

**Capability gates:**

| Capability | Enabled when | WillNotBeEnabled when |
|---|---|---|
| BrainAwareness | CustomerImpact + ICM mapping + CUJO journey | Platform or Operational impact |
| OutageDeclaration | LID present + regional scope + Stable + High precision | No regional scope |
| DeploymentStops | Deployment impact + subscription scope | Non-deployment impact |
| AutoComms | CommRelevant + Stable + High precision | Platform or Operational impact |

---

### `evaluate_service_brain_intent_and_persist`
Runs `evaluate_monitor_brain_integration` across **every monitor for a service** (bounded parallelism, default 8) and **persists one row per monitor** to ADX.

**Parameters:** `serviceId`, `serviceName`, `monitorsJson`, `genevaAccountId`, `maxParallelism` (default 8), `batchSize` (default 200)

**Monitor resolution priority (first match wins):**
1. `genevaAccountId` supplied → auto-fetches from `cluster('geneva.kusto.windows.net').database('genevahealthconfigs').MonitorConfigMetadata` (rows within the last 1 hour)
2. `monitorsJson` supplied → uses the provided JSON array directly
3. Neither supplied → **auto-fetches from** `cluster('sherica-prod.uksouth.kusto.windows.net').database('Analytics')` using `GetIntegratedMonitorOutageCoverageDrillThrough(_StartTime=now(-365d), _EndTime=now())` filtered by `serviceId`

**ADX persistence target:**
```
Cluster:  https://shm-dev-uksouth-kusto.uksouth.kusto.windows.net
Database: SHMDatabase
Table:    MCP_BrainIntentEvaluation
```

**Latest-per-monitor query:**
```kql
MCP_BrainIntentEvaluation
| summarize arg_max(EvaluationTimestamp, *) by ServiceId, MonitorId
```

---

### `ingest_brain_intent_evaluation_rows`
Directly ingests **pre-formed** `BrainIntentEvaluationRow` objects into ADX. Use when evaluation results are already available (e.g. from an external pipeline or a prior run) and only persistence is needed.

**Parameters:** `rowsJson` (JSON array of evaluation rows), `batchSize` (default 200)

**Required fields per row:** `ServiceId`, `MonitorId`, `MonitorName`, `BrainAwareness`, `OutageDeclaration`, `DeploymentStops`, `AutoComms`, `EvaluationSource`, `EvaluationTimestamp`

**ADX persistence target:** same as `evaluate_service_brain_intent_and_persist`

---

## Data-Source Invocation Policy

The server embeds `ServerInstructions` (sent to clients at MCP handshake) mandating that the agent retrieves runtime data **before** any validation or recommendation. The five mandatory gates are:

| Gate | Data source | Triggers |
|---|---|---|
| **Geneva Monitor Metadata** | `cluster('sherica-prod.uksouth.kusto.windows.net').database('Analytics')` → `GetIntegratedMonitorOutageCoverageDrillThrough()` | BrainIntent presence, monitor classification, automation eligibility |
| **SLIQ / Kusto Telemetry** | `cluster('sherica-prod.uksouth.kusto.windows.net').database('sherica-prod').SLIQualityScore` | LID readiness, SLI selectivity, detection quality (SLI signals only) |
| **CUJO Hub** | `getCriticalSLIsFromCujoHub`, `getCUJOAnalysisPerService` | CUJO mapping, AOD onboarding, journey coverage |
| **IcM / Brain Propagation** | `cluster('geneva.kusto.windows.net').database('genevahealthconfigs').MonitorConfigMetadata` | BrainIntent operational effectiveness |
| **S360 KPI** | `generateS360SLIQualityKPIWrapper`, `generateS360AODKPIWrapper()` | KPI impact, repair generation, automation classification |

If runtime data cannot be retrieved, the agent must return `"Validation Pending – Runtime Data Required"` and must **not** infer values from conversational context.

---

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Azure identity** — the server uses `DefaultAzureCredential` (Managed Identity in production, developer credential chain locally) for all Kusto and ADX connections.

---

## Building

```bash
dotnet build
```

---

## Running the MCP Server

```bash
dotnet run --project src/ServiceHealthAssistant
```

The server communicates over **stdio** using the Model Context Protocol.

### MCP client configuration (e.g. Claude Desktop / VS Code)

```json
{
  "mcpServers": {
    "service-health-assistant": {
      "command": "dotnet",
      "args": ["run", "--project", "/path/to/src/ServiceHealthAssistant"]
    }
  }
}
```

Or with a published binary:

```json
{
  "mcpServers": {
    "service-health-assistant": {
      "command": "/path/to/ServiceHealthAssistant"
    }
  }
}
```

---

## Running Tests

```bash
dotnet test
```

**66 xUnit tests** across three test classes:

| Test class | Tests | Coverage |
|---|---|---|
| `AllRulesTests` | 32 | All rule engine functions |
| `BrainIntentPersistenceTests` | 28 | ADX row normalisation + batching logic |
| `ShericaAutoFetchTests` | 6 | sherica-prod auto-fetch paths and error handling |

---

## Engineering Constraints

- **Deterministic rules only** — no speculative reasoning.
- **Governance correctness over speed** — all checks must pass before automation is enabled.
- **Auditability** — every recommendation includes `WhyRequired` and `OutcomeUnblocked`.
- **Safety gates** — AOD/auto-comms are only enabled when ALL criteria are met.
- **KQL injection prevention** — all Kusto query parameters are passed via `ClientRequestProperties.SetParameter` or validated against an allowlist before use.
- **Shared ingestion path** — both `evaluate_service_brain_intent_and_persist` and `ingest_brain_intent_evaluation_rows` call the same `IKustoBrainIntentWriter.IngestBatchAsync` code path.

---

## End-to-End Flow

```
Discover  →  Reason  →  Validate  →  Recommend  →  Act  →  Learn
  (pull       (apply      (enforce     (produce      (create    (improve
 signals,    SH rules)   compliance,  concrete      repairs,   from
 CUJOs,                  readiness    repairs &     S360       outcomes)
 KPIs)                   gates)       KPI actions)  actions)
```
