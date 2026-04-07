using ServiceHealthAssistant.Models;
using ServiceHealthAssistant.Rules;
using Xunit;

namespace ServiceHealthAssistant.Tests.RulesTests;

// ---------------------------------------------------------------------------
// ClassifySignal
// ---------------------------------------------------------------------------

public class SignalClassificationTests
{
    [Fact]
    public void CustomerFacing_WithCujo_ReturnsSli()
    {
        var req = new SignalClassificationRequest(
            "MyService", "Availability metric",
            CujoId: "CUJO-001",
            HasCustomerFacingImpact: true,
            HasAvailabilityMetric: true);

        var result = ServiceHealthRules.ClassifySignal(req);

        Assert.Equal(SignalType.SLI, result.SignalType);
        Assert.Equal(BrainIntentCategory.CustomerImpact, result.BrainIntent);
    }

    [Fact]
    public void InfrastructureOnly_ReturnsServiceMonitor()
    {
        var req = new SignalClassificationRequest(
            "MyService", "CPU utilization",
            IsInfrastructureSignal: true);

        var result = ServiceHealthRules.ClassifySignal(req);

        Assert.Equal(SignalType.ServiceMonitor, result.SignalType);
        Assert.Equal(BrainIntentCategory.OperationalInfrastructure, result.BrainIntent);
    }

    [Fact]
    public void CustomerFacing_WithoutCujo_RecommendsAddingCujo()
    {
        var req = new SignalClassificationRequest(
            "MyService", "Error rate",
            HasErrorRateMetric: true);

        var result = ServiceHealthRules.ClassifySignal(req);

        Assert.Equal(SignalType.SLI, result.SignalType);
        Assert.Contains(result.Recommendations, r => r.Contains("CUJO"));
    }

    [Fact]
    public void LatencyOnly_WithCustomerImpactFlag_IsCustomerImpact()
    {
        var req = new SignalClassificationRequest(
            "MyService", "API latency with customer impact",
            HasCustomerFacingImpact: true,
            HasLatencyMetric: true);

        var result = ServiceHealthRules.ClassifySignal(req);

        Assert.Equal(BrainIntentCategory.CustomerImpact, result.BrainIntent);
    }

    [Fact]
    public void LatencyOnly_WithoutCustomerFlag_IsUnclassified()
    {
        var req = new SignalClassificationRequest(
            "MyService", "P99 latency",
            HasLatencyMetric: true);

        var result = ServiceHealthRules.ClassifySignal(req);

        Assert.Equal(BrainIntentCategory.Unclassified, result.BrainIntent);
    }

    [Fact]
    public void AmbiguousSignal_DefaultsToSli_IsUnclassified()
    {
        var req = new SignalClassificationRequest("MyService", "Some metric");

        var result = ServiceHealthRules.ClassifySignal(req);

        Assert.Equal(SignalType.SLI, result.SignalType);
        Assert.Equal(BrainIntentCategory.Unclassified, result.BrainIntent);
    }
}

// ---------------------------------------------------------------------------
// EvaluateLidCompliance
// ---------------------------------------------------------------------------

public class LidComplianceTests
{
    private static IReadOnlyList<MetricDimension> Dims(params string[] names) =>
        names.Select(n => new MetricDimension(n)).ToList().AsReadOnly();

    [Fact]
    public void AllThreeDimensions_IsCompliant()
    {
        var dims = Dims("Latency_P99", "AvailabilityRate", "ServiceName");
        var result = ServiceHealthRules.EvaluateLidCompliance("sig-1", SignalType.SLI, dims);

        Assert.Equal(ComplianceStatus.Compliant, result.Status);
        Assert.Equal(1.0, result.Score);
        Assert.True(result.LatencyPresent);
        Assert.True(result.ImpactPresent);
    }

    [Fact]
    public void MissingLatency_IsPartial()
    {
        var dims = Dims("AvailabilityRate", "ServiceName");
        var result = ServiceHealthRules.EvaluateLidCompliance("sig-2", SignalType.SLI, dims);

        Assert.Equal(ComplianceStatus.Partial, result.Status);
        Assert.False(result.LatencyPresent);
        Assert.Contains(result.MissingDimensions, m => m.Contains("Latency"));
    }

    [Fact]
    public void NoDimensions_IsNonCompliant()
    {
        var result = ServiceHealthRules.EvaluateLidCompliance("sig-3", SignalType.SLI, null);

        Assert.Equal(ComplianceStatus.NonCompliant, result.Status);
        Assert.Equal(0.0, result.Score);
    }

    [Fact]
    public void KqlCompensatesForMissingDimensions()
    {
        var result = ServiceHealthRules.EvaluateLidCompliance(
            "sig-4", SignalType.SLI, null,
            kqlQuery: "| where latency > 200 | where availability < 99 | where region == 'us'");

        Assert.True(result.LatencyPresent);
        Assert.True(result.ImpactPresent);
    }
}

// ---------------------------------------------------------------------------
// ValidateBrainIntent
// ---------------------------------------------------------------------------

public class BrainIntentTests
{
    [Fact]
    public void CustomerImpact_DeclaredCorrectly_IsCorrect()
    {
        var result = ServiceHealthRules.ValidateBrainIntent(
            "sig-1", SignalType.SLI, BrainIntentCategory.CustomerImpact,
            hasCustomerFacingImpact: true);

        Assert.True(result.IsCorrect);
        Assert.Equal(1.0, result.Confidence);
    }

    [Fact]
    public void WrongIntent_IsDetected()
    {
        var result = ServiceHealthRules.ValidateBrainIntent(
            "sig-2", SignalType.SLI, BrainIntentCategory.OperationalInfrastructure,
            hasCustomerFacingImpact: true);

        Assert.False(result.IsCorrect);
        Assert.Equal(0.0, result.Confidence);
    }

    [Fact]
    public void Unclassified_GeneratesRecommendation()
    {
        var result = ServiceHealthRules.ValidateBrainIntent(
            "sig-3", SignalType.SLI, BrainIntentCategory.Unclassified);

        Assert.Contains(result.Recommendations, r => r.Contains("Unclassified"));
    }
}

// ---------------------------------------------------------------------------
// ScoreSliQuality
// ---------------------------------------------------------------------------

public class SliQualityTests
{
    private static LidComplianceResult MakeLid(double score) => new(
        "sli-1", SignalType.SLI,
        score == 1.0 ? ComplianceStatus.Compliant : ComplianceStatus.Partial,
        LatencyPresent: true, ImpactPresent: true,
        DependencyDimensions: [],
        MissingDimensions: [],
        Score: score,
        Recommendations: []);

    private static BrainIntentResult MakeBrain(bool correct) => new(
        "sli-1", SignalType.SLI, BrainIntentCategory.CustomerImpact,
        Confidence: correct ? 1.0 : 0.0,
        IsCorrect: correct,
        Rationale: "",
        Recommendations: []);

    [Fact]
    public void HighQualitySli_IsPublishSafe()
    {
        var dims = new[]
        {
            new MetricDimension("Latency_P99", PresentInMdm: true),
            new MetricDimension("AvailabilityRate", PresentInMdm: true)
        };
        var result = ServiceHealthRules.ScoreSliQuality(
            "sli-1", "Azure.MyService", "RequestLatency",
            "MyMetric | summarize ...", dims,
            threshold: 99.5, windowMinutes: 60,
            MakeLid(1.0), MakeBrain(true));

        Assert.True(result.PublishSafe);
        Assert.True(result.OverallScore >= 0.7);
    }

    [Fact]
    public void MissingNamespace_BlocksPublish()
    {
        var result = ServiceHealthRules.ScoreSliQuality(
            "sli-2", "", "", "", null,
            threshold: null, windowMinutes: 60,
            MakeLid(0.0), MakeBrain(false));

        Assert.False(result.PublishSafe);
        Assert.NotEmpty(result.BlockingIssues);
    }
}

// ---------------------------------------------------------------------------
// RunPreFlightValidation
// ---------------------------------------------------------------------------

public class PreFlightValidationTests
{
    [Fact]
    public void UnclassifiedIntent_BlocksPublish()
    {
        var req = new PreFlightValidationRequest(
            "sig-1", SignalType.SLI,
            "Azure.MyService", "Availability",
            BrainIntent: BrainIntentCategory.Unclassified);

        var result = ServiceHealthRules.RunPreFlightValidation(req);

        Assert.False(result.Passed);
        Assert.Contains(result.BlockingIssues, i => i.Contains("UNCLASSIFIED"));
    }

    [Fact]
    public void ValidSignal_NoLidBlockingIssues()
    {
        var dims = new[]
        {
            new MetricDimension("Latency_P99", PresentInMdm: true),
            new MetricDimension("AvailabilityRate", PresentInMdm: true),
            new MetricDimension("ServiceName", PresentInMdm: true)
        };
        var req = new PreFlightValidationRequest(
            "sig-2", SignalType.SLI,
            "Azure.MyService", "RequestLatency",
            KqlQuery: "Azure.MyService | where Latency > 200",
            Dimensions: dims,
            BrainIntent: BrainIntentCategory.CustomerImpact,
            Owner: "sre-team");

        var result = ServiceHealthRules.RunPreFlightValidation(req);

        Assert.DoesNotContain(result.BlockingIssues, i => i.Contains("LID compliance failure"));
    }
}

// ---------------------------------------------------------------------------
// DetectCoverageGaps
// ---------------------------------------------------------------------------

public class CoverageGapTests
{
    [Fact]
    public void CujoWithoutSli_IsDetectionGap()
    {
        var gaps = ServiceHealthRules.DetectCoverageGaps(
            "SvcA", ["CUJO-1", "CUJO-2"], [], []);

        Assert.Equal(2, gaps.Count);
        Assert.All(gaps, g => Assert.Equal(GapType.DetectionGap, g.GapType));
    }

    [Fact]
    public void CoveredCujo_NoDetectionGap()
    {
        var slis = new[]
        {
            new Sli("sli-1", "SLI1", "SvcA",
                BrainAware: true,
                CujoIds: ["CUJO-1"],
                QualityScores: new Dictionary<string, double> { ["overall"] = 0.9 })
        };

        var gaps = ServiceHealthRules.DetectCoverageGaps("SvcA", ["CUJO-1"], slis, []);

        Assert.DoesNotContain(gaps, g => g.GapType == GapType.DetectionGap);
    }

    [Fact]
    public void NonBrainAwareSli_CreatesAutomationGap()
    {
        var slis = new[]
        {
            new Sli("sli-1", "SLI1", "SvcA",
                BrainAware: false,
                CujoIds: ["CUJO-1"],
                QualityScores: new Dictionary<string, double> { ["overall"] = 0.9 })
        };

        var gaps = ServiceHealthRules.DetectCoverageGaps("SvcA", ["CUJO-1"], slis, []);
        var autoGaps = gaps.Where(g => g.GapType == GapType.AutomationReadinessGap).ToList();

        Assert.Single(autoGaps);
    }

    [Fact]
    public void PromotionEligibleMonitor_CreatesQualityGap()
    {
        var monitors = new[]
        {
            new ServiceMonitor("mon-1", "Monitor1", "SvcA", SliPromotionEligible: true)
        };

        var gaps = ServiceHealthRules.DetectCoverageGaps("SvcA", [], [], monitors);
        var qualityGaps = gaps.Where(g => g.GapType == GapType.SignalQualityGap).ToList();

        Assert.Single(qualityGaps);
    }
}

// ---------------------------------------------------------------------------
// GenerateRepairItems
// ---------------------------------------------------------------------------

public class RepairItemTests
{
    [Fact]
    public void DetectionGap_MapsToCoverageKpi()
    {
        var gap = new CoverageGap("gap-1", "SvcA", GapType.DetectionGap, "CUJO-1 has no SLI coverage.", CujoId: "CUJO-1");
        var repairs = ServiceHealthRules.GenerateRepairItems([gap]);

        Assert.Single(repairs);
        Assert.Equal(S360KpiCategory.Coverage, repairs[0].S360KpiCategory);
    }

    [Fact]
    public void AutomationGap_MapsToAutomationKpi()
    {
        var gap = new CoverageGap("gap-2", "SvcA", GapType.AutomationReadinessGap, "Not Brain-aware.");
        var repairs = ServiceHealthRules.GenerateRepairItems([gap]);

        Assert.Equal(S360KpiCategory.Automation, repairs[0].S360KpiCategory);
    }

    [Fact]
    public void SignalQualityGap_MapsToQualityKpi()
    {
        var gap = new CoverageGap("gap-3", "SvcA", GapType.SignalQualityGap, "Quality too low.");
        var repairs = ServiceHealthRules.GenerateRepairItems([gap]);

        Assert.Equal(S360KpiCategory.Quality, repairs[0].S360KpiCategory);
    }
}

// ---------------------------------------------------------------------------
// GenerateS360KpiActions
// ---------------------------------------------------------------------------

public class S360KpiActionTests
{
    [Fact]
    public void RepairItems_GroupedByCategory()
    {
        var items = new[]
        {
            new RepairItem("r-1", "Fix LID",      "...", ServiceName: "SvcA", S360KpiCategory: S360KpiCategory.Lid),
            new RepairItem("r-2", "Fix Coverage", "...", ServiceName: "SvcA", S360KpiCategory: S360KpiCategory.Coverage),
            new RepairItem("r-3", "Fix LID 2",    "...", ServiceName: "SvcA", S360KpiCategory: S360KpiCategory.Lid)
        };

        var actions = ServiceHealthRules.GenerateS360KpiActions(items);
        var categories = actions.Select(a => a.Category).ToHashSet();

        Assert.Contains(S360KpiCategory.Lid, categories);
        Assert.Contains(S360KpiCategory.Coverage, categories);

        var lidAction = actions.Single(a => a.Category == S360KpiCategory.Lid);
        Assert.Equal(2, lidAction.RepairItemIds!.Count);
    }
}

// ---------------------------------------------------------------------------
// EvaluateAutomationReadiness
// ---------------------------------------------------------------------------

public class AutomationReadinessTests
{
    [Fact]
    public void AllCriteriaMet_IsReady()
    {
        var result = ServiceHealthRules.EvaluateAutomationReadiness(
            "sig-1", SignalType.SLI,
            brainAware: true,
            brainIntent: BrainIntentCategory.CustomerImpact,
            lidStatus: ComplianceStatus.Compliant,
            qualityScore: 0.85);

        Assert.Equal(AutomationReadinessLevel.Ready, result.ReadinessLevel);
        Assert.True(result.AodEligible);
        Assert.True(result.AutoCommsReady);
    }

    [Fact]
    public void NotBrainAware_IsBlocked()
    {
        var result = ServiceHealthRules.EvaluateAutomationReadiness(
            "sig-2", SignalType.SLI,
            brainAware: false,
            brainIntent: BrainIntentCategory.CustomerImpact,
            lidStatus: ComplianceStatus.Compliant,
            qualityScore: 0.85);

        Assert.Equal(AutomationReadinessLevel.Blocked, result.ReadinessLevel);
        Assert.False(result.AodEligible);
    }

    [Fact]
    public void WrongIntent_IsNotEligible()
    {
        var result = ServiceHealthRules.EvaluateAutomationReadiness(
            "sig-3", SignalType.SLI,
            brainAware: true,
            brainIntent: BrainIntentCategory.OperationalInfrastructure,
            lidStatus: ComplianceStatus.Compliant,
            qualityScore: 0.85);

        Assert.False(result.AodEligible);
    }

    [Fact]
    public void LowQualityScore_BlocksAutomation()
    {
        var result = ServiceHealthRules.EvaluateAutomationReadiness(
            "sig-4", SignalType.SLI,
            brainAware: true,
            brainIntent: BrainIntentCategory.CustomerImpact,
            lidStatus: ComplianceStatus.Compliant,
            qualityScore: 0.5);

        Assert.False(result.AodEligible);
    }

    [Fact]
    public void BrainAware_SingleOtherIssue_IsConditionallyReady()
    {
        // brain-aware=true, but intent is wrong (single non-brain issue)
        var result = ServiceHealthRules.EvaluateAutomationReadiness(
            "sig-5", SignalType.SLI,
            brainAware: true,
            brainIntent: BrainIntentCategory.OperationalInfrastructure,
            lidStatus: ComplianceStatus.Compliant,
            qualityScore: 0.85);

        Assert.Equal(AutomationReadinessLevel.ConditionallyReady, result.ReadinessLevel);
    }
}

// ---------------------------------------------------------------------------
// GenerateSliTemplate
// ---------------------------------------------------------------------------

public class SliTemplateTests
{
    [Fact]
    public void EmptyDimensions_LidDimensionsAutoAdded()
    {
        var template = ServiceHealthRules.GenerateSliTemplate(
            "SvcA", "CUJO-1", "Azure.SvcA", "RequestLatency",
            SignalType.SLI, BrainIntentCategory.CustomerImpact,
            [], suggestedThreshold: 99.5, windowMinutes: 60);

        var lidAdded = (List<string>)template["lid_dimensions_added"]!;
        Assert.NotEmpty(lidAdded);
        Assert.Equal("SLI", template["type"]);
        Assert.Equal("CUJO-1", template["cujo_id"]);
    }

    [Fact]
    public void AllLidDimsPresent_NoneAdded()
    {
        string[] dims = ["Latency_P99", "AvailabilityRate", "ServiceName"];
        var template = ServiceHealthRules.GenerateSliTemplate(
            "SvcA", null, "Azure.SvcA", "Availability",
            SignalType.SLI, BrainIntentCategory.CustomerImpact,
            dims, suggestedThreshold: 99.9, windowMinutes: 30);

        var lidAdded = (List<string>)template["lid_dimensions_added"]!;
        Assert.Empty(lidAdded);
    }
}
