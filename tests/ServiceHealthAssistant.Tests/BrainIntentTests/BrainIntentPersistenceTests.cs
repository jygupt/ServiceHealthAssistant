using Moq;
using ServiceHealthAssistant.Adx;
using ServiceHealthAssistant.Evaluators;
using ServiceHealthAssistant.Models;

namespace ServiceHealthAssistant.Tests.BrainIntentTests;

// ---------------------------------------------------------------------------
// Normalization tests – verify Evaluate() maps fields correctly into ADX row
// ---------------------------------------------------------------------------

public class BrainIntentNormalizationTests
{
    private static readonly DateTime FixedTimestamp = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Evaluate_CustomerImpact_WithAllRequiredFields_ReturnsEnabledBrainAwareness()
    {
        var monitor = new MonitorEvaluationInput(
            MonitorId: "mon-001",
            MonitorName: "CheckoutAvailability",
            MonitorType: "MdmMetricMonitor",
            LinkedCujoJourney: "CJ-checkout",
            OutageDrivingIcmMapping: true,
            DetectedImpactType: DetectedImpactType.Customer,
            LidPresence: true,
            RegionalScopeDetectable: true,
            SubscriptionScopeDetectable: true,
            HistoricalPrecision: HistoricalPrecision.High,
            SignalStability: SignalStability.Stable,
            UsedInOutageDeclarationPreviously: true,
            CommunicationRelevantImpact: true,
            LinkedICMIncidentId: "IcM-999");

        var row = BrainIntentServiceEvaluator.Evaluate("svc-123", "CheckoutService", monitor, FixedTimestamp);

        Assert.Equal("svc-123", row.ServiceId);
        Assert.Equal("CheckoutService", row.ServiceName);
        Assert.Equal("mon-001", row.MonitorId);
        Assert.Equal("CheckoutAvailability", row.MonitorName);
        Assert.Equal("MdmMetricMonitor", row.MonitorType);
        Assert.False(row.IsSLI);
        Assert.Equal(BrainIntentStatus.Enabled, row.BrainAwareness);
        Assert.Equal(BrainIntentStatus.Enabled, row.OutageDeclaration);
        // DeploymentStops requires DetectedImpactType == Deployment; Customer impact → WillNotBeEnabled.
        Assert.Equal(BrainIntentStatus.WillNotBeEnabled, row.DeploymentStops);
        Assert.Equal(BrainIntentStatus.Enabled, row.AutoComms);
        Assert.Equal("MCP:evaluate_monitor_brain_integration", row.EvaluationSource);
        Assert.Equal(FixedTimestamp, row.EvaluationTimestamp);
        Assert.Equal("CJ-checkout", row.CujoJourney);
        Assert.Equal("IcM-999", row.LinkedICMIncidentId);
        Assert.True(row.LIDPresent);
        Assert.True(row.RegionalScopeDetectable);
        Assert.True(row.SubscriptionScopeDetectable);
        Assert.Equal(HistoricalPrecision.High, row.HistoricalPrecision);
        Assert.Equal(SignalStability.Stable, row.SignalStability);
        Assert.True(row.CommunicationRelevant);
    }

    [Fact]
    public void Evaluate_OperationalImpact_ReturnsWillNotBeEnabledBrainAwareness()
    {
        var monitor = new MonitorEvaluationInput(
            MonitorId: "mon-002",
            MonitorName: "DiskUtilization",
            DetectedImpactType: DetectedImpactType.Operational);

        var row = BrainIntentServiceEvaluator.Evaluate("svc-999", "InfraService", monitor, FixedTimestamp);

        Assert.Equal(BrainIntentStatus.WillNotBeEnabled, row.BrainAwareness);
        Assert.Equal(BrainIntentStatus.WillNotBeEnabled, row.OutageDeclaration); // no regional scope
        Assert.Equal(BrainIntentStatus.WillNotBeEnabled, row.DeploymentStops);
        Assert.Equal(BrainIntentStatus.WillNotBeEnabled, row.AutoComms);
    }

    [Fact]
    public void Evaluate_CustomerImpact_MissingCujoOrIcm_ReturnsShouldBeEnabled()
    {
        var monitor = new MonitorEvaluationInput(
            MonitorId: "mon-003",
            MonitorName: "ApiLatency",
            DetectedImpactType: DetectedImpactType.Customer
            // No LinkedCujoJourney, OutageDrivingIcmMapping defaults false
        );

        var row = BrainIntentServiceEvaluator.Evaluate("svc-456", "ApiService", monitor, FixedTimestamp);

        Assert.Equal(BrainIntentStatus.ShouldBeEnabled, row.BrainAwareness);
    }

    [Fact]
    public void Evaluate_NullOptionalFields_AreNullInRow()
    {
        var monitor = new MonitorEvaluationInput(
            MonitorId: "mon-004",
            MonitorName: "SimpleMonitor");

        var row = BrainIntentServiceEvaluator.Evaluate("svc-789", "", monitor, FixedTimestamp);

        Assert.Null(row.CujoJourney);
        Assert.Null(row.LinkedICMIncidentId);
        // MonitorType is mapped from null input to empty string in the ADX row.
        Assert.Equal(string.Empty, row.MonitorType);
    }

    [Fact]
    public void Evaluate_AlwaysSetsIsSliToFalse()
    {
        var monitor = new MonitorEvaluationInput("mon-005", "AnyMonitor");
        var row = BrainIntentServiceEvaluator.Evaluate("svc-0", "S", monitor, FixedTimestamp);
        Assert.False(row.IsSLI);
    }

    [Fact]
    public void Evaluate_DeploymentMonitor_WithoutSubscriptionScope_ReturnsShouldBeEnabled()
    {
        var monitor = new MonitorEvaluationInput(
            MonitorId: "mon-006",
            MonitorName: "DeploySignal",
            DetectedImpactType: DetectedImpactType.Deployment,
            SubscriptionScopeDetectable: false);

        var row = BrainIntentServiceEvaluator.Evaluate("svc-d", "DeployService", monitor, FixedTimestamp);

        Assert.Equal(BrainIntentStatus.ShouldBeEnabled, row.DeploymentStops);
    }
}

// ---------------------------------------------------------------------------
// Batching tests – verify IngestBatchAsync is called with expected counts
// ---------------------------------------------------------------------------

public class BrainIntentBatchingTests
{
    private static readonly DateTime FixedTimestamp = new(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static MonitorEvaluationInput MakeMonitor(int index) =>
        new($"mon-{index}", $"Monitor{index}");

    [Fact]
    public async Task EvaluateAndPersist_SingleBatch_CallsIngestOnce()
    {
        var writerMock = new Mock<IKustoBrainIntentWriter>();
        writerMock
            .Setup(w => w.IngestBatchAsync(It.IsAny<IReadOnlyList<BrainIntentEvaluationRow>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var evaluator = new BrainIntentServiceEvaluator(writerMock.Object);
        var monitors = Enumerable.Range(0, 5).Select(MakeMonitor).ToList();

        var rows = await evaluator.EvaluateAndPersistAsync("svc-1", "SvcName", monitors,
            evaluationTimestamp: FixedTimestamp, batchSize: 10);

        Assert.Equal(5, rows.Count);
        // All 5 rows fit in one batch.
        writerMock.Verify(
            w => w.IngestBatchAsync(It.IsAny<IReadOnlyList<BrainIntentEvaluationRow>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EvaluateAndPersist_MultipleBatches_CallsIngestExpectedTimes()
    {
        var capturedBatches = new List<IReadOnlyList<BrainIntentEvaluationRow>>();
        var writerMock = new Mock<IKustoBrainIntentWriter>();
        writerMock
            .Setup(w => w.IngestBatchAsync(It.IsAny<IReadOnlyList<BrainIntentEvaluationRow>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<BrainIntentEvaluationRow>, CancellationToken>((batch, _) => capturedBatches.Add(batch))
            .Returns(Task.CompletedTask);

        var evaluator = new BrainIntentServiceEvaluator(writerMock.Object);
        var monitors = Enumerable.Range(0, 25).Select(MakeMonitor).ToList();

        var rows = await evaluator.EvaluateAndPersistAsync("svc-2", "BigService", monitors,
            evaluationTimestamp: FixedTimestamp, batchSize: 10);

        Assert.Equal(25, rows.Count);
        // 25 monitors with batch size 10 => 3 batches (10 + 10 + 5).
        Assert.Equal(3, capturedBatches.Count);
        Assert.Equal(10, capturedBatches[0].Count);
        Assert.Equal(10, capturedBatches[1].Count);
        Assert.Equal(5,  capturedBatches[2].Count);
    }

    [Fact]
    public async Task EvaluateAndPersist_EmptyMonitorList_NeverCallsIngest()
    {
        var writerMock = new Mock<IKustoBrainIntentWriter>();
        var evaluator = new BrainIntentServiceEvaluator(writerMock.Object);

        var rows = await evaluator.EvaluateAndPersistAsync("svc-3", "EmptyService", [],
            evaluationTimestamp: FixedTimestamp);

        Assert.Empty(rows);
        writerMock.Verify(
            w => w.IngestBatchAsync(It.IsAny<IReadOnlyList<BrainIntentEvaluationRow>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EvaluateAndPersist_RowsHaveCorrectServiceId()
    {
        var writerMock = new Mock<IKustoBrainIntentWriter>();
        writerMock
            .Setup(w => w.IngestBatchAsync(It.IsAny<IReadOnlyList<BrainIntentEvaluationRow>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var evaluator = new BrainIntentServiceEvaluator(writerMock.Object);
        var monitors = new[] { MakeMonitor(1) };

        var rows = await evaluator.EvaluateAndPersistAsync("my-service-id", "MyService", monitors,
            evaluationTimestamp: FixedTimestamp);

        Assert.All(rows, r => Assert.Equal("my-service-id", r.ServiceId));
        Assert.All(rows, r => Assert.Equal("MyService", r.ServiceName));
        Assert.All(rows, r => Assert.Equal(FixedTimestamp, r.EvaluationTimestamp));
        Assert.All(rows, r => Assert.Equal("MCP:evaluate_monitor_brain_integration", r.EvaluationSource));
    }

    [Fact]
    public async Task EvaluateAndPersist_BatchPayloadCount_MatchesMonitorCount()
    {
        var totalIngested = 0;
        var writerMock = new Mock<IKustoBrainIntentWriter>();
        writerMock
            .Setup(w => w.IngestBatchAsync(It.IsAny<IReadOnlyList<BrainIntentEvaluationRow>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<BrainIntentEvaluationRow>, CancellationToken>((batch, _) => totalIngested += batch.Count)
            .Returns(Task.CompletedTask);

        var evaluator = new BrainIntentServiceEvaluator(writerMock.Object);
        var monitors = Enumerable.Range(0, 15).Select(MakeMonitor).ToList();

        await evaluator.EvaluateAndPersistAsync("svc-4", "CountService", monitors,
            evaluationTimestamp: FixedTimestamp, batchSize: 7);

        // 15 monitors → 3 batches (7 + 7 + 1) → total ingested = 15.
        Assert.Equal(15, totalIngested);
    }
}
