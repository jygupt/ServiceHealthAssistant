using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceHealthAssistant.Models;
using ServiceHealthAssistant.Repair;

namespace ServiceHealthAssistant.Tests.RepairTests;

// ---------------------------------------------------------------------------
// Helper factory
// ---------------------------------------------------------------------------

file static class RepairTestHelpers
{
    public static BulkGenevaBrainCapabilityMetadataRepairAgent BuildAgent(
        IGenevaMonitorMetadataClient? metadataClient = null,
        IDashboardMonitorSetProvider? dashboardProvider = null,
        IPropagationValidator? propagationValidator = null)
    {
        metadataClient     ??= new Mock<IGenevaMonitorMetadataClient>().Object;
        dashboardProvider  ??= new Mock<IDashboardMonitorSetProvider>().Object;
        propagationValidator ??= new Mock<IPropagationValidator>().Object;

        return new BulkGenevaBrainCapabilityMetadataRepairAgent(
            metadataClient,
            dashboardProvider,
            propagationValidator,
            NullLogger<BulkGenevaBrainCapabilityMetadataRepairAgent>.Instance);
    }

    public static BrainCapabilityRepairRequest MinimalRequest(
        string serviceId      = "svc-001",
        string genevaAccountId = "test-account",
        bool dryRun           = true,
        CapabilityTargetStates? desiredStates = null,
        IReadOnlyList<string>? monitorIds = null,
        int batchSize         = 10,
        int maxConcurrency    = 2,
        int maxRetry          = 1,
        bool stopOnFirstFailure = false)
        => new(
            ServiceId:                    serviceId,
            ServiceName:                  "TestService",
            GenevaAccountId:              genevaAccountId,
            CorrelationId:                "corr-test",
            MonitorIds:                   monitorIds,
            DashboardActionRequiredFilter: "Yes",
            DesiredStates: desiredStates ?? new CapabilityTargetStates(
                BrainAwareness: BrainIntentStatus.Enabled),
            DryRun:                       dryRun,
            BatchSize:                    batchSize,
            MaxConcurrency:               maxConcurrency,
            MaxRetry:                     maxRetry,
            RetryBackoff:                 TimeSpan.FromMilliseconds(1),
            StopOnFirstFailure:           stopOnFirstFailure);

    public static DashboardMonitorDescriptor MakeDashboardMonitor(
        string monitorId,
        BrainIntentStatus brainAwareness = BrainIntentStatus.ShouldBeEnabled)
        => new(
            MonitorId:         monitorId,
            MonitorName:       $"Monitor-{monitorId}",
            AccountId:         "test-account",
            ServiceId:         "svc-001",
            BrainAwareness:    brainAwareness,
            OutageDeclaration: BrainIntentStatus.NotClassified,
            DeploymentStops:   BrainIntentStatus.NotClassified,
            AutoComms:         BrainIntentStatus.NotClassified,
            NextAction:        null);
}

// ---------------------------------------------------------------------------
// BuildDesiredMetadata unit tests
// ---------------------------------------------------------------------------

public class BuildDesiredMetadataTests
{
    [Fact]
    public void AllNull_ReturnsEmptyDictionary()
    {
        var states = new CapabilityTargetStates();
        var result = BulkGenevaBrainCapabilityMetadataRepairAgent.BuildDesiredMetadata(states);
        Assert.Empty(result);
    }

    [Fact]
    public void SingleCapability_ReturnsOnlyThatKey()
    {
        var states = new CapabilityTargetStates(BrainAwareness: BrainIntentStatus.Enabled);
        var result = BulkGenevaBrainCapabilityMetadataRepairAgent.BuildDesiredMetadata(states);

        Assert.Single(result);
        Assert.Equal("Enabled", result[BrainCapabilityMetadataKeys.BrainAwareness]);
    }

    [Fact]
    public void AllFourCapabilities_ReturnsAllKeys()
    {
        var states = new CapabilityTargetStates(
            BrainAwareness:    BrainIntentStatus.Enabled,
            OutageDeclaration: BrainIntentStatus.ShouldBeEnabled,
            DeploymentStops:   BrainIntentStatus.WillNotBeEnabled,
            AutoComms:         BrainIntentStatus.NotClassified);

        var result = BulkGenevaBrainCapabilityMetadataRepairAgent.BuildDesiredMetadata(states);

        Assert.Equal(4, result.Count);
        Assert.Equal("Enabled",           result[BrainCapabilityMetadataKeys.BrainAwareness]);
        Assert.Equal("ShouldBeEnabled",   result[BrainCapabilityMetadataKeys.OutageDeclaration]);
        Assert.Equal("WillNotBeEnabled",  result[BrainCapabilityMetadataKeys.DeploymentStops]);
        Assert.Equal("NotClassified",     result[BrainCapabilityMetadataKeys.AutoComms]);
    }

    [Fact]
    public void NullCapabilitiesAreExcluded()
    {
        var states = new CapabilityTargetStates(
            BrainAwareness: BrainIntentStatus.Enabled,
            OutageDeclaration: null,
            DeploymentStops: BrainIntentStatus.WillNotBeEnabled,
            AutoComms: null);

        var result = BulkGenevaBrainCapabilityMetadataRepairAgent.BuildDesiredMetadata(states);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey(BrainCapabilityMetadataKeys.BrainAwareness));
        Assert.True(result.ContainsKey(BrainCapabilityMetadataKeys.DeploymentStops));
        Assert.False(result.ContainsKey(BrainCapabilityMetadataKeys.OutageDeclaration));
        Assert.False(result.ContainsKey(BrainCapabilityMetadataKeys.AutoComms));
    }
}

// ---------------------------------------------------------------------------
// Batching tests
// ---------------------------------------------------------------------------

public class BatchingTests
{
    [Fact]
    public async Task ExecuteAsync_100Monitors_ProcessesAllWithoutLoss()
    {
        const int monitorCount = 100;

        // Metadata client returns empty current metadata for all monitors.
        var metaMock = new Mock<IGenevaMonitorMetadataClient>();
        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        var monitorIds = Enumerable.Range(1, monitorCount)
            .Select(i => $"mon-{i:D3}")
            .ToList();

        var agent   = RepairTestHelpers.BuildAgent(metadataClient: metaMock.Object);
        var request = RepairTestHelpers.MinimalRequest(
            monitorIds: monitorIds,
            batchSize:  15,
            maxConcurrency: 5);

        var result = await agent.ExecuteAsync(request);

        Assert.Equal(monitorCount, result.TotalTargeted);
        Assert.Equal(monitorCount, result.MonitorResults.Count);
    }

    [Fact]
    public async Task ExecuteAsync_BatchSize1_ProcessesOneAtATime()
    {
        var metaMock = new Mock<IGenevaMonitorMetadataClient>();
        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        var monitorIds = new[] { "m-1", "m-2", "m-3" };
        var agent      = RepairTestHelpers.BuildAgent(metadataClient: metaMock.Object);
        var request    = RepairTestHelpers.MinimalRequest(monitorIds: monitorIds, batchSize: 1);

        var result = await agent.ExecuteAsync(request);

        Assert.Equal(3, result.TotalTargeted);
        Assert.Equal(3, result.MonitorResults.Count);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyMonitorIds_AndEmptyDashboard_ReturnsZeroTargeted()
    {
        var dashMock = new Mock<IDashboardMonitorSetProvider>();
        dashMock
            .Setup(p => p.GetMonitorsForServiceAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<DashboardMonitorDescriptor>());

        var agent  = RepairTestHelpers.BuildAgent(dashboardProvider: dashMock.Object);
        var request = RepairTestHelpers.MinimalRequest(monitorIds: null);

        var result = await agent.ExecuteAsync(request);

        Assert.Equal(0, result.TotalTargeted);
        Assert.Equal(0, result.Succeeded);
    }
}

// ---------------------------------------------------------------------------
// Idempotency / delta tests
// ---------------------------------------------------------------------------

public class IdempotencyTests
{
    [Fact]
    public async Task ExecuteAsync_MetadataAlreadyCorrect_NoAttemptedChanges()
    {
        var metaMock = new Mock<IGenevaMonitorMetadataClient>();
        // Current metadata matches desired.
        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                [BrainCapabilityMetadataKeys.BrainAwareness] = "Enabled"
            });

        var agent  = RepairTestHelpers.BuildAgent(metadataClient: metaMock.Object);
        var request = RepairTestHelpers.MinimalRequest(
            monitorIds: ["mon-001"],
            desiredStates: new CapabilityTargetStates(BrainAwareness: BrainIntentStatus.Enabled));

        var result = await agent.ExecuteAsync(request);

        Assert.Equal(1, result.TotalTargeted);
        var monResult = Assert.Single(result.MonitorResults);
        Assert.Empty(monResult.AttemptedChanges);
        Assert.Empty(monResult.AppliedChanges);
        Assert.Equal(MetadataValidationStatus.Verified, monResult.SetValidationStatus);
    }

    [Fact]
    public async Task ExecuteAsync_MetadataPartiallyCorrect_OnlyDeltaAttempted()
    {
        var metaMock = new Mock<IGenevaMonitorMetadataClient>();
        // BrainAwareness already Enabled, OutageDeclaration is wrong.
        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                [BrainCapabilityMetadataKeys.BrainAwareness]    = "Enabled",
                [BrainCapabilityMetadataKeys.OutageDeclaration] = "NotClassified"
            });

        var agent  = RepairTestHelpers.BuildAgent(metadataClient: metaMock.Object);
        var request = RepairTestHelpers.MinimalRequest(
            monitorIds: ["mon-001"],
            desiredStates: new CapabilityTargetStates(
                BrainAwareness:    BrainIntentStatus.Enabled,
                OutageDeclaration: BrainIntentStatus.ShouldBeEnabled));

        var result = await agent.ExecuteAsync(request);

        var monResult = Assert.Single(result.MonitorResults);
        // BrainAwareness already correct → not in AttemptedChanges.
        Assert.DoesNotContain(BrainCapabilityMetadataKeys.BrainAwareness, monResult.AttemptedChanges.Keys);
        // OutageDeclaration differs → in AttemptedChanges.
        Assert.Contains(BrainCapabilityMetadataKeys.OutageDeclaration, monResult.AttemptedChanges.Keys);
    }

    [Fact]
    public async Task ExecuteAsync_DryRun_NeverCallsUpdate()
    {
        var metaMock = new Mock<IGenevaMonitorMetadataClient>();
        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        var agent  = RepairTestHelpers.BuildAgent(metadataClient: metaMock.Object);
        var request = RepairTestHelpers.MinimalRequest(monitorIds: ["mon-001"], dryRun: true);

        await agent.ExecuteAsync(request);

        // UpdateCapabilityMetadataAsync must never be called in DryRun mode.
        metaMock.Verify(
            c => c.UpdateCapabilityMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_DryRun_AppliedChangesIsEmpty()
    {
        var metaMock = new Mock<IGenevaMonitorMetadataClient>();
        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        var agent   = RepairTestHelpers.BuildAgent(metadataClient: metaMock.Object);
        var request = RepairTestHelpers.MinimalRequest(monitorIds: ["mon-001"], dryRun: true);

        var result = await agent.ExecuteAsync(request);

        Assert.All(result.MonitorResults, r => Assert.Empty(r.AppliedChanges));
    }
}

// ---------------------------------------------------------------------------
// Throttling / concurrency tests
// ---------------------------------------------------------------------------

public class ThrottlingTests
{
    [Fact]
    public async Task ExecuteAsync_MaxConcurrency_IsRespected()
    {
        const int monitorCount   = 20;
        const int maxConcurrency = 3;
        int       peakConcurrent = 0;
        int       currentActive  = 0;
        var       lockObj        = new object();

        var metaMock = new Mock<IGenevaMonitorMetadataClient>();
        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, string _, CancellationToken _) =>
            {
                lock (lockObj)
                {
                    currentActive++;
                    if (currentActive > peakConcurrent)
                        peakConcurrent = currentActive;
                }
                await Task.Delay(5);
                lock (lockObj) { currentActive--; }
                return (IReadOnlyDictionary<string, string>)new Dictionary<string, string>();
            });

        var monitorIds = Enumerable.Range(1, monitorCount).Select(i => $"mon-{i}").ToList();
        var agent      = RepairTestHelpers.BuildAgent(metadataClient: metaMock.Object);
        var request    = RepairTestHelpers.MinimalRequest(
            monitorIds: monitorIds,
            batchSize:  monitorCount,   // single batch
            maxConcurrency: maxConcurrency);

        await agent.ExecuteAsync(request);

        Assert.True(peakConcurrent <= maxConcurrency,
            $"Peak concurrent operations ({peakConcurrent}) exceeded maxConcurrency ({maxConcurrency}).");
    }
}

// ---------------------------------------------------------------------------
// Partial failure and error aggregation tests
// ---------------------------------------------------------------------------

public class PartialFailureTests
{
    [Fact]
    public async Task ExecuteAsync_OneMonitorReadFails_OthersStillProcessed()
    {
        const string failMonitorId = "mon-FAIL";
        var metaMock = new Mock<IGenevaMonitorMetadataClient>();

        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(), failMonitorId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Kusto timeout"));

        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(),
                It.Is<string>(id => id != failMonitorId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        var monitorIds = new[] { "mon-001", failMonitorId, "mon-003" };
        var agent      = RepairTestHelpers.BuildAgent(metadataClient: metaMock.Object);
        var request    = RepairTestHelpers.MinimalRequest(
            monitorIds: monitorIds, maxRetry: 1, stopOnFirstFailure: false);

        var result = await agent.ExecuteAsync(request);

        Assert.Equal(3, result.TotalTargeted);
        Assert.Equal(1, result.Failed);

        var failResult = result.MonitorResults.Single(r => r.MonitorId == failMonitorId);
        Assert.NotEmpty(failResult.Errors);

        var okResults = result.MonitorResults.Where(r => r.MonitorId != failMonitorId).ToList();
        Assert.All(okResults, r => Assert.Empty(r.Errors));
    }

    [Fact]
    public async Task ExecuteAsync_StopOnFirstFailure_HaltsAfterFirstBatchWithFailure()
    {
        const string failMonitorId = "mon-FAIL";
        var metaMock = new Mock<IGenevaMonitorMetadataClient>();

        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(), failMonitorId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Kusto timeout"));

        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(),
                It.Is<string>(id => id != failMonitorId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        // 3 monitors in two batches of 1. The failure is in batch 1.
        var monitorIds = new[] { failMonitorId, "mon-002", "mon-003" };
        var agent      = RepairTestHelpers.BuildAgent(metadataClient: metaMock.Object);
        var request    = RepairTestHelpers.MinimalRequest(
            monitorIds: monitorIds,
            batchSize:  1,
            maxRetry:   1,
            stopOnFirstFailure: true);

        var result = await agent.ExecuteAsync(request);

        // Only the first batch (1 monitor) was processed before stopping.
        Assert.Single(result.MonitorResults);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_AuditLog_ContainsEntryForEachCapabilityChange()
    {
        var metaMock = new Mock<IGenevaMonitorMetadataClient>();
        // Current metadata is empty → both requested capabilities are delta.
        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        var agent  = RepairTestHelpers.BuildAgent(metadataClient: metaMock.Object);
        var request = RepairTestHelpers.MinimalRequest(
            monitorIds: ["mon-001"],
            desiredStates: new CapabilityTargetStates(
                BrainAwareness:    BrainIntentStatus.Enabled,
                OutageDeclaration: BrainIntentStatus.ShouldBeEnabled),
            dryRun: true);

        var result = await agent.ExecuteAsync(request);

        // Two capability keys → two audit entries.
        Assert.Equal(2, result.AuditLog.Count);
        Assert.All(result.AuditLog, e => Assert.Equal("corr-test", e.CorrelationId));
        Assert.All(result.AuditLog, e => Assert.Equal("DryRun", e.Status));
    }
}

// ---------------------------------------------------------------------------
// ShouldBeEnabled → Enabled transition tracking tests
// ---------------------------------------------------------------------------

public class TransitionTrackingTests
{
    [Fact]
    public async Task ExecuteAsync_NonDryRun_Successful_CountsShouldBeEnabledTransition()
    {
        var metaMock = new Mock<IGenevaMonitorMetadataClient>();
        // Current: ShouldBeEnabled (empty metadata → will be ShouldBeEnabled on dashboard).
        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>
            {
                [BrainCapabilityMetadataKeys.BrainAwareness] = "ShouldBeEnabled"
            });

        metaMock
            .Setup(c => c.UpdateCapabilityMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonitorMetadataUpdateResult(Succeeded: true));

        var propMock = new Mock<IPropagationValidator>();
        propMock
            .Setup(v => v.ValidateAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PropagationValidationResult>
            {
                new("mon-001", BrainCapabilityMetadataKeys.BrainAwareness,
                    "Enabled", "Enabled", IsSet: true, IsFlowing: null)
            }.AsReadOnly());

        // Dashboard shows ShouldBeEnabled for this monitor.
        var dashMock = new Mock<IDashboardMonitorSetProvider>();
        dashMock
            .Setup(p => p.GetMonitorsForServiceAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DashboardMonitorDescriptor>
            {
                RepairTestHelpers.MakeDashboardMonitor(
                    "mon-001", brainAwareness: BrainIntentStatus.ShouldBeEnabled)
            }.AsReadOnly());

        var agent  = RepairTestHelpers.BuildAgent(
            metadataClient:       metaMock.Object,
            dashboardProvider:    dashMock.Object,
            propagationValidator: propMock.Object);

        var request = RepairTestHelpers.MinimalRequest(
            monitorIds: null, // use dashboard
            desiredStates: new CapabilityTargetStates(BrainAwareness: BrainIntentStatus.Enabled),
            dryRun: false);

        var result = await agent.ExecuteAsync(request);

        Assert.Equal(1, result.ShouldBeEnabledCandidatesUpdated);
    }
}

// ---------------------------------------------------------------------------
// Propagation validation tests
// ---------------------------------------------------------------------------

public class PropagationValidationTests
{
    [Fact]
    public async Task ExecuteAsync_NonDryRun_FlowingNull_EmitsPendingPropagation()
    {
        var metaMock = new Mock<IGenevaMonitorMetadataClient>();
        metaMock
            .Setup(c => c.GetCapabilityMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());

        metaMock
            .Setup(c => c.UpdateCapabilityMetadataAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonitorMetadataUpdateResult(Succeeded: true));

        var propMock = new Mock<IPropagationValidator>();
        propMock
            .Setup(v => v.ValidateAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PropagationValidationResult>
            {
                // IsSet=true, IsFlowing=null → PendingPropagation
                new("mon-001", BrainCapabilityMetadataKeys.BrainAwareness,
                    "Enabled", "Enabled", IsSet: true, IsFlowing: null)
            }.AsReadOnly());

        var agent  = RepairTestHelpers.BuildAgent(
            metadataClient: metaMock.Object, propagationValidator: propMock.Object);

        var request = RepairTestHelpers.MinimalRequest(
            monitorIds: ["mon-001"], dryRun: false);

        var result = await agent.ExecuteAsync(request);

        var monResult = Assert.Single(result.MonitorResults);
        Assert.Equal(MetadataValidationStatus.Verified, monResult.SetValidationStatus);
        Assert.Equal(MetadataValidationStatus.PendingPropagation, monResult.FlowValidationStatus);
        Assert.Equal(1, result.PendingPropagation);
    }
}

// ---------------------------------------------------------------------------
// RetryPolicy unit tests
// ---------------------------------------------------------------------------

public class RetryPolicyTests
{
    [Fact]
    public async Task ExecuteAsync_SucceedsOnFirstAttempt_NoRetries()
    {
        int callCount = 0;
        var result = await RetryPolicy.ExecuteAsync(
            _ => { callCount++; return Task.FromResult(42); },
            maxAttempts: 3,
            baseDelay: TimeSpan.FromMilliseconds(1));

        Assert.Equal(42, result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_FailsThenSucceeds_ReturnsSuccessResult()
    {
        int callCount = 0;
        var result = await RetryPolicy.ExecuteAsync(
            _ =>
            {
                callCount++;
                if (callCount < 3)
                    throw new InvalidOperationException("Transient");
                return Task.FromResult("ok");
            },
            maxAttempts: 3,
            baseDelay: TimeSpan.FromMilliseconds(1));

        Assert.Equal("ok", result);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_ExhaustsRetries_ThrowsAggregateException()
    {
        await Assert.ThrowsAsync<AggregateException>(() =>
            RetryPolicy.ExecuteAsync<int>(
                _ => throw new InvalidOperationException("Always fails"),
                maxAttempts: 2,
                baseDelay: TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public async Task ExecuteAsync_NonTransientException_ThrowsImmediately()
    {
        int callCount = 0;
        await Assert.ThrowsAsync<AggregateException>(() =>
            RetryPolicy.ExecuteAsync<int>(
                _ =>
                {
                    callCount++;
                    throw new InvalidOperationException("Always fails");
                },
                maxAttempts: 3,
                baseDelay: TimeSpan.FromMilliseconds(1),
                // Mark ALL exceptions as non-transient → should stop after 1 try.
                isTransient: _ => false));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_OnRetry_CallbackInvokedWithCorrectAttemptNumber()
    {
        var retryAttempts = new List<int>();
        int callCount     = 0;

        await Assert.ThrowsAsync<AggregateException>(() =>
            RetryPolicy.ExecuteAsync<int>(
                _ =>
                {
                    callCount++;
                    throw new InvalidOperationException("fail");
                },
                maxAttempts: 3,
                baseDelay: TimeSpan.FromMilliseconds(1),
                onRetry: (attempt, _, _) => retryAttempts.Add(attempt)));

        // onRetry called for attempts 1 and 2 (not the final failure).
        Assert.Equal([1, 2], retryAttempts);
    }

    [Fact]
    public async Task ExecuteAsync_MaxAttempts1_DoesNotRetry()
    {
        int callCount = 0;
        await Assert.ThrowsAsync<AggregateException>(() =>
            RetryPolicy.ExecuteAsync<int>(
                _ =>
                {
                    callCount++;
                    throw new InvalidOperationException("fail");
                },
                maxAttempts: 1,
                baseDelay: TimeSpan.FromMilliseconds(1)));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationToken_ThrowsOperationCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            RetryPolicy.ExecuteAsync(
                ct => { ct.ThrowIfCancellationRequested(); return Task.FromResult(1); },
                maxAttempts: 3,
                baseDelay: TimeSpan.FromMilliseconds(1),
                cancellationToken: cts.Token));
    }
}
