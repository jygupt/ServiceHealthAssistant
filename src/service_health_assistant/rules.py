"""
Deterministic rule engine for Service Health governance.

All rules are deterministic — no speculative reasoning.  Each rule returns a
structured result that can be audited and traced back to a specific policy.
"""

from __future__ import annotations

import re
from typing import Any

from .models import (
    AutomationReadinessLevel,
    AutomationReadinessResult,
    BrainIntentCategory,
    BrainIntentResult,
    ComplianceStatus,
    CoverageGap,
    GapType,
    LIDComplianceResult,
    MetricDimension,
    PreFlightValidationRequest,
    PreFlightValidationResult,
    RepairItem,
    RepairPriority,
    RepairStatus,
    S360KPIAction,
    S360KPICategory,
    SLI,
    SLIQualityScore,
    ServiceMonitor,
    SignalClassificationRequest,
    SignalType,
)

# ---------------------------------------------------------------------------
# LID required dimension names (canonical, case-insensitive prefix match)
# ---------------------------------------------------------------------------
_LID_LATENCY_PATTERNS = re.compile(
    r"(latency|p50|p75|p90|p95|p99|duration|response_time)", re.IGNORECASE
)
_LID_IMPACT_PATTERNS = re.compile(
    r"(availability|success_rate|error_rate|failure_rate|throughput|rps|qps|impact)",
    re.IGNORECASE,
)
_LID_DEPENDENCY_PATTERNS = re.compile(
    r"(dependency|upstream|downstream|service|component|region|datacenter|cluster)",
    re.IGNORECASE,
)

# Minimum quality score to be considered publish-safe
_MIN_PUBLISH_SCORE = 0.70

# ---------------------------------------------------------------------------
# Signal classification
# ---------------------------------------------------------------------------


def classify_signal(req: SignalClassificationRequest) -> dict[str, Any]:
    """
    Deterministically decide whether a signal should be an SLI or a
    Service Monitor, and classify its Brain Intent.

    Rules:
    - SLI if: customer-facing impact OR has availability/latency/error-rate
      metrics AND a CUJO is provided.
    - Service Monitor if: infrastructure-only signal with no direct CUJO link.
    - Brain Intent = CUSTOMER_IMPACT if has_customer_facing_impact or
      has_availability/error_rate metrics.
    - Brain Intent = OPERATIONAL_INFRASTRUCTURE if is_infrastructure_signal.
    """
    signal_type: SignalType
    brain_intent: BrainIntentCategory
    rationale_parts: list[str] = []
    recommendations: list[str] = []

    # --- Signal type decision ---
    is_customer_signal = (
        req.has_customer_facing_impact
        or req.has_availability_metric
        or req.has_error_rate_metric
        or req.has_latency_metric
    )

    if is_customer_signal and req.cujo_id:
        signal_type = SignalType.SLI
        rationale_parts.append(
            "Signal has customer-facing metrics and is linked to a CUJO — use SLI."
        )
    elif is_customer_signal and not req.cujo_id:
        signal_type = SignalType.SLI
        rationale_parts.append(
            "Signal has customer-facing metrics but no CUJO. SLI recommended; "
            "link a CUJO before publishing."
        )
        recommendations.append(
            "Provide a CUJO ID to satisfy LID compliance and coverage requirements."
        )
    elif req.is_infrastructure_signal:
        signal_type = SignalType.SERVICE_MONITOR
        rationale_parts.append(
            "Infrastructure-only signal with no direct customer impact — use Service Monitor."
        )
    else:
        signal_type = SignalType.SLI
        rationale_parts.append(
            "Signal type is ambiguous. Defaulting to SLI for governance completeness. "
            "Review with service owner."
        )
        recommendations.append(
            "Clarify whether signal directly measures customer experience or is "
            "infrastructure-only."
        )

    # --- Brain Intent classification ---
    if req.has_customer_facing_impact or req.has_availability_metric or req.has_error_rate_metric:
        brain_intent = BrainIntentCategory.CUSTOMER_IMPACT
        rationale_parts.append("Customer-impact signals map to Brain Intent: CUSTOMER_IMPACT.")
    elif req.is_infrastructure_signal:
        # Only classify as OPERATIONAL_INFRASTRUCTURE when explicitly flagged as
        # infrastructure-only.  Latency metrics alone are insufficient, as API
        # latency is a customer-facing signal that belongs to CUSTOMER_IMPACT.
        brain_intent = BrainIntentCategory.OPERATIONAL_INFRASTRUCTURE
        rationale_parts.append(
            "Infrastructure-only signal maps to Brain Intent: OPERATIONAL_INFRASTRUCTURE."
        )
    else:
        brain_intent = BrainIntentCategory.UNCLASSIFIED
        rationale_parts.append(
            "Brain Intent could not be determined from provided metadata."
        )
        recommendations.append(
            "Manually classify Brain Intent as CUSTOMER_IMPACT or "
            "OPERATIONAL_INFRASTRUCTURE before publishing."
        )

    if req.brain_outage_mode_required and brain_intent != BrainIntentCategory.CUSTOMER_IMPACT:
        recommendations.append(
            "Outage-mode is required but Brain Intent is not CUSTOMER_IMPACT. "
            "Re-evaluate signal scope."
        )

    if signal_type == SignalType.SERVICE_MONITOR:
        recommendations.append(
            "Evaluate this Service Monitor for SLI promotion eligibility once "
            "metric quality is confirmed."
        )

    return {
        "signal_type": signal_type.value,
        "brain_intent": brain_intent.value,
        "rationale": " ".join(rationale_parts),
        "recommendations": recommendations,
    }


# ---------------------------------------------------------------------------
# LID compliance
# ---------------------------------------------------------------------------


def evaluate_lid_compliance(
    signal_id: str,
    signal_type: SignalType,
    dimensions: list[MetricDimension],
    kql_query: str = "",
) -> LIDComplianceResult:
    """
    Enforce LID (Latency, Impact, Dependency) compliance.

    A signal is compliant if it exposes at least one latency dimension,
    at least one impact dimension, and at least one dependency dimension,
    either via metric dimensions or via the KQL query text.
    """
    dim_names = [d.name for d in dimensions]
    combined_text = " ".join(dim_names) + " " + kql_query

    latency_present = bool(_LID_LATENCY_PATTERNS.search(combined_text))
    impact_present = bool(_LID_IMPACT_PATTERNS.search(combined_text))
    dependency_dims = [
        n for n in dim_names if _LID_DEPENDENCY_PATTERNS.search(n)
    ]
    dependency_present = bool(dependency_dims) or bool(
        _LID_DEPENDENCY_PATTERNS.search(kql_query)
    )

    missing: list[str] = []
    if not latency_present:
        missing.append("Latency dimension (p50/p95/p99/duration)")
    if not impact_present:
        missing.append("Impact dimension (availability/error_rate/success_rate)")
    if not dependency_present:
        missing.append("Dependency dimension (service/region/cluster)")

    score = (
        (1.0 if latency_present else 0.0)
        + (1.0 if impact_present else 0.0)
        + (1.0 if dependency_present else 0.0)
    ) / 3.0

    if score == 1.0:
        status = ComplianceStatus.COMPLIANT
    elif score > 0.0:
        status = ComplianceStatus.PARTIAL
    else:
        status = ComplianceStatus.NON_COMPLIANT

    recommendations: list[str] = []
    for m in missing:
        recommendations.append(f"Add required LID dimension: {m}")
    if status != ComplianceStatus.COMPLIANT:
        recommendations.append(
            "LID compliance is required for Brain Intent validation and S360 KPI health."
        )

    return LIDComplianceResult(
        signal_id=signal_id,
        signal_type=signal_type,
        status=status,
        latency_present=latency_present,
        impact_present=impact_present,
        dependency_dimensions=dependency_dims,
        missing_dimensions=missing,
        score=round(score, 2),
        recommendations=recommendations,
    )


# ---------------------------------------------------------------------------
# Brain Intent validation
# ---------------------------------------------------------------------------


def validate_brain_intent(
    signal_id: str,
    signal_type: SignalType,
    declared_intent: BrainIntentCategory,
    has_customer_facing_impact: bool = False,
    is_infrastructure_signal: bool = False,
    description: str = "",
) -> BrainIntentResult:
    """
    Validate that the declared Brain Intent is correct given signal metadata.
    Returns confidence and correctness assessment.
    """
    expected_intent: BrainIntentCategory
    recommendations: list[str] = []

    if has_customer_facing_impact:
        expected_intent = BrainIntentCategory.CUSTOMER_IMPACT
    elif is_infrastructure_signal:
        expected_intent = BrainIntentCategory.OPERATIONAL_INFRASTRUCTURE
    else:
        # Use description heuristics
        desc_lower = description.lower()
        customer_words = {"customer", "user", "availability", "latency", "error", "failure"}
        infra_words = {"cpu", "memory", "disk", "host", "pod", "node", "infra", "infrastructure"}
        if any(w in desc_lower for w in customer_words):
            expected_intent = BrainIntentCategory.CUSTOMER_IMPACT
        elif any(w in desc_lower for w in infra_words):
            expected_intent = BrainIntentCategory.OPERATIONAL_INFRASTRUCTURE
        else:
            expected_intent = BrainIntentCategory.UNCLASSIFIED

    is_correct = declared_intent == expected_intent and declared_intent != BrainIntentCategory.UNCLASSIFIED
    confidence = 1.0 if is_correct else (0.5 if expected_intent == BrainIntentCategory.UNCLASSIFIED else 0.0)

    if declared_intent == BrainIntentCategory.UNCLASSIFIED:
        recommendations.append(
            "Brain Intent is UNCLASSIFIED. Classify as CUSTOMER_IMPACT or "
            "OPERATIONAL_INFRASTRUCTURE before publishing."
        )
    if not is_correct and expected_intent != BrainIntentCategory.UNCLASSIFIED:
        recommendations.append(
            f"Declared intent '{declared_intent.value}' does not match expected "
            f"'{expected_intent.value}' based on signal metadata. Correct it to align "
            "with Brain outage-mode principles."
        )
    if declared_intent == BrainIntentCategory.CUSTOMER_IMPACT:
        recommendations.append(
            "Ensure Brain outage-mode is enabled for CUSTOMER_IMPACT signals to "
            "support AOD and auto-comms."
        )

    rationale = (
        f"Expected intent: {expected_intent.value}. "
        f"Declared intent: {declared_intent.value}. "
        f"Correct: {is_correct}."
    )

    return BrainIntentResult(
        signal_id=signal_id,
        signal_type=signal_type,
        classified_intent=expected_intent,
        confidence=confidence,
        is_correct=is_correct,
        rationale=rationale,
        recommendations=recommendations,
    )


# ---------------------------------------------------------------------------
# SLI quality scoring
# ---------------------------------------------------------------------------


def score_sli_quality(
    sli_id: str,
    metric_namespace: str,
    metric_name: str,
    kql_query: str,
    dimensions: list[MetricDimension],
    threshold: float | None,
    window_minutes: int,
    lid_result: LIDComplianceResult,
    brain_intent_result: BrainIntentResult,
) -> SLIQualityScore:
    """
    Score SLI quality across Measurability, Sensitivity, and Relevance.

    Measurability: metric namespace/name present, KQL non-empty, MDM-available dimensions.
    Sensitivity:   threshold set, reasonable window, low noise estimate.
    Relevance:     LID compliant, Brain Intent correct, CUJO linked (via LID dims).
    """
    blocking_issues: list[str] = []
    recommendations: list[str] = []

    # Measurability
    measurability = 0.0
    if metric_namespace:
        measurability += 0.33
    else:
        blocking_issues.append("Metric namespace is missing.")
    if metric_name:
        measurability += 0.33
    else:
        blocking_issues.append("Metric name is missing.")
    if kql_query:
        measurability += 0.34
    else:
        recommendations.append("KQL query is empty; provide a validated query.")

    mdm_dims = [d for d in dimensions if d.present_in_mdm]
    if dimensions and not mdm_dims:
        recommendations.append(
            "No dimensions are confirmed present in MDM. Validate dimensional readiness."
        )

    # Sensitivity
    sensitivity = 0.5  # baseline
    if threshold is None:
        sensitivity -= 0.25
        recommendations.append("No threshold defined. Set an appropriate SLO threshold.")
    if window_minutes < 1 or window_minutes > 1440:
        sensitivity -= 0.25
        recommendations.append(
            "Evaluation window is outside recommended range (1–1440 minutes)."
        )
    noise_level = "LOW" if sensitivity >= 0.5 else "MEDIUM"

    # Relevance
    relevance = lid_result.score * 0.6
    if brain_intent_result.is_correct:
        relevance += 0.4
    else:
        recommendations.append(
            "Brain Intent is incorrect or unclassified, reducing relevance score."
        )

    overall = round((measurability + sensitivity + relevance) / 3.0, 2)

    coverage_estimate = "HIGH" if overall >= 0.8 else ("MEDIUM" if overall >= 0.5 else "LOW")
    precision_estimate = "HIGH" if threshold is not None and lid_result.score >= 0.67 else "LOW"

    publish_safe = overall >= _MIN_PUBLISH_SCORE and not blocking_issues

    return SLIQualityScore(
        sli_id=sli_id,
        overall_score=overall,
        dimension_scores={
            "measurability": round(measurability, 2),
            "sensitivity": round(sensitivity, 2),
            "relevance": round(relevance, 2),
        },
        noise_level=noise_level,
        coverage_estimate=coverage_estimate,
        precision_estimate=precision_estimate,
        blocking_issues=blocking_issues,
        recommendations=recommendations,
        publish_safe=publish_safe,
    )


# ---------------------------------------------------------------------------
# Pre-flight validation
# ---------------------------------------------------------------------------


def run_preflight_validation(req: PreFlightValidationRequest) -> PreFlightValidationResult:
    """Run all pre-publish validation checks for a signal."""
    blocking_issues: list[str] = []
    warnings: list[str] = []
    recommended_fixes: list[str] = []

    lid_result = evaluate_lid_compliance(
        signal_id=req.signal_id,
        signal_type=req.signal_type,
        dimensions=req.dimensions,
        kql_query=req.kql_query,
    )

    brain_result = validate_brain_intent(
        signal_id=req.signal_id,
        signal_type=req.signal_type,
        declared_intent=req.brain_intent,
        description=req.kql_query,
    )

    quality_score: SLIQualityScore | None = None
    if req.signal_type == SignalType.SLI:
        quality_score = score_sli_quality(
            sli_id=req.signal_id,
            metric_namespace=req.metric_namespace,
            metric_name=req.metric_name,
            kql_query=req.kql_query,
            dimensions=req.dimensions,
            threshold=None,
            window_minutes=60,
            lid_result=lid_result,
            brain_intent_result=brain_result,
        )
        blocking_issues.extend(quality_score.blocking_issues)
        recommended_fixes.extend(quality_score.recommendations)

    if lid_result.status == ComplianceStatus.NON_COMPLIANT:
        blocking_issues.append(
            f"LID compliance failure: missing {', '.join(lid_result.missing_dimensions)}."
        )
        recommended_fixes.extend(lid_result.recommendations)
    elif lid_result.status == ComplianceStatus.PARTIAL:
        warnings.append(
            f"Partial LID compliance (score={lid_result.score}): "
            f"missing {', '.join(lid_result.missing_dimensions)}."
        )

    if not brain_result.is_correct:
        warnings.append(
            f"Brain Intent issue: {brain_result.rationale}"
        )
        recommended_fixes.extend(brain_result.recommendations)
    if req.brain_intent == BrainIntentCategory.UNCLASSIFIED:
        blocking_issues.append(
            "Brain Intent is UNCLASSIFIED. Classify before publishing."
        )

    if not req.owner:
        warnings.append("No owner specified. Assign an owner for auditability.")

    passed = not blocking_issues

    return PreFlightValidationResult(
        signal_id=req.signal_id,
        passed=passed,
        blocking_issues=blocking_issues,
        warnings=warnings,
        lid_result=lid_result,
        brain_intent_result=brain_result,
        quality_score=quality_score,
        recommended_fixes=list(dict.fromkeys(recommended_fixes)),  # deduplicate
    )


# ---------------------------------------------------------------------------
# Coverage gap detection
# ---------------------------------------------------------------------------


def detect_coverage_gaps(
    service_name: str,
    cujo_ids: list[str],
    slis: list[SLI],
    monitors: list[ServiceMonitor],
) -> list[CoverageGap]:
    """
    Identify CUJOs without SLI coverage, low-confidence monitors, and
    automation readiness gaps.
    """
    gaps: list[CoverageGap] = []
    sli_cujos: set[str] = {c for s in slis for c in s.cujo_ids}

    # Detection gaps — CUJOs with no SLI
    for cujo in cujo_ids:
        if cujo not in sli_cujos:
            gaps.append(
                CoverageGap(
                    id=f"gap-detect-{cujo}",
                    service_name=service_name,
                    cujo_id=cujo,
                    gap_type=GapType.DETECTION_GAP,
                    description=f"CUJO '{cujo}' has no SLI coverage.",
                    severity=RepairPriority.HIGH,
                    recommended_actions=[
                        f"Author an SLI for CUJO '{cujo}'.",
                        "Validate metric availability in MDM before authoring.",
                    ],
                )
            )

    # Signal quality gaps — SLIs with poor quality score
    for sli in slis:
        score = sli.quality_scores.get("overall", None)
        if score is not None and score < _MIN_PUBLISH_SCORE:
            gaps.append(
                CoverageGap(
                    id=f"gap-quality-{sli.id}",
                    service_name=service_name,
                    cujo_id=sli.cujo_ids[0] if sli.cujo_ids else None,
                    gap_type=GapType.SIGNAL_QUALITY_GAP,
                    description=(
                        f"SLI '{sli.name}' has quality score {score:.2f} "
                        f"(below threshold {_MIN_PUBLISH_SCORE})."
                    ),
                    affected_signals=[sli.id],
                    severity=RepairPriority.HIGH if score < 0.5 else RepairPriority.MEDIUM,
                    recommended_actions=[
                        "Improve LID compliance.",
                        "Validate and correct Brain Intent.",
                        "Set a measurable threshold.",
                    ],
                )
            )

    # Automation readiness gaps — SLIs without Brain awareness
    for sli in slis:
        if not sli.brain_aware:
            gaps.append(
                CoverageGap(
                    id=f"gap-auto-{sli.id}",
                    service_name=service_name,
                    cujo_id=sli.cujo_ids[0] if sli.cujo_ids else None,
                    gap_type=GapType.AUTOMATION_READINESS_GAP,
                    description=(
                        f"SLI '{sli.name}' is not Brain-aware, blocking AOD and auto-comms."
                    ),
                    affected_signals=[sli.id],
                    severity=RepairPriority.MEDIUM,
                    recommended_actions=[
                        "Enable Brain integration for this SLI.",
                        "Validate Brain Intent is CUSTOMER_IMPACT before enabling AOD.",
                    ],
                )
            )

    # Monitors eligible for SLI promotion
    for monitor in monitors:
        if monitor.sli_promotion_eligible:
            gaps.append(
                CoverageGap(
                    id=f"gap-promote-{monitor.id}",
                    service_name=service_name,
                    gap_type=GapType.SIGNAL_QUALITY_GAP,
                    description=(
                        f"Service Monitor '{monitor.name}' is eligible for SLI promotion "
                        "but has not been promoted."
                    ),
                    affected_signals=[monitor.id],
                    severity=RepairPriority.LOW,
                    recommended_actions=[
                        f"Promote Service Monitor '{monitor.name}' to an SLI.",
                        "Validate LID compliance and Brain Intent after promotion.",
                    ],
                )
            )

    return gaps


# ---------------------------------------------------------------------------
# Repair item generation
# ---------------------------------------------------------------------------


def generate_repair_items(gaps: list[CoverageGap]) -> list[RepairItem]:
    """Translate coverage gaps and compliance violations into repair items."""
    repairs: list[RepairItem] = []
    for gap in gaps:
        kpi_category: S360KPICategory
        if gap.gap_type == GapType.DETECTION_GAP:
            kpi_category = S360KPICategory.COVERAGE
        elif gap.gap_type == GapType.SIGNAL_QUALITY_GAP:
            kpi_category = S360KPICategory.QUALITY
        else:
            kpi_category = S360KPICategory.AUTOMATION

        repairs.append(
            RepairItem(
                id=f"repair-{gap.id}",
                title=f"[{gap.gap_type.value}] {gap.description[:80]}",
                description=gap.description,
                gap_id=gap.id,
                service_name=gap.service_name,
                priority=gap.severity,
                status=RepairStatus.OPEN,
                s360_kpi_category=kpi_category,
                why_required=(
                    "This gap directly impacts S360 KPI health and may block "
                    "automation enablement (AOD, auto-comms, deployment stops)."
                ),
                outcome_unblocked=(
                    "Resolving this repair improves reliability coverage, "
                    "signal quality, and governance compliance."
                ),
                owner=gap.owner,
                steps=gap.recommended_actions,
            )
        )
    return repairs


# ---------------------------------------------------------------------------
# S360 KPI action generation
# ---------------------------------------------------------------------------


def generate_s360_kpi_actions(repair_items: list[RepairItem]) -> list[S360KPIAction]:
    """Group repair items into S360 KPI actions by category."""
    category_map: dict[S360KPICategory, list[RepairItem]] = {}
    for item in repair_items:
        if item.s360_kpi_category:
            category_map.setdefault(item.s360_kpi_category, []).append(item)

    actions: list[S360KPIAction] = []
    for category, items in category_map.items():
        services = list({i.service_name for i in items if i.service_name})
        service_name = services[0] if len(services) == 1 else "multiple-services"
        actions.append(
            S360KPIAction(
                id=f"s360-{category.value.lower()}-{service_name}",
                title=f"S360 KPI: {category.value} improvement for {service_name}",
                category=category,
                description=(
                    f"Address {len(items)} repair item(s) in the {category.value} "
                    "category to improve S360 KPI health."
                ),
                repair_item_ids=[i.id for i in items],
                service_name=service_name,
                status=RepairStatus.OPEN,
                why_required=(
                    f"Unresolved {category.value} gaps directly lower the S360 KPI score "
                    "and may block governance approvals."
                ),
                outcome_unblocked=(
                    "Closing this KPI action will improve the service's reliability "
                    "posture and unblock automation readiness milestones."
                ),
            )
        )
    return actions


# ---------------------------------------------------------------------------
# Automation readiness evaluation
# ---------------------------------------------------------------------------


def evaluate_automation_readiness(
    signal_id: str,
    signal_type: SignalType,
    brain_aware: bool,
    brain_intent: BrainIntentCategory,
    lid_status: ComplianceStatus,
    quality_score: float,
    aod_enabled: bool = False,
) -> AutomationReadinessResult:
    """
    Evaluate a signal's readiness for Brain/AOD automation.

    Safety gates (all must pass for READY):
    1. Brain-aware
    2. Brain Intent = CUSTOMER_IMPACT (for AOD)
    3. LID fully compliant
    4. Quality score >= 0.70
    """
    blocking: list[str] = []
    remediation: list[str] = []

    if not brain_aware:
        blocking.append("Signal is not Brain-aware.")
        remediation.append("Enable Brain integration for this signal.")

    brain_intent_valid = brain_intent == BrainIntentCategory.CUSTOMER_IMPACT
    if not brain_intent_valid:
        blocking.append(
            f"Brain Intent is '{brain_intent.value}', not CUSTOMER_IMPACT. "
            "AOD requires CUSTOMER_IMPACT."
        )
        remediation.append("Set Brain Intent to CUSTOMER_IMPACT and validate correctness.")

    if lid_status != ComplianceStatus.COMPLIANT:
        blocking.append(f"LID compliance status is '{lid_status.value}', not COMPLIANT.")
        remediation.append(
            "Achieve full LID compliance (Latency, Impact, Dependency dimensions)."
        )

    if quality_score < _MIN_PUBLISH_SCORE:
        blocking.append(
            f"Quality score {quality_score:.2f} is below the minimum {_MIN_PUBLISH_SCORE}."
        )
        remediation.append("Improve SLI quality score to at least 0.70 before enabling AOD.")

    aod_eligible = not blocking
    auto_comms_ready = aod_eligible  # same criteria for now
    deployment_stop_ready = aod_eligible and aod_enabled

    if not blocking:
        readiness_level = AutomationReadinessLevel.READY
    elif not brain_aware:
        # Brain awareness is a fundamental prerequisite — always BLOCKED without it,
        # regardless of how many other criteria are satisfied.
        readiness_level = AutomationReadinessLevel.BLOCKED
    elif len(blocking) == 1:
        # brain_aware is True here (caught above), so the single remaining issue
        # is one of: intent, LID, or quality — conditionally addressable.
        readiness_level = AutomationReadinessLevel.CONDITIONALLY_READY
    else:
        readiness_level = AutomationReadinessLevel.NOT_READY

    rationale = (
        f"Signal has {len(blocking)} blocking issue(s). "
        "All safety criteria must pass before enabling automation."
    )

    return AutomationReadinessResult(
        signal_id=signal_id,
        signal_type=signal_type,
        readiness_level=readiness_level,
        brain_integration_valid=brain_aware,
        aod_eligible=aod_eligible,
        auto_comms_ready=auto_comms_ready,
        deployment_stop_ready=deployment_stop_ready,
        blocking_criteria=blocking,
        remediation_steps=remediation,
        rationale=rationale,
    )


# ---------------------------------------------------------------------------
# SLI template generation
# ---------------------------------------------------------------------------


def generate_sli_template(
    service_name: str,
    cujo_id: str | None,
    metric_namespace: str,
    metric_name: str,
    signal_type: SignalType,
    brain_intent: BrainIntentCategory,
    dimensions: list[str],
    suggested_threshold: float | None,
    window_minutes: int,
) -> dict[str, Any]:
    """
    Generate a starter SLI/ServiceMonitor template with KQL, dimensions,
    and threshold placeholders.
    """
    dim_clauses = " ".join(
        f"| where {d} == \"<{d}_value>\"" for d in dimensions
    )

    kql = (
        f"{metric_namespace}\n"
        f"| where MetricName == \"{metric_name}\"\n"
    )
    if dim_clauses:
        kql += dim_clauses + "\n"
    kql += (
        f"| summarize Value = avg(Value) by bin(Timestamp, {window_minutes}m)\n"
        f"| where Value {'<' if signal_type == SignalType.SLI else '>'} "
        f"{suggested_threshold if suggested_threshold is not None else '<threshold>'}"
    )

    lid_dim_names = []
    existing_lower = {d.lower() for d in dimensions}
    if not any(_LID_LATENCY_PATTERNS.search(d) for d in dimensions):
        lid_dim_names.append("Latency_P99")
    if not any(_LID_IMPACT_PATTERNS.search(d) for d in dimensions):
        lid_dim_names.append("AvailabilityRate")
    if not any(_LID_DEPENDENCY_PATTERNS.search(d) for d in dimensions):
        lid_dim_names.append("ServiceName")

    all_dimensions = list(dimensions) + lid_dim_names

    template = {
        "type": signal_type.value,
        "name": f"{service_name}_{metric_name}_{signal_type.value.lower()}",
        "service_name": service_name,
        "cujo_id": cujo_id or "<REQUIRED>",
        "brain_intent": brain_intent.value,
        "metric_namespace": metric_namespace,
        "metric_name": metric_name,
        "kql_query": kql,
        "dimensions": all_dimensions,
        "threshold": suggested_threshold if suggested_threshold is not None else "<set_threshold>",
        "window_minutes": window_minutes,
        "owner": "<team_alias>",
        "notes": (
            "Review and validate all <placeholder> values before publishing. "
            "Ensure dimensions are confirmed present in MDM. "
            "Run pre-flight validation before onboarding."
        ),
        "lid_dimensions_added": lid_dim_names,
    }
    return template
