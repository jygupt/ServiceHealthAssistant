namespace ServiceHealthAssistant.Models;

// ---------------------------------------------------------------------------
// Enumerations
// ---------------------------------------------------------------------------

public enum SignalType
{
    SLI,
    ServiceMonitor,
    Unknown
}

public enum BrainIntentCategory
{
    CustomerImpact,
    OperationalInfrastructure,
    Unclassified
}

public enum ComplianceStatus
{
    Compliant,
    NonCompliant,
    Partial,
    Unknown
}

public enum GapType
{
    DetectionGap,
    SignalQualityGap,
    AutomationReadinessGap
}

public enum RepairPriority
{
    Critical,
    High,
    Medium,
    Low
}

public enum RepairStatus
{
    Open,
    InProgress,
    Resolved,
    WontFix
}

public enum AutomationReadinessLevel
{
    Ready,
    ConditionallyReady,
    NotReady,
    Blocked
}

public enum S360KpiCategory
{
    Lid,
    Quality,
    Coverage,
    Automation
}
