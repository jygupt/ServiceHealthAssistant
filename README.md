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
| **Service Health Summary** | Produces a scored health overview with top priorities |

---

## Project Structure

```
ServiceHealthAssistant.sln
src/
  ServiceHealthAssistant/
    Models/
      Enums.cs          # SignalType, BrainIntentCategory, ComplianceStatus, …
      Domain.cs         # Sli, ServiceMonitor, CoverageGap, RepairItem, S360KpiAction
      Requests.cs       # SignalClassificationRequest, PreFlightValidationRequest
      Results.cs        # All result record types
    Rules/
      ServiceHealthRules.cs   # Deterministic rule engine (all governance logic)
    Tools/
      ServiceHealthTools.cs   # MCP tool handlers (11 tools)
    Program.cs          # MCP server entry point (stdio transport)
tests/
  ServiceHealthAssistant.Tests/
    RulesTests/
      AllRulesTests.cs  # 32 xUnit tests covering all rule engine functions
```

---

## Tools Exposed via MCP (11 total)

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

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

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

32 xUnit tests covering all rule engine functions.

---

## Engineering Constraints

- **Deterministic rules only** — no speculative reasoning.
- **Governance correctness over speed** — all checks must pass before automation is enabled.
- **Auditability** — every recommendation includes `WhyRequired` and `OutcomeUnblocked`.
- **Safety gates** — AOD/auto-comms are only enabled when ALL criteria are met.

---

## End-to-End Flow

```
Discover  →  Reason  →  Validate  →  Recommend  →  Act  →  Learn
  (pull       (apply      (enforce     (produce      (create    (improve
 signals,    SH rules)   compliance,  concrete      repairs,   from
 CUJOs,                  readiness    repairs &     S360       outcomes)
 KPIs)                   gates)       KPI actions)  actions)
```
