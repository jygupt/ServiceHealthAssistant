"""
MCP Server for the Service Health Assistant.

Registers all tool handlers and exposes them via the Model Context Protocol.
"""

from __future__ import annotations

import json
import logging
import sys
import traceback
from typing import Any

import mcp.server.stdio
import mcp.types as types
from mcp.server import Server

from .models import (
    BrainIntentCategory,
    ComplianceStatus,
    CoverageGap,
    MetricDimension,
    PreFlightValidationRequest,
    RepairItem,
    S360KPIAction,
    SLI,
    ServiceMonitor,
    SignalClassificationRequest,
    SignalType,
)
from .rules import (
    classify_signal,
    detect_coverage_gaps,
    evaluate_automation_readiness,
    evaluate_lid_compliance,
    generate_repair_items,
    generate_s360_kpi_actions,
    generate_sli_template,
    run_preflight_validation,
    score_sli_quality,
    validate_brain_intent,
)

# ---------------------------------------------------------------------------
# Server setup
# ---------------------------------------------------------------------------

app = Server("service-health-assistant")


# ---------------------------------------------------------------------------
# Tool definitions
# ---------------------------------------------------------------------------

TOOLS: list[types.Tool] = [
    types.Tool(
        name="classify_signal",
        description=(
            "Decide whether a signal should be an SLI or a Service Monitor, "
            "and classify its Brain Intent (CUSTOMER_IMPACT or "
            "OPERATIONAL_INFRASTRUCTURE). Use this as the first step when "
            "onboarding a new signal."
        ),
        inputSchema={
            "type": "object",
            "required": ["service_name", "description"],
            "properties": {
                "service_name": {"type": "string", "description": "Name of the service."},
                "description": {
                    "type": "string",
                    "description": "Description of the signal and what it measures.",
                },
                "cujo_id": {
                    "type": "string",
                    "description": "CUJO ID this signal covers (if known).",
                },
                "telemetry_type": {
                    "type": "string",
                    "description": "Type of telemetry (e.g., metric, log, trace).",
                },
                "has_customer_facing_impact": {
                    "type": "boolean",
                    "description": "True if the signal directly measures customer experience.",
                },
                "has_latency_metric": {"type": "boolean"},
                "has_availability_metric": {"type": "boolean"},
                "has_error_rate_metric": {"type": "boolean"},
                "is_infrastructure_signal": {
                    "type": "boolean",
                    "description": "True if signal only measures infrastructure health.",
                },
                "existing_sli_count": {
                    "type": "integer",
                    "description": "Number of existing SLIs for this service.",
                },
                "brain_outage_mode_required": {
                    "type": "boolean",
                    "description": "True if outage-mode Brain integration is required.",
                },
            },
        },
    ),
    types.Tool(
        name="validate_lid_compliance",
        description=(
            "Validate LID (Latency, Impact, Dependency) compliance for a signal. "
            "Returns compliance status, score, missing dimensions, and remediation "
            "recommendations."
        ),
        inputSchema={
            "type": "object",
            "required": ["signal_id", "signal_type"],
            "properties": {
                "signal_id": {"type": "string"},
                "signal_type": {
                    "type": "string",
                    "enum": ["SLI", "SERVICE_MONITOR"],
                },
                "dimensions": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "name": {"type": "string"},
                            "values": {"type": "array", "items": {"type": "string"}},
                            "required": {"type": "boolean"},
                            "present_in_mdm": {"type": "boolean"},
                        },
                        "required": ["name"],
                    },
                    "description": "List of metric dimensions.",
                },
                "kql_query": {
                    "type": "string",
                    "description": "KQL query text (used for supplemental LID detection).",
                },
            },
        },
    ),
    types.Tool(
        name="validate_brain_intent",
        description=(
            "Validate or classify the Brain Intent of a signal. "
            "Checks whether the declared intent matches expected intent based on signal metadata. "
            "Required for Brain outage-mode and AOD eligibility."
        ),
        inputSchema={
            "type": "object",
            "required": ["signal_id", "signal_type", "declared_intent"],
            "properties": {
                "signal_id": {"type": "string"},
                "signal_type": {"type": "string", "enum": ["SLI", "SERVICE_MONITOR"]},
                "declared_intent": {
                    "type": "string",
                    "enum": ["CUSTOMER_IMPACT", "OPERATIONAL_INFRASTRUCTURE", "UNCLASSIFIED"],
                },
                "has_customer_facing_impact": {"type": "boolean"},
                "is_infrastructure_signal": {"type": "boolean"},
                "description": {"type": "string"},
            },
        },
    ),
    types.Tool(
        name="score_sli_quality",
        description=(
            "Score an SLI's quality across Measurability, Sensitivity, and Relevance. "
            "Returns per-dimension scores, noise estimate, and publish-safety determination. "
            "A score below 0.70 blocks publishing."
        ),
        inputSchema={
            "type": "object",
            "required": ["sli_id", "metric_namespace", "metric_name"],
            "properties": {
                "sli_id": {"type": "string"},
                "metric_namespace": {"type": "string"},
                "metric_name": {"type": "string"},
                "kql_query": {"type": "string"},
                "dimensions": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "name": {"type": "string"},
                            "present_in_mdm": {"type": "boolean"},
                        },
                        "required": ["name"],
                    },
                },
                "threshold": {"type": "number"},
                "window_minutes": {"type": "integer"},
                "brain_intent": {
                    "type": "string",
                    "enum": ["CUSTOMER_IMPACT", "OPERATIONAL_INFRASTRUCTURE", "UNCLASSIFIED"],
                },
                "has_customer_facing_impact": {"type": "boolean"},
            },
        },
    ),
    types.Tool(
        name="run_preflight_validation",
        description=(
            "Run all pre-publish validation checks for a signal: "
            "LID compliance, Brain Intent correctness, and SLI quality scoring. "
            "Returns a pass/fail verdict with blocking issues and recommended fixes. "
            "Unsafe or low-quality signals are blocked from onboarding."
        ),
        inputSchema={
            "type": "object",
            "required": ["signal_id", "signal_type", "metric_namespace", "metric_name"],
            "properties": {
                "signal_id": {"type": "string"},
                "signal_type": {"type": "string", "enum": ["SLI", "SERVICE_MONITOR"]},
                "metric_namespace": {"type": "string"},
                "metric_name": {"type": "string"},
                "kql_query": {"type": "string"},
                "dimensions": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "name": {"type": "string"},
                            "present_in_mdm": {"type": "boolean"},
                        },
                        "required": ["name"],
                    },
                },
                "brain_intent": {
                    "type": "string",
                    "enum": ["CUSTOMER_IMPACT", "OPERATIONAL_INFRASTRUCTURE", "UNCLASSIFIED"],
                },
                "owner": {"type": "string"},
            },
        },
    ),
    types.Tool(
        name="detect_coverage_gaps",
        description=(
            "Detect coverage gaps for a service: CUJOs without SLI coverage, "
            "low-quality signals, and automation readiness gaps. "
            "Classifies each gap as DETECTION_GAP, SIGNAL_QUALITY_GAP, or "
            "AUTOMATION_READINESS_GAP."
        ),
        inputSchema={
            "type": "object",
            "required": ["service_name"],
            "properties": {
                "service_name": {"type": "string"},
                "cujo_ids": {
                    "type": "array",
                    "items": {"type": "string"},
                    "description": "List of CUJO IDs the service should cover.",
                },
                "slis": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "id": {"type": "string"},
                            "name": {"type": "string"},
                            "cujo_ids": {"type": "array", "items": {"type": "string"}},
                            "brain_aware": {"type": "boolean"},
                            "quality_scores": {"type": "object"},
                        },
                        "required": ["id", "name"],
                    },
                    "description": "Existing SLIs for this service.",
                },
                "monitors": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "id": {"type": "string"},
                            "name": {"type": "string"},
                            "sli_promotion_eligible": {"type": "boolean"},
                        },
                        "required": ["id", "name"],
                    },
                    "description": "Existing Service Monitors for this service.",
                },
            },
        },
    ),
    types.Tool(
        name="generate_repair_items",
        description=(
            "Generate actionable repair items from coverage gaps. "
            "Each repair item includes a description, priority, S360 KPI category, "
            "why it is required, what outcome it unblocks, and remediation steps."
        ),
        inputSchema={
            "type": "object",
            "required": ["gaps"],
            "properties": {
                "gaps": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "id": {"type": "string"},
                            "service_name": {"type": "string"},
                            "cujo_id": {"type": "string"},
                            "gap_type": {
                                "type": "string",
                                "enum": [
                                    "DETECTION_GAP",
                                    "SIGNAL_QUALITY_GAP",
                                    "AUTOMATION_READINESS_GAP",
                                ],
                            },
                            "description": {"type": "string"},
                            "severity": {
                                "type": "string",
                                "enum": ["CRITICAL", "HIGH", "MEDIUM", "LOW"],
                            },
                            "owner": {"type": "string"},
                            "recommended_actions": {
                                "type": "array",
                                "items": {"type": "string"},
                            },
                        },
                        "required": ["id", "service_name", "gap_type", "description"],
                    },
                }
            },
        },
    ),
    types.Tool(
        name="generate_s360_kpi_actions",
        description=(
            "Group repair items into S360 KPI actions by category "
            "(LID, QUALITY, COVERAGE, AUTOMATION). "
            "Returns KPI actions with traceability back to individual repair items."
        ),
        inputSchema={
            "type": "object",
            "required": ["repair_items"],
            "properties": {
                "repair_items": {
                    "type": "array",
                    "items": {
                        "type": "object",
                        "properties": {
                            "id": {"type": "string"},
                            "title": {"type": "string"},
                            "description": {"type": "string"},
                            "service_name": {"type": "string"},
                            "priority": {
                                "type": "string",
                                "enum": ["CRITICAL", "HIGH", "MEDIUM", "LOW"],
                            },
                            "s360_kpi_category": {
                                "type": "string",
                                "enum": ["LID", "QUALITY", "COVERAGE", "AUTOMATION"],
                            },
                        },
                        "required": ["id", "title", "description"],
                    },
                }
            },
        },
    ),
    types.Tool(
        name="evaluate_automation_readiness",
        description=(
            "Evaluate whether a signal is ready for Brain/AOD automation. "
            "Checks Brain awareness, Brain Intent correctness, LID compliance, "
            "and quality score. Returns readiness level and blocking criteria. "
            "Never enables automation unless all safety criteria are met."
        ),
        inputSchema={
            "type": "object",
            "required": ["signal_id", "signal_type"],
            "properties": {
                "signal_id": {"type": "string"},
                "signal_type": {"type": "string", "enum": ["SLI", "SERVICE_MONITOR"]},
                "brain_aware": {"type": "boolean"},
                "brain_intent": {
                    "type": "string",
                    "enum": ["CUSTOMER_IMPACT", "OPERATIONAL_INFRASTRUCTURE", "UNCLASSIFIED"],
                },
                "lid_status": {
                    "type": "string",
                    "enum": ["COMPLIANT", "NON_COMPLIANT", "PARTIAL", "UNKNOWN"],
                },
                "quality_score": {
                    "type": "number",
                    "description": "Overall SLI quality score (0.0–1.0).",
                },
                "aod_enabled": {"type": "boolean"},
            },
        },
    ),
    types.Tool(
        name="generate_sli_template",
        description=(
            "Generate a starter SLI or Service Monitor template with KQL query, "
            "required dimensions, threshold placeholder, and LID compliance guidance. "
            "Use this as a starting point — always validate before publishing."
        ),
        inputSchema={
            "type": "object",
            "required": ["service_name", "metric_namespace", "metric_name"],
            "properties": {
                "service_name": {"type": "string"},
                "cujo_id": {"type": "string"},
                "metric_namespace": {"type": "string"},
                "metric_name": {"type": "string"},
                "signal_type": {
                    "type": "string",
                    "enum": ["SLI", "SERVICE_MONITOR"],
                    "description": "Defaults to SLI.",
                },
                "brain_intent": {
                    "type": "string",
                    "enum": ["CUSTOMER_IMPACT", "OPERATIONAL_INFRASTRUCTURE", "UNCLASSIFIED"],
                },
                "dimensions": {
                    "type": "array",
                    "items": {"type": "string"},
                    "description": "Dimension names to include.",
                },
                "suggested_threshold": {"type": "number"},
                "window_minutes": {"type": "integer"},
            },
        },
    ),
    types.Tool(
        name="get_service_health_summary",
        description=(
            "Get a high-level reliability health summary for a service, including "
            "SLI/monitor counts, LID compliance rate, Brain awareness, open repair "
            "items, S360 KPI actions, and top priorities."
        ),
        inputSchema={
            "type": "object",
            "required": ["service_name"],
            "properties": {
                "service_name": {"type": "string"},
                "slis": {
                    "type": "array",
                    "items": {"type": "object"},
                    "description": "List of SLI objects.",
                },
                "monitors": {
                    "type": "array",
                    "items": {"type": "object"},
                    "description": "List of Service Monitor objects.",
                },
                "cujo_ids": {
                    "type": "array",
                    "items": {"type": "string"},
                    "description": "All CUJO IDs the service should cover.",
                },
                "open_repair_items": {"type": "integer"},
                "open_s360_actions": {"type": "integer"},
            },
        },
    ),
]


# ---------------------------------------------------------------------------
# Tool list handler
# ---------------------------------------------------------------------------


@app.list_tools()
async def list_tools() -> list[types.Tool]:
    return TOOLS


# ---------------------------------------------------------------------------
# Tool call handlers
# ---------------------------------------------------------------------------


_logger = logging.getLogger(__name__)


@app.call_tool()
async def call_tool(
    name: str, arguments: dict[str, Any]
) -> list[types.TextContent]:
    try:
        result = _dispatch(name, arguments)
        return [types.TextContent(type="text", text=json.dumps(result, indent=2))]
    except Exception as exc:  # noqa: BLE001
        _logger.error("Tool '%s' raised an exception:\n%s", name, traceback.format_exc())
        return [
            types.TextContent(
                type="text",
                text=json.dumps({"error": str(exc), "tool": name}, indent=2),
            )
        ]


def _dispatch(name: str, args: dict[str, Any]) -> Any:
    if name == "classify_signal":
        return _handle_classify_signal(args)
    if name == "validate_lid_compliance":
        return _handle_validate_lid(args)
    if name == "validate_brain_intent":
        return _handle_validate_brain_intent(args)
    if name == "score_sli_quality":
        return _handle_score_sli_quality(args)
    if name == "run_preflight_validation":
        return _handle_preflight(args)
    if name == "detect_coverage_gaps":
        return _handle_detect_gaps(args)
    if name == "generate_repair_items":
        return _handle_generate_repairs(args)
    if name == "generate_s360_kpi_actions":
        return _handle_generate_s360(args)
    if name == "evaluate_automation_readiness":
        return _handle_eval_automation(args)
    if name == "generate_sli_template":
        return _handle_generate_template(args)
    if name == "get_service_health_summary":
        return _handle_service_summary(args)
    raise ValueError(f"Unknown tool: {name}")


# ---------------------------------------------------------------------------
# Individual handlers
# ---------------------------------------------------------------------------


def _handle_classify_signal(args: dict[str, Any]) -> dict[str, Any]:
    req = SignalClassificationRequest(
        service_name=args["service_name"],
        description=args["description"],
        cujo_id=args.get("cujo_id"),
        telemetry_type=args.get("telemetry_type", ""),
        has_customer_facing_impact=args.get("has_customer_facing_impact", False),
        has_latency_metric=args.get("has_latency_metric", False),
        has_availability_metric=args.get("has_availability_metric", False),
        has_error_rate_metric=args.get("has_error_rate_metric", False),
        is_infrastructure_signal=args.get("is_infrastructure_signal", False),
        existing_sli_count=args.get("existing_sli_count", 0),
        brain_outage_mode_required=args.get("brain_outage_mode_required", False),
    )
    return classify_signal(req)


def _parse_dimensions(raw: list[dict[str, Any]]) -> list[MetricDimension]:
    return [
        MetricDimension(
            name=d["name"],
            values=d.get("values", []),
            required=d.get("required", False),
            present_in_mdm=d.get("present_in_mdm", False),
        )
        for d in raw
    ]


def _handle_validate_lid(args: dict[str, Any]) -> dict[str, Any]:
    dims = _parse_dimensions(args.get("dimensions", []))
    result = evaluate_lid_compliance(
        signal_id=args["signal_id"],
        signal_type=SignalType(args["signal_type"]),
        dimensions=dims,
        kql_query=args.get("kql_query", ""),
    )
    return result.model_dump()


def _handle_validate_brain_intent(args: dict[str, Any]) -> dict[str, Any]:
    result = validate_brain_intent(
        signal_id=args["signal_id"],
        signal_type=SignalType(args["signal_type"]),
        declared_intent=BrainIntentCategory(args["declared_intent"]),
        has_customer_facing_impact=args.get("has_customer_facing_impact", False),
        is_infrastructure_signal=args.get("is_infrastructure_signal", False),
        description=args.get("description", ""),
    )
    return result.model_dump()


def _handle_score_sli_quality(args: dict[str, Any]) -> dict[str, Any]:
    dims = _parse_dimensions(args.get("dimensions", []))
    brain_intent = BrainIntentCategory(
        args.get("brain_intent", BrainIntentCategory.UNCLASSIFIED.value)
    )
    lid_result = evaluate_lid_compliance(
        signal_id=args["sli_id"],
        signal_type=SignalType.SLI,
        dimensions=dims,
        kql_query=args.get("kql_query", ""),
    )
    brain_result = validate_brain_intent(
        signal_id=args["sli_id"],
        signal_type=SignalType.SLI,
        declared_intent=brain_intent,
        has_customer_facing_impact=args.get("has_customer_facing_impact", False),
        description=args.get("kql_query", ""),
    )
    result = score_sli_quality(
        sli_id=args["sli_id"],
        metric_namespace=args["metric_namespace"],
        metric_name=args["metric_name"],
        kql_query=args.get("kql_query", ""),
        dimensions=dims,
        threshold=args.get("threshold"),
        window_minutes=args.get("window_minutes", 60),
        lid_result=lid_result,
        brain_intent_result=brain_result,
    )
    return result.model_dump()


def _handle_preflight(args: dict[str, Any]) -> dict[str, Any]:
    dims = _parse_dimensions(args.get("dimensions", []))
    brain_intent_raw = args.get("brain_intent", "UNCLASSIFIED")
    req = PreFlightValidationRequest(
        signal_id=args["signal_id"],
        signal_type=SignalType(args["signal_type"]),
        metric_namespace=args["metric_namespace"],
        metric_name=args["metric_name"],
        kql_query=args.get("kql_query", ""),
        dimensions=dims,
        brain_intent=BrainIntentCategory(brain_intent_raw),
        owner=args.get("owner", ""),
    )
    result = run_preflight_validation(req)
    return result.model_dump()


def _handle_detect_gaps(args: dict[str, Any]) -> dict[str, Any]:
    service_name = args["service_name"]
    cujo_ids = args.get("cujo_ids", [])

    slis = [
        SLI(
            id=s["id"],
            name=s.get("name", s["id"]),
            service_name=service_name,
            cujo_ids=s.get("cujo_ids", []),
            brain_aware=s.get("brain_aware", False),
            quality_scores=s.get("quality_scores", {}),
        )
        for s in args.get("slis", [])
    ]

    monitors = [
        ServiceMonitor(
            id=m["id"],
            name=m.get("name", m["id"]),
            service_name=service_name,
            sli_promotion_eligible=m.get("sli_promotion_eligible", False),
        )
        for m in args.get("monitors", [])
    ]

    gaps = detect_coverage_gaps(service_name, cujo_ids, slis, monitors)
    return {"gaps": [g.model_dump() for g in gaps], "total": len(gaps)}


def _handle_generate_repairs(args: dict[str, Any]) -> dict[str, Any]:
    gaps = [
        CoverageGap(
            id=g["id"],
            service_name=g.get("service_name", ""),
            cujo_id=g.get("cujo_id"),
            gap_type=g["gap_type"],
            description=g["description"],
            severity=g.get("severity", "MEDIUM"),
            owner=g.get("owner", ""),
            recommended_actions=g.get("recommended_actions", []),
        )
        for g in args["gaps"]
    ]
    repairs = generate_repair_items(gaps)
    return {"repair_items": [r.model_dump() for r in repairs], "total": len(repairs)}


def _handle_generate_s360(args: dict[str, Any]) -> dict[str, Any]:
    items = [
        RepairItem(
            id=r["id"],
            title=r["title"],
            description=r["description"],
            service_name=r.get("service_name", ""),
            priority=r.get("priority", "MEDIUM"),
            s360_kpi_category=r.get("s360_kpi_category"),
        )
        for r in args["repair_items"]
    ]
    actions = generate_s360_kpi_actions(items)
    return {"s360_actions": [a.model_dump() for a in actions], "total": len(actions)}


def _handle_eval_automation(args: dict[str, Any]) -> dict[str, Any]:
    brain_intent = BrainIntentCategory(
        args.get("brain_intent", BrainIntentCategory.UNCLASSIFIED.value)
    )
    lid_status = ComplianceStatus(
        args.get("lid_status", ComplianceStatus.UNKNOWN.value)
    )
    result = evaluate_automation_readiness(
        signal_id=args["signal_id"],
        signal_type=SignalType(args["signal_type"]),
        brain_aware=args.get("brain_aware", False),
        brain_intent=brain_intent,
        lid_status=lid_status,
        quality_score=args.get("quality_score", 0.0),
        aod_enabled=args.get("aod_enabled", False),
    )
    return result.model_dump()


def _handle_generate_template(args: dict[str, Any]) -> dict[str, Any]:
    signal_type_raw = args.get("signal_type", "SLI")
    brain_intent_raw = args.get("brain_intent", "UNCLASSIFIED")
    return generate_sli_template(
        service_name=args["service_name"],
        cujo_id=args.get("cujo_id"),
        metric_namespace=args["metric_namespace"],
        metric_name=args["metric_name"],
        signal_type=SignalType(signal_type_raw),
        brain_intent=BrainIntentCategory(brain_intent_raw),
        dimensions=args.get("dimensions", []),
        suggested_threshold=args.get("suggested_threshold"),
        window_minutes=args.get("window_minutes", 60),
    )


def _handle_service_summary(args: dict[str, Any]) -> dict[str, Any]:
    service_name = args["service_name"]
    sli_raw = args.get("slis", [])
    monitor_raw = args.get("monitors", [])
    cujo_ids = args.get("cujo_ids", [])

    slis = [
        SLI(
            id=s.get("id", f"sli-{i}"),
            name=s.get("name", f"sli-{i}"),
            service_name=service_name,
            cujo_ids=s.get("cujo_ids", []),
            brain_aware=s.get("brain_aware", False),
            lid_compliant=s.get("lid_compliant", False),
            aod_enabled=s.get("aod_enabled", False),
            quality_scores=s.get("quality_scores", {}),
        )
        for i, s in enumerate(sli_raw)
    ]

    sli_cujos = {c for s in slis for c in s.cujo_ids}
    cujos_without_sli = [c for c in cujo_ids if c not in sli_cujos]

    total = len(slis) + len(monitor_raw)
    lid_count = sum(1 for s in slis if s.lid_compliant)
    brain_count = sum(1 for s in slis if s.brain_aware)
    aod_count = sum(1 for s in slis if s.aod_enabled)
    quality_ready = sum(
        1 for s in slis if s.quality_scores.get("overall", 0.0) >= 0.7
    )

    open_repairs = args.get("open_repair_items", 0)
    open_s360 = args.get("open_s360_actions", 0)

    health_components = [
        (lid_count / len(slis)) if slis else 0.0,
        (brain_count / len(slis)) if slis else 0.0,
        (quality_ready / len(slis)) if slis else 0.0,
        1.0 - min(1.0, (open_repairs + open_s360) / max(1, total)),
    ]
    health_score = round(sum(health_components) / len(health_components), 2)

    priorities: list[str] = []
    if cujos_without_sli:
        priorities.append(
            f"Author SLIs for {len(cujos_without_sli)} uncovered CUJO(s): "
            + ", ".join(cujos_without_sli[:3])
            + ("..." if len(cujos_without_sli) > 3 else "")
        )
    if lid_count < len(slis):
        priorities.append(
            f"Fix LID compliance for {len(slis) - lid_count} SLI(s)."
        )
    if brain_count < len(slis):
        priorities.append(
            f"Enable Brain awareness for {len(slis) - brain_count} SLI(s)."
        )
    if open_repairs > 0:
        priorities.append(f"Resolve {open_repairs} open repair item(s).")
    if open_s360 > 0:
        priorities.append(f"Close {open_s360} open S360 KPI action(s).")

    return {
        "service_name": service_name,
        "total_slis": len(slis),
        "total_monitors": len(monitor_raw),
        "lid_compliant_count": lid_count,
        "brain_aware_count": brain_count,
        "aod_enabled_count": aod_count,
        "open_repair_items": open_repairs,
        "open_s360_actions": open_s360,
        "coverage_gaps": len(cujos_without_sli),
        "automation_ready_signals": quality_ready,
        "overall_health_score": health_score,
        "top_priorities": priorities,
        "cujos_without_sli": cujos_without_sli,
    }


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------


def main() -> None:
    import asyncio

    asyncio.run(_run())


async def _run() -> None:
    async with mcp.server.stdio.stdio_server() as (read_stream, write_stream):
        await app.run(
            read_stream,
            write_stream,
            app.create_initialization_options(),
        )


if __name__ == "__main__":
    main()
