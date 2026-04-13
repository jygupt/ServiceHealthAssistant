using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ServiceHealthAssistant.Adx;
using ServiceHealthAssistant.Evaluators;
using ServiceHealthAssistant.Models;
using ServiceHealthAssistant.Tools;
using System.Text.Json;

namespace ServiceHealthAssistant.Tests.BrainIntentTests;

/// <summary>
/// Tests that verify the sherica-prod auto-fetch path in
/// <see cref="BrainIntentPersistenceTools.EvaluateServiceBrainIntentAndPersist"/>.
/// </summary>
public class ShericaAutoFetchTests
{
    private static readonly string ServiceId   = "df36aee8-c644-400b-a0ab-fd0f1191211d";
    private static readonly string ServiceName = "App Service (Web Apps)";

    private static BrainIntentPersistenceTools BuildTools(
        IShericaMonitorFetcher shericaFetcher,
        IReadOnlyList<BrainIntentEvaluationRow>? rowsToReturn = null)
    {
        var writerMock = new Mock<IKustoBrainIntentWriter>();
        writerMock
            .Setup(w => w.IngestBatchAsync(
                It.IsAny<IReadOnlyList<BrainIntentEvaluationRow>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var genevaFetcherMock = new Mock<IGenevaMonitorFetcher>();

        var evaluator = new BrainIntentServiceEvaluator(writerMock.Object);

        return new BrainIntentPersistenceTools(
            evaluator,
            genevaFetcherMock.Object,
            shericaFetcher,
            writerMock.Object);
    }

    [Fact]
    public async Task EvaluateServiceBrainIntentAndPersist_NoMonitorsJsonNoGenevaId_UsesShericaFetcher()
    {
        var monitors = new List<MonitorEvaluationInput>
        {
            new("mon-001", "CheckoutAvailability", MonitorType: "MdmMetricMonitor")
        };

        var shericaMock = new Mock<IShericaMonitorFetcher>();
        shericaMock
            .Setup(f => f.FetchMonitorsForServiceAsync(ServiceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(monitors.AsReadOnly());

        var tools = BuildTools(shericaMock.Object);

        var result = await tools.EvaluateServiceBrainIntentAndPersist(
            serviceId:    ServiceId,
            serviceName:  ServiceName,
            monitorsJson: "[]",
            genevaAccountId: "");

        shericaMock.Verify(
            f => f.FetchMonitorsForServiceAsync(ServiceId, It.IsAny<CancellationToken>()),
            Times.Once);

        var json = JsonDocument.Parse(result).RootElement;
        Assert.Equal(1, json.GetProperty("evaluatedCount").GetInt32());
        Assert.Equal(ServiceId, json.GetProperty("serviceId").GetString());
    }

    [Fact]
    public async Task EvaluateServiceBrainIntentAndPersist_GenevaIdProvided_DoesNotUseShericaFetcher()
    {
        var shericaMock = new Mock<IShericaMonitorFetcher>();

        var genevaFetcherMock = new Mock<IGenevaMonitorFetcher>();
        genevaFetcherMock
            .Setup(f => f.FetchMonitorsForAccountAsync("sherica", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MonitorEvaluationInput>
            {
                new("mon-g01", "GenevaMonitor")
            }.AsReadOnly());

        var writerMock = new Mock<IKustoBrainIntentWriter>();
        writerMock
            .Setup(w => w.IngestBatchAsync(
                It.IsAny<IReadOnlyList<BrainIntentEvaluationRow>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var evaluator = new BrainIntentServiceEvaluator(writerMock.Object);
        var tools = new BrainIntentPersistenceTools(
            evaluator, genevaFetcherMock.Object, shericaMock.Object, writerMock.Object);

        var result = await tools.EvaluateServiceBrainIntentAndPersist(
            serviceId:       ServiceId,
            serviceName:     ServiceName,
            monitorsJson:    "[]",
            genevaAccountId: "sherica");

        shericaMock.Verify(
            f => f.FetchMonitorsForServiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var json = JsonDocument.Parse(result).RootElement;
        Assert.Equal(1, json.GetProperty("evaluatedCount").GetInt32());
    }

    [Fact]
    public async Task EvaluateServiceBrainIntentAndPersist_MonitorsJsonProvided_DoesNotUseShericaFetcher()
    {
        var shericaMock = new Mock<IShericaMonitorFetcher>();

        var tools = BuildTools(shericaMock.Object);

        var result = await tools.EvaluateServiceBrainIntentAndPersist(
            serviceId:    ServiceId,
            serviceName:  ServiceName,
            monitorsJson: """[{"Id":"mon-x01","Name":"ExplicitMonitor"}]""",
            genevaAccountId: "");

        shericaMock.Verify(
            f => f.FetchMonitorsForServiceAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var json = JsonDocument.Parse(result).RootElement;
        Assert.Equal(1, json.GetProperty("evaluatedCount").GetInt32());
    }

    [Fact]
    public async Task EvaluateServiceBrainIntentAndPersist_ShericaReturnsEmpty_ReturnsNoMonitorsMessage()
    {
        var shericaMock = new Mock<IShericaMonitorFetcher>();
        shericaMock
            .Setup(f => f.FetchMonitorsForServiceAsync(ServiceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<MonitorEvaluationInput>());

        var tools = BuildTools(shericaMock.Object);

        var result = await tools.EvaluateServiceBrainIntentAndPersist(
            serviceId:    ServiceId,
            serviceName:  ServiceName,
            monitorsJson: "[]",
            genevaAccountId: "");

        var json = JsonDocument.Parse(result).RootElement;
        Assert.Equal(0, json.GetProperty("evaluatedCount").GetInt32());
        Assert.Contains("GetIntegratedMonitorOutageCoverageDrillThrough", json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task EvaluateServiceBrainIntentAndPersist_ShericaThrows_ReturnsErrorJson()
    {
        var shericaMock = new Mock<IShericaMonitorFetcher>();
        shericaMock
            .Setup(f => f.FetchMonitorsForServiceAsync(ServiceId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Kusto connection failed"));

        var tools = BuildTools(shericaMock.Object);

        var result = await tools.EvaluateServiceBrainIntentAndPersist(
            serviceId:    ServiceId,
            serviceName:  ServiceName,
            monitorsJson: "[]",
            genevaAccountId: "");

        var json = JsonDocument.Parse(result).RootElement;
        Assert.True(json.TryGetProperty("error", out var errorProp));
        Assert.Contains("sherica-prod", errorProp.GetString());
        Assert.Contains("Kusto connection failed", errorProp.GetString());
    }

    [Fact]
    public async Task EvaluateServiceBrainIntentAndPersist_ShericaMonitors_MappedFieldsPreserved()
    {
        var monitors = new List<MonitorEvaluationInput>
        {
            new(
                MonitorId:                   "mon-rich",
                MonitorName:                 "RichMonitor",
                MonitorType:                 "MetricAlert",
                LinkedCujoJourney:           "CJ-001",
                OutageDrivingIcmMapping:     true,
                DetectedImpactType:          DetectedImpactType.Customer,
                LidPresence:                 true,
                RegionalScopeDetectable:     true,
                SubscriptionScopeDetectable: true,
                HistoricalPrecision:         HistoricalPrecision.High,
                SignalStability:             SignalStability.Stable,
                LinkedICMIncidentId:         "IcM-42")
        };

        var shericaMock = new Mock<IShericaMonitorFetcher>();
        shericaMock
            .Setup(f => f.FetchMonitorsForServiceAsync(ServiceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(monitors.AsReadOnly());

        var tools = BuildTools(shericaMock.Object);

        var result = await tools.EvaluateServiceBrainIntentAndPersist(
            serviceId:    ServiceId,
            serviceName:  ServiceName,
            monitorsJson: "[]",
            genevaAccountId: "");

        var json = JsonDocument.Parse(result).RootElement;
        Assert.Equal(1, json.GetProperty("evaluatedCount").GetInt32());

        var summaryRow = json.GetProperty("summary").EnumerateArray().First();
        Assert.Equal("mon-rich",    summaryRow.GetProperty("monitorId").GetString());
        Assert.Equal("RichMonitor", summaryRow.GetProperty("monitorName").GetString());
        // Customer impact with all required fields → BrainAwareness = Enabled
        Assert.Equal("Enabled", summaryRow.GetProperty("brainAwareness").GetString());
    }
}
