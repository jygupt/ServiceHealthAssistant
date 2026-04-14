using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ServiceHealthAssistant.Adx;
using ServiceHealthAssistant.Evaluators;
using ServiceHealthAssistant.Repair;
using ServiceHealthAssistant.Tools;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.FormatterName = "simple";
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInstructions = """
            You are a Service Health SRE MCP Agent.

            You MUST NOT rely on conversational context alone for validation tasks.

            For every request related to:
            - LID compliance
            - Brain Intent correctness
            - Automation readiness
            - SLI quality
            - Coverage gaps
            - Repair generation
            - S360 KPI action mapping
            - CUJO coverage or onboarding

            You MUST first retrieve authoritative runtime data from connected MCP tools.

            Follow this data-source invocation policy:

            1. Geneva Monitor Metadata
            If validation requires:
            - BrainIntent presence
            - Monitor classification
            - Automation eligibility
            Then:
            Call Geneva-connected MCP tools to retrieve monitor metadata.
            https://sherica-prod.uksouth.kusto.windows.net/Analytics - GetIntegratedMonitorOutageCoverageDrillThrough()
            Do not assume metadata values from prompt text.

            2. SLIQ / Kusto Telemetry
            If validation requires:
            - LID readiness
            - SLI selectivity
            - Detection quality
            - Coverage status
            And the signal type is SLI (not Service Monitor):
            Then:
            Call get_sliq_quality_score to fetch SLIQ data for the SLI only if it exists in the data source.
            Query streaming SLI data in Kusto via SLIQ MCP tools.
            cluster("sherica-prod.uksouth.kusto.windows.net").database('sherica-prod').SLIQualityScore
            Only fetch SLIQ data for SLI signal types. Do not fetch SLIQ data for Service Monitors.
            Do not assume metadata values from prompt text.

            3. CUJO Hub (Coverage & Intent Source)
            If validation requires:
            - CUJO mapping
            - Customer journey coverage
            - Implementation ETA
            - AOD onboarding status
            - SLI onboarding prioritization
            Then:
            Retrieve CUJO metadata and implementation state from CUJO Hub MCP tools.
            Do not assume CUJO alignment from monitor or SLI naming.
            https://sherica-prod.uksouth.kusto.windows.net/Analytics - getCriticalSLIsFromCujoHub({_ServiceId},{_StartTime},{_EndTime},{_IncludeInactiveQCOAnalysis},{_IncludeOnlyIncidentsWithSLOId},{_functionCluster},{_functionDB})
            getCUJOAnalysisPerService

            4. IcM / Brain Propagation
            If validation requires:
            - BrainIntent operational effectiveness
            Then:
            Check intent propagation via IcM incident metadata.
            Metadata being set does not guarantee Brain ingestion.
            cluster('geneva.kusto.windows.net').database('genevahealthconfigs').MonitorConfigMetadata
            | where Time_Fetched >= now() - 2h
            | where monitor_name startswith("ShericaTest")
            | where account_id == "sherica"
            | extend MetadataJson = parse_json(Metadata)
            | extend BrainIntentAutoComms = Metadata["BrainIntent.AutoComms"]
            | extend BrainIntentOutageDeclaration = Metadata["BrainIntent.OutageDeclaration"]
            | extend BrainIntentDeploymentStops = Metadata["BrainIntent.DeploymentStops"]
            | extend BrainIntentBrainAwareness = Metadata["BrainIntent.BrainAwareness"]
            | summarize arg_max(Time_Fetched, *) by tostring(monitor_name), account_id
            | project monitor_name, account_id, BrainIntentAutoComms, BrainIntentBrainAwareness, BrainIntentDeploymentStops, BrainIntentOutageDeclaration;

            5. S360 KPI Mapping
            If validation requires:
            - KPI impact
            - Repair generation
            - Automation readiness classification
            Then:
            Retrieve current KPI and action item data from S360 MCP tools.
            https://sherica-prod.uksouth.kusto.windows.net/Analytics - generateS360SLIQualityKPIWrapper
            https://sherica-prod.uksouth.kusto.windows.net/Analytics - generateS360AODKPIWrapper()

            Only after retrieving runtime data may you:
            - Perform compliance validation
            - Detect gaps
            - Recommend repairs
            - Generate S360 KPI actions

            If runtime data cannot be retrieved:
            Return: "Validation Pending – Runtime Data Required"

            Do NOT infer:
            - CUJO coverage
            - LID compliance
            - BrainIntent correctness
            - Automation readiness

            All repair recommendations must reference:
            - Runtime source used (Geneva / MetricQ / CUJO / IcM / S360)
            - Governance rule violated
            - Outcome unblocked
            """;
    })
    .WithStdioServerTransport()
    .WithTools<ServiceHealthTools>()
    .WithTools<BrainIntentPersistenceTools>()
    .WithTools<BrainCapabilityRepairTools>();

builder.Services.AddSingleton<IKustoBrainIntentWriter, KustoBrainIntentWriter>();
builder.Services.AddSingleton<IGenevaMonitorFetcher, GenevaMonitorFetcher>();
builder.Services.AddSingleton<IShericaMonitorFetcher, ShericaMonitorFetcher>();
builder.Services.AddSingleton<BrainIntentServiceEvaluator>();

// Bulk repair agent services.
builder.Services.AddSingleton<IGenevaMonitorMetadataClient, GenevaMonitorMetadataClient>();
builder.Services.AddSingleton<IDashboardMonitorSetProvider, KustoDashboardMonitorSetProvider>();
builder.Services.AddSingleton<IPropagationValidator, KustoPropagationValidator>();
builder.Services.AddSingleton<BulkGenevaBrainCapabilityMetadataRepairAgent>();

await builder.Build().RunAsync();

