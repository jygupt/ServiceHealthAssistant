namespace ServiceHealthAssistant.Repair;

/// <summary>
/// Canonical metadata keys written to and read from the Geneva monitor
/// <c>MonitorConfigMetadata</c> table for the four Brain capability domains.
///
/// Source reference (from Program.cs ServerInstructions):
///   cluster('geneva.kusto.windows.net').database('genevahealthconfigs').MonitorConfigMetadata
///   | extend BrainIntentAutoComms          = Metadata["BrainIntent.AutoComms"]
///   | extend BrainIntentOutageDeclaration  = Metadata["BrainIntent.OutageDeclaration"]
///   | extend BrainIntentDeploymentStops    = Metadata["BrainIntent.DeploymentStops"]
///   | extend BrainIntentBrainAwareness     = Metadata["BrainIntent.BrainAwareness"]
/// </summary>
public static class BrainCapabilityMetadataKeys
{
    public const string BrainAwareness    = "BrainIntent.BrainAwareness";
    public const string OutageDeclaration = "BrainIntent.OutageDeclaration";
    public const string DeploymentStops   = "BrainIntent.DeploymentStops";
    public const string AutoComms         = "BrainIntent.AutoComms";

    /// <summary>All four capability keys in a stable order.</summary>
    public static readonly IReadOnlyList<string> All =
        [BrainAwareness, OutageDeclaration, DeploymentStops, AutoComms];
}
