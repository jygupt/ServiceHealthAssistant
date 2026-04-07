"""
Data models for the Service Health Assistant MCP Agent.

These models represent the core domain objects used throughout the SRE workflows:
SLIs, Service Monitors, LID compliance, Brain Intent, coverage gaps,
repair items, and S360 KPI actions.
"""

from __future__ import annotations

from enum import Enum
from typing import Any

from pydantic import BaseModel, Field


# ---------------------------------------------------------------------------
# Enumerations
# ---------------------------------------------------------------------------


class SignalType(str, Enum):
    SLI = "SLI"
    SERVICE_MONITOR = "SERVICE_MONITOR"
    UNKNOWN = "UNKNOWN"


class BrainIntentCategory(str, Enum):
    CUSTOMER_IMPACT = "CUSTOMER_IMPACT"
    OPERATIONAL_INFRASTRUCTURE = "OPERATIONAL_INFRASTRUCTURE"
    UNCLASSIFIED = "UNCLASSIFIED"


class ComplianceStatus(str, Enum):
    COMPLIANT = "COMPLIANT"
    NON_COMPLIANT = "NON_COMPLIANT"
    PARTIAL = "PARTIAL"
    UNKNOWN = "UNKNOWN"


class GapType(str, Enum):
    DETECTION_GAP = "DETECTION_GAP"
    SIGNAL_QUALITY_GAP = "SIGNAL_QUALITY_GAP"
    AUTOMATION_READINESS_GAP = "AUTOMATION_READINESS_GAP"


class RepairPriority(str, Enum):
    CRITICAL = "CRITICAL"
    HIGH = "HIGH"
    MEDIUM = "MEDIUM"
    LOW = "LOW"


class RepairStatus(str, Enum):
    OPEN = "OPEN"
    IN_PROGRESS = "IN_PROGRESS"
    RESOLVED = "RESOLVED"
    WONT_FIX = "WONT_FIX"


class AutomationReadinessLevel(str, Enum):
    READY = "READY"
    CONDITIONALLY_READY = "CONDITIONALLY_READY"
    NOT_READY = "NOT_READY"
    BLOCKED = "BLOCKED"


class S360KPICategory(str, Enum):
    LID = "LID"
    QUALITY = "QUALITY"
    COVERAGE = "COVERAGE"
    AUTOMATION = "AUTOMATION"


class SLIQualityDimension(str, Enum):
    MEASURABILITY = "MEASURABILITY"
    SENSITIVITY = "SENSITIVITY"
    RELEVANCE = "RELEVANCE"


# ---------------------------------------------------------------------------
# Core domain models
# ---------------------------------------------------------------------------


class MetricDimension(BaseModel):
    name: str
    values: list[str] = Field(default_factory=list)
    required: bool = False
    present_in_mdm: bool = False


class SLI(BaseModel):
    """Represents a Service Level Indicator."""

    id: str
    name: str
    service_name: str
    description: str = ""
    metric_namespace: str = ""
    metric_name: str = ""
    kql_query: str = ""
    dimensions: list[MetricDimension] = Field(default_factory=list)
    threshold: float | None = None
    window_minutes: int = 60
    brain_intent: BrainIntentCategory = BrainIntentCategory.UNCLASSIFIED
    brain_aware: bool = False
    aod_enabled: bool = False
    lid_compliant: bool = False
    cujo_ids: list[str] = Field(default_factory=list)
    tags: dict[str, str] = Field(default_factory=dict)
    owner: str = ""
    quality_scores: dict[str, float] = Field(default_factory=dict)


class ServiceMonitor(BaseModel):
    """Represents a Service Monitor (lower-fidelity than SLI)."""

    id: str
    name: str
    service_name: str
    description: str = ""
    metric_namespace: str = ""
    metric_name: str = ""
    kql_query: str = ""
    dimensions: list[MetricDimension] = Field(default_factory=list)
    threshold: float | None = None
    window_minutes: int = 5
    brain_intent: BrainIntentCategory = BrainIntentCategory.UNCLASSIFIED
    brain_aware: bool = False
    sli_promotion_eligible: bool = False
    owner: str = ""
    tags: dict[str, str] = Field(default_factory=dict)


class LIDComplianceResult(BaseModel):
    """Result of a LID (Latency, Impact, Dependency) compliance check."""

    signal_id: str
    signal_type: SignalType
    status: ComplianceStatus
    latency_present: bool = False
    impact_present: bool = False
    dependency_dimensions: list[str] = Field(default_factory=list)
    missing_dimensions: list[str] = Field(default_factory=list)
    score: float = 0.0  # 0.0 – 1.0
    recommendations: list[str] = Field(default_factory=list)


class BrainIntentResult(BaseModel):
    """Result of a Brain Intent classification or validation."""

    signal_id: str
    signal_type: SignalType
    classified_intent: BrainIntentCategory
    confidence: float = 0.0  # 0.0 – 1.0
    is_correct: bool = False
    rationale: str = ""
    recommendations: list[str] = Field(default_factory=list)


class SLIQualityScore(BaseModel):
    """Quality assessment for an SLI across measurability, sensitivity, relevance."""

    sli_id: str
    overall_score: float = 0.0  # 0.0 – 1.0
    dimension_scores: dict[str, float] = Field(default_factory=dict)
    noise_level: str = "UNKNOWN"  # LOW / MEDIUM / HIGH
    coverage_estimate: str = "UNKNOWN"
    precision_estimate: str = "UNKNOWN"
    blocking_issues: list[str] = Field(default_factory=list)
    recommendations: list[str] = Field(default_factory=list)
    publish_safe: bool = False


class CoverageGap(BaseModel):
    """A detected coverage gap tied to a CUJO or service area."""

    id: str
    service_name: str
    cujo_id: str | None = None
    gap_type: GapType
    description: str
    affected_signals: list[str] = Field(default_factory=list)
    severity: RepairPriority = RepairPriority.MEDIUM
    owner: str = ""
    recommended_actions: list[str] = Field(default_factory=list)


class RepairItem(BaseModel):
    """An actionable repair item derived from a gap or compliance violation."""

    id: str
    title: str
    description: str
    gap_id: str | None = None
    signal_id: str | None = None
    service_name: str = ""
    priority: RepairPriority = RepairPriority.MEDIUM
    status: RepairStatus = RepairStatus.OPEN
    s360_kpi_category: S360KPICategory | None = None
    why_required: str = ""
    outcome_unblocked: str = ""
    owner: str = ""
    due_date: str | None = None
    steps: list[str] = Field(default_factory=list)


class S360KPIAction(BaseModel):
    """An S360 KPI action mapped from one or more repair items."""

    id: str
    title: str
    category: S360KPICategory
    description: str
    repair_item_ids: list[str] = Field(default_factory=list)
    service_name: str = ""
    status: RepairStatus = RepairStatus.OPEN
    why_required: str = ""
    outcome_unblocked: str = ""
    owner: str = ""
    due_date: str | None = None


class AutomationReadinessResult(BaseModel):
    """Evaluation of a signal's readiness for Brain/AOD automation."""

    signal_id: str
    signal_type: SignalType
    readiness_level: AutomationReadinessLevel
    brain_integration_valid: bool = False
    aod_eligible: bool = False
    auto_comms_ready: bool = False
    deployment_stop_ready: bool = False
    blocking_criteria: list[str] = Field(default_factory=list)
    remediation_steps: list[str] = Field(default_factory=list)
    rationale: str = ""


class ServiceHealthSummary(BaseModel):
    """High-level health summary for a service."""

    service_name: str
    total_slis: int = 0
    total_monitors: int = 0
    lid_compliant_count: int = 0
    brain_aware_count: int = 0
    aod_enabled_count: int = 0
    open_repair_items: int = 0
    open_s360_actions: int = 0
    coverage_gaps: int = 0
    automation_ready_signals: int = 0
    overall_health_score: float = 0.0  # 0.0 – 1.0
    top_priorities: list[str] = Field(default_factory=list)
    cujos_without_sli: list[str] = Field(default_factory=list)


class SignalClassificationRequest(BaseModel):
    """Input for the classify_signal tool."""

    service_name: str
    description: str
    cujo_id: str | None = None
    telemetry_type: str = ""
    has_customer_facing_impact: bool = False
    has_latency_metric: bool = False
    has_availability_metric: bool = False
    has_error_rate_metric: bool = False
    is_infrastructure_signal: bool = False
    existing_sli_count: int = 0
    brain_outage_mode_required: bool = False


class SLIAuthoringRequest(BaseModel):
    """Input for generating a starter SLI template."""

    service_name: str
    cujo_id: str | None = None
    metric_namespace: str
    metric_name: str
    signal_type: SignalType = SignalType.SLI
    brain_intent: BrainIntentCategory = BrainIntentCategory.UNCLASSIFIED
    dimensions: list[str] = Field(default_factory=list)
    suggested_threshold: float | None = None
    window_minutes: int = 60


class PreFlightValidationRequest(BaseModel):
    """Input for pre-publish validation."""

    signal_id: str
    signal_type: SignalType
    metric_namespace: str
    metric_name: str
    kql_query: str
    dimensions: list[MetricDimension] = Field(default_factory=list)
    brain_intent: BrainIntentCategory = BrainIntentCategory.UNCLASSIFIED
    lid_dimensions_present: list[str] = Field(default_factory=list)
    owner: str = ""


class PreFlightValidationResult(BaseModel):
    """Result of pre-publish validation."""

    signal_id: str
    passed: bool
    blocking_issues: list[str] = Field(default_factory=list)
    warnings: list[str] = Field(default_factory=list)
    lid_result: LIDComplianceResult | None = None
    brain_intent_result: BrainIntentResult | None = None
    quality_score: SLIQualityScore | None = None
    recommended_fixes: list[str] = Field(default_factory=list)
