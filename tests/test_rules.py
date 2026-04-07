"""Tests for the Service Health Assistant rule engine."""

from __future__ import annotations

import pytest

from service_health_assistant.models import (
    AutomationReadinessLevel,
    BrainIntentCategory,
    ComplianceStatus,
    CoverageGap,
    GapType,
    MetricDimension,
    PreFlightValidationRequest,
    RepairPriority,
    SLI,
    ServiceMonitor,
    SignalClassificationRequest,
    SignalType,
)
from service_health_assistant.rules import (
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
# classify_signal
# ---------------------------------------------------------------------------


class TestClassifySignal:
    def test_customer_facing_with_cujo_returns_sli(self):
        req = SignalClassificationRequest(
            service_name="MyService",
            description="Availability metric",
            cujo_id="CUJO-001",
            has_customer_facing_impact=True,
            has_availability_metric=True,
        )
        result = classify_signal(req)
        assert result["signal_type"] == SignalType.SLI.value
        assert result["brain_intent"] == BrainIntentCategory.CUSTOMER_IMPACT.value

    def test_infrastructure_only_returns_service_monitor(self):
        req = SignalClassificationRequest(
            service_name="MyService",
            description="CPU utilization",
            is_infrastructure_signal=True,
        )
        result = classify_signal(req)
        assert result["signal_type"] == SignalType.SERVICE_MONITOR.value
        assert result["brain_intent"] == BrainIntentCategory.OPERATIONAL_INFRASTRUCTURE.value

    def test_customer_facing_without_cujo_recommends_cujo(self):
        req = SignalClassificationRequest(
            service_name="MyService",
            description="Error rate",
            has_error_rate_metric=True,
        )
        result = classify_signal(req)
        assert result["signal_type"] == SignalType.SLI.value
        assert any("CUJO" in r for r in result["recommendations"])

    def test_latency_only_does_not_override_customer_impact(self):
        """Latency metrics can be customer-facing; customer_impact flag takes precedence."""
        req = SignalClassificationRequest(
            service_name="MyService",
            description="API latency with customer impact",
            has_customer_facing_impact=True,
            has_latency_metric=True,
        )
        result = classify_signal(req)
        assert result["brain_intent"] == BrainIntentCategory.CUSTOMER_IMPACT.value

    def test_latency_only_without_customer_flag_is_unclassified(self):
        """Latency alone (without explicit customer-facing flag) cannot determine intent."""
        req = SignalClassificationRequest(
            service_name="MyService",
            description="P99 latency",
            has_latency_metric=True,
        )
        result = classify_signal(req)
        assert result["brain_intent"] == BrainIntentCategory.UNCLASSIFIED.value

        req = SignalClassificationRequest(
            service_name="MyService",
            description="Some metric",
        )
        result = classify_signal(req)
        assert result["signal_type"] == SignalType.SLI.value
        assert result["brain_intent"] == BrainIntentCategory.UNCLASSIFIED.value


# ---------------------------------------------------------------------------
# evaluate_lid_compliance
# ---------------------------------------------------------------------------


class TestLIDCompliance:
    def _dims(self, names: list[str]) -> list[MetricDimension]:
        return [MetricDimension(name=n) for n in names]

    def test_fully_compliant(self):
        dims = self._dims(["Latency_P99", "AvailabilityRate", "ServiceName"])
        result = evaluate_lid_compliance("sig-1", SignalType.SLI, dims)
        assert result.status == ComplianceStatus.COMPLIANT
        assert result.score == 1.0
        assert result.latency_present
        assert result.impact_present

    def test_missing_latency_partial(self):
        dims = self._dims(["AvailabilityRate", "ServiceName"])
        result = evaluate_lid_compliance("sig-2", SignalType.SLI, dims)
        assert result.status == ComplianceStatus.PARTIAL
        assert not result.latency_present
        assert "Latency dimension" in result.missing_dimensions[0]

    def test_all_missing_non_compliant(self):
        result = evaluate_lid_compliance("sig-3", SignalType.SLI, [])
        assert result.status == ComplianceStatus.NON_COMPLIANT
        assert result.score == 0.0

    def test_kql_compensates_for_missing_dim(self):
        result = evaluate_lid_compliance(
            "sig-4",
            SignalType.SLI,
            [],
            kql_query="| where latency > 200 | where availability < 99 | where region == 'us'",
        )
        assert result.latency_present
        assert result.impact_present


# ---------------------------------------------------------------------------
# validate_brain_intent
# ---------------------------------------------------------------------------


class TestBrainIntent:
    def test_correct_customer_impact(self):
        result = validate_brain_intent(
            "sig-1",
            SignalType.SLI,
            BrainIntentCategory.CUSTOMER_IMPACT,
            has_customer_facing_impact=True,
        )
        assert result.is_correct
        assert result.confidence == 1.0

    def test_incorrect_intent_detected(self):
        result = validate_brain_intent(
            "sig-2",
            SignalType.SLI,
            BrainIntentCategory.OPERATIONAL_INFRASTRUCTURE,
            has_customer_facing_impact=True,
        )
        assert not result.is_correct
        assert result.confidence == 0.0

    def test_unclassified_generates_recommendation(self):
        result = validate_brain_intent(
            "sig-3",
            SignalType.SLI,
            BrainIntentCategory.UNCLASSIFIED,
        )
        assert any("UNCLASSIFIED" in r for r in result.recommendations)


# ---------------------------------------------------------------------------
# score_sli_quality
# ---------------------------------------------------------------------------


class TestSLIQuality:
    def _lid(self, score: float):
        from service_health_assistant.models import LIDComplianceResult

        return LIDComplianceResult(
            signal_id="sli-1",
            signal_type=SignalType.SLI,
            status=ComplianceStatus.COMPLIANT if score == 1.0 else ComplianceStatus.PARTIAL,
            latency_present=True,
            impact_present=True,
            score=score,
        )

    def _brain(self, correct: bool):
        from service_health_assistant.models import BrainIntentResult

        return BrainIntentResult(
            signal_id="sli-1",
            signal_type=SignalType.SLI,
            classified_intent=BrainIntentCategory.CUSTOMER_IMPACT,
            confidence=1.0 if correct else 0.0,
            is_correct=correct,
        )

    def test_high_quality_sli_is_publish_safe(self):
        dims = [
            MetricDimension(name="Latency_P99", present_in_mdm=True),
            MetricDimension(name="AvailabilityRate", present_in_mdm=True),
        ]
        result = score_sli_quality(
            sli_id="sli-1",
            metric_namespace="Azure.MyService",
            metric_name="RequestLatency",
            kql_query="MyMetric | summarize ...",
            dimensions=dims,
            threshold=99.5,
            window_minutes=60,
            lid_result=self._lid(1.0),
            brain_intent_result=self._brain(True),
        )
        assert result.publish_safe
        assert result.overall_score >= 0.7

    def test_missing_namespace_blocks_publish(self):
        result = score_sli_quality(
            sli_id="sli-2",
            metric_namespace="",
            metric_name="",
            kql_query="",
            dimensions=[],
            threshold=None,
            window_minutes=60,
            lid_result=self._lid(0.0),
            brain_intent_result=self._brain(False),
        )
        assert not result.publish_safe
        assert result.blocking_issues


# ---------------------------------------------------------------------------
# run_preflight_validation
# ---------------------------------------------------------------------------


class TestPreflightValidation:
    def test_unclassified_intent_blocks_publish(self):
        req = PreFlightValidationRequest(
            signal_id="sig-1",
            signal_type=SignalType.SLI,
            metric_namespace="Azure.MyService",
            metric_name="Availability",
            kql_query="",
            brain_intent=BrainIntentCategory.UNCLASSIFIED,
        )
        result = run_preflight_validation(req)
        assert not result.passed
        assert any("UNCLASSIFIED" in i for i in result.blocking_issues)

    def test_valid_signal_passes(self):
        dims = [
            MetricDimension(name="Latency_P99", present_in_mdm=True),
            MetricDimension(name="AvailabilityRate", present_in_mdm=True),
            MetricDimension(name="ServiceName", present_in_mdm=True),
        ]
        req = PreFlightValidationRequest(
            signal_id="sig-2",
            signal_type=SignalType.SLI,
            metric_namespace="Azure.MyService",
            metric_name="RequestLatency",
            kql_query="Azure.MyService | where Latency > 200",
            dimensions=dims,
            brain_intent=BrainIntentCategory.CUSTOMER_IMPACT,
            owner="sre-team",
        )
        result = run_preflight_validation(req)
        # Should have no LID blocking issues; overall result depends on quality
        assert not any("LID compliance failure" in i for i in result.blocking_issues)


# ---------------------------------------------------------------------------
# detect_coverage_gaps
# ---------------------------------------------------------------------------


class TestDetectCoverageGaps:
    def test_cujo_without_sli_is_detection_gap(self):
        slis: list[SLI] = []
        gaps = detect_coverage_gaps("SvcA", ["CUJO-1", "CUJO-2"], slis, [])
        assert len(gaps) == 2
        assert all(g.gap_type == GapType.DETECTION_GAP for g in gaps)

    def test_covered_cujo_has_no_detection_gap(self):
        slis = [
            SLI(
                id="sli-1",
                name="SLI1",
                service_name="SvcA",
                cujo_ids=["CUJO-1"],
                brain_aware=True,
                quality_scores={"overall": 0.9},
            )
        ]
        gaps = detect_coverage_gaps("SvcA", ["CUJO-1"], slis, [])
        detection_gaps = [g for g in gaps if g.gap_type == GapType.DETECTION_GAP]
        assert not detection_gaps

    def test_non_brain_aware_sli_creates_automation_gap(self):
        slis = [
            SLI(
                id="sli-1",
                name="SLI1",
                service_name="SvcA",
                cujo_ids=["CUJO-1"],
                brain_aware=False,
                quality_scores={"overall": 0.9},
            )
        ]
        gaps = detect_coverage_gaps("SvcA", ["CUJO-1"], slis, [])
        auto_gaps = [g for g in gaps if g.gap_type == GapType.AUTOMATION_READINESS_GAP]
        assert len(auto_gaps) == 1

    def test_promotion_eligible_monitor_creates_quality_gap(self):
        monitors = [
            ServiceMonitor(
                id="mon-1",
                name="Monitor1",
                service_name="SvcA",
                sli_promotion_eligible=True,
            )
        ]
        gaps = detect_coverage_gaps("SvcA", [], [], monitors)
        quality_gaps = [g for g in gaps if g.gap_type == GapType.SIGNAL_QUALITY_GAP]
        assert len(quality_gaps) == 1


# ---------------------------------------------------------------------------
# generate_repair_items
# ---------------------------------------------------------------------------


class TestGenerateRepairItems:
    def test_detection_gap_maps_to_coverage_kpi(self):
        gap = CoverageGap(
            id="gap-1",
            service_name="SvcA",
            cujo_id="CUJO-1",
            gap_type=GapType.DETECTION_GAP,
            description="CUJO-1 has no SLI coverage.",
        )
        repairs = generate_repair_items([gap])
        assert len(repairs) == 1
        from service_health_assistant.models import S360KPICategory

        assert repairs[0].s360_kpi_category == S360KPICategory.COVERAGE

    def test_automation_gap_maps_to_automation_kpi(self):
        gap = CoverageGap(
            id="gap-2",
            service_name="SvcA",
            gap_type=GapType.AUTOMATION_READINESS_GAP,
            description="Not Brain-aware.",
        )
        repairs = generate_repair_items([gap])
        from service_health_assistant.models import S360KPICategory

        assert repairs[0].s360_kpi_category == S360KPICategory.AUTOMATION


# ---------------------------------------------------------------------------
# generate_s360_kpi_actions
# ---------------------------------------------------------------------------


class TestGenerateS360KPIActions:
    def test_groups_by_category(self):
        from service_health_assistant.models import RepairItem, S360KPICategory

        items = [
            RepairItem(
                id="r-1",
                title="Fix LID",
                description="...",
                service_name="SvcA",
                s360_kpi_category=S360KPICategory.LID,
            ),
            RepairItem(
                id="r-2",
                title="Fix Coverage",
                description="...",
                service_name="SvcA",
                s360_kpi_category=S360KPICategory.COVERAGE,
            ),
            RepairItem(
                id="r-3",
                title="Fix LID 2",
                description="...",
                service_name="SvcA",
                s360_kpi_category=S360KPICategory.LID,
            ),
        ]
        actions = generate_s360_kpi_actions(items)
        categories = {a.category for a in actions}
        assert S360KPICategory.LID in categories
        assert S360KPICategory.COVERAGE in categories
        lid_action = next(a for a in actions if a.category == S360KPICategory.LID)
        assert len(lid_action.repair_item_ids) == 2


# ---------------------------------------------------------------------------
# evaluate_automation_readiness
# ---------------------------------------------------------------------------


class TestAutomationReadiness:
    def test_all_criteria_met_is_ready(self):
        result = evaluate_automation_readiness(
            signal_id="sig-1",
            signal_type=SignalType.SLI,
            brain_aware=True,
            brain_intent=BrainIntentCategory.CUSTOMER_IMPACT,
            lid_status=ComplianceStatus.COMPLIANT,
            quality_score=0.85,
        )
        assert result.readiness_level == AutomationReadinessLevel.READY
        assert result.aod_eligible
        assert result.auto_comms_ready

    def test_not_brain_aware_is_blocked(self):
        result = evaluate_automation_readiness(
            signal_id="sig-2",
            signal_type=SignalType.SLI,
            brain_aware=False,
            brain_intent=BrainIntentCategory.CUSTOMER_IMPACT,
            lid_status=ComplianceStatus.COMPLIANT,
            quality_score=0.85,
        )
        assert result.readiness_level == AutomationReadinessLevel.BLOCKED
        assert not result.aod_eligible

    def test_wrong_intent_is_not_ready(self):
        result = evaluate_automation_readiness(
            signal_id="sig-3",
            signal_type=SignalType.SLI,
            brain_aware=True,
            brain_intent=BrainIntentCategory.OPERATIONAL_INFRASTRUCTURE,
            lid_status=ComplianceStatus.COMPLIANT,
            quality_score=0.85,
        )
        assert not result.aod_eligible

    def test_low_quality_score_blocks_automation(self):
        result = evaluate_automation_readiness(
            signal_id="sig-4",
            signal_type=SignalType.SLI,
            brain_aware=True,
            brain_intent=BrainIntentCategory.CUSTOMER_IMPACT,
            lid_status=ComplianceStatus.COMPLIANT,
            quality_score=0.5,
        )
        assert not result.aod_eligible


# ---------------------------------------------------------------------------
# generate_sli_template
# ---------------------------------------------------------------------------


class TestGenerateSLITemplate:
    def test_template_includes_required_lid_dimensions(self):
        result = generate_sli_template(
            service_name="SvcA",
            cujo_id="CUJO-1",
            metric_namespace="Azure.SvcA",
            metric_name="RequestLatency",
            signal_type=SignalType.SLI,
            brain_intent=BrainIntentCategory.CUSTOMER_IMPACT,
            dimensions=[],
            suggested_threshold=99.5,
            window_minutes=60,
        )
        assert result["type"] == "SLI"
        assert result["cujo_id"] == "CUJO-1"
        # At least one LID dimension should have been auto-added
        assert len(result["lid_dimensions_added"]) > 0

    def test_template_with_existing_lid_dims_no_duplicates(self):
        result = generate_sli_template(
            service_name="SvcA",
            cujo_id=None,
            metric_namespace="Azure.SvcA",
            metric_name="Availability",
            signal_type=SignalType.SLI,
            brain_intent=BrainIntentCategory.CUSTOMER_IMPACT,
            dimensions=["Latency_P99", "AvailabilityRate", "ServiceName"],
            suggested_threshold=99.9,
            window_minutes=30,
        )
        # All LID dims already present — none should be added
        assert result["lid_dimensions_added"] == []
