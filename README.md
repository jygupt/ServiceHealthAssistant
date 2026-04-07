# ServiceHealthAssistant

**MCP Agent for Service Health** — an intelligent automation layer that connects service owners with Service Health SRE platforms to author, validate, monitor, repair, and continuously improve SLIs and Service Monitors.

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
src/
  service_health_assistant/
    __init__.py       # Package marker
    models.py         # Pydantic domain models
    rules.py          # Deterministic rule engine
    server.py         # MCP server with all tool handlers
tests/
  test_rules.py       # Unit tests for the rule engine
pyproject.toml        # Project metadata and dependencies
```

---

## Tools Exposed via MCP

### `classify_signal`
Determines whether a signal should be an **SLI** or a **Service Monitor** and classifies its **Brain Intent**.

**Input:** service name, description, CUJO ID, metric flags (customer-facing, latency, availability, error-rate, infrastructure).
**Output:** `signal_type`, `brain_intent`, `rationale`, `recommendations`.

---

### `validate_lid_compliance`
Checks **LID** (Latency, Impact, Dependency) compliance for a signal.

**Input:** signal ID, type, dimensions, KQL query.
**Output:** compliance status, score (0.0–1.0), missing dimensions, recommendations.

---

### `validate_brain_intent`
Validates or classifies the **Brain Intent** of a signal.

**Input:** signal ID, type, declared intent, metadata flags.
**Output:** correctness assessment, confidence, rationale, recommendations.

---

### `score_sli_quality`
Scores an SLI across **Measurability**, **Sensitivity**, and **Relevance**.

**Input:** SLI ID, metric namespace/name, KQL, dimensions, threshold, window, Brain Intent.
**Output:** dimension scores, noise level, coverage/precision estimates, `publish_safe` flag.

---

### `run_preflight_validation`
Runs **all pre-publish checks** in one call: LID compliance, Brain Intent, quality scoring.

**Input:** signal ID, type, metric namespace/name, KQL, dimensions, Brain Intent, owner.
**Output:** `passed` (bool), blocking issues, warnings, recommended fixes.

---

### `detect_coverage_gaps`
Detects **CUJO coverage gaps**, signal quality gaps, and automation readiness gaps.

**Input:** service name, expected CUJO IDs, SLI list, Service Monitor list.
**Output:** list of typed gaps (`DETECTION_GAP`, `SIGNAL_QUALITY_GAP`, `AUTOMATION_READINESS_GAP`).

---

### `generate_repair_items`
Translates gaps into **actionable repair items** with priority, S360 KPI category, and remediation steps.

**Input:** list of gaps.
**Output:** repair items with `why_required` and `outcome_unblocked`.

---

### `generate_s360_kpi_actions`
Groups repair items into **S360 KPI actions** by category (LID, QUALITY, COVERAGE, AUTOMATION).

**Input:** list of repair items.
**Output:** S360 actions with traceability back to repair items.

---

### `evaluate_automation_readiness`
Evaluates whether a signal is **ready for Brain/AOD automation**. Never enables automation unless all safety criteria are met.

**Safety gates:** Brain awareness, CUSTOMER_IMPACT intent, full LID compliance, quality score ≥ 0.70.
**Output:** readiness level (READY / CONDITIONALLY_READY / NOT_READY / BLOCKED), blocking criteria, remediation steps.

---

### `generate_sli_template`
Generates a **starter SLI or Service Monitor template** with KQL, dimensions, and threshold placeholder. Auto-adds missing LID dimensions.

**Input:** service name, CUJO ID, metric namespace/name, Brain Intent, dimensions, threshold, window.
**Output:** template dict ready to review and publish.

---

### `get_service_health_summary`
Produces a **scored health overview** for a service with top priorities.

**Output:** SLI/monitor counts, LID compliance rate, Brain awareness, open repairs, S360 actions, CUJOs without SLI, overall health score.

---

## Installation

```bash
pip install -e .
```

Requires Python 3.11+.

---

## Running the MCP Server

```bash
service-health-assistant
```

The server communicates over **stdio** using the Model Context Protocol.

### MCP client configuration (e.g. Claude Desktop / VS Code)

```json
{
  "mcpServers": {
    "service-health-assistant": {
      "command": "service-health-assistant"
    }
  }
}
```

---

## Running Tests

```bash
pip install -e .
pip install pytest
pytest tests/
```

---

## Engineering Constraints

- **Deterministic rules only** — no speculative reasoning.
- **Governance correctness over speed** — all checks must pass before automation is enabled.
- **Auditability** — every recommendation includes `why_required` and `outcome_unblocked`.
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
