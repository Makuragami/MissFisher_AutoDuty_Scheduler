using Dalamud.Configuration;

namespace FisherDutyScheduler;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 5;
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public int WaitThresholdMinutes { get; set; } = 40;
    public bool RepeatWhileWindowIsFar { get; set; } = true;
    public int DutyTimeoutMinutes { get; set; } = 90;
    public int EstimatedDutyMinutes { get; set; } = 30;
    public int RecoveryReserveMinutes { get; set; } = 5;
    public int SafetyMarginMinutes { get; set; } = 10;
    public bool RepairFisherGear { get; set; } = true;
    public int FisherRepairThresholdPercent { get; set; } = 30;
    public string MissFisherChecklistId { get; set; } = "ef950191-84e7-40ff-87ab-8d56f9d29572";
    public string MissFisherChecklistName { get; set; } = "新合集";
    public MissFisherResumeKind MissFisherResumeKind { get; set; } = MissFisherResumeKind.Collection;
    public List<uint> ExcludedDutyTerritories { get; set; } = [978];
    public CycleCheckpoint Checkpoint { get; set; } = new();
}

public sealed class CycleCheckpoint
{
    public bool Active { get; set; }
    public string Phase { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; }
    public DateTime? WindowStartUtc { get; set; }
    public DateTime? AutoDutyStartUtc { get; set; }
    public int? FisherGearsetId { get; set; }
    public bool ManagedFisherPause { get; set; }
    public bool AutoDutyOwned { get; set; }
    public bool DutyWasObservedRunning { get; set; }
    public uint? ExpectedDutyTerritoryId { get; set; }
    public string ChecklistId { get; set; } = string.Empty;
    public string ChecklistName { get; set; } = string.Empty;
    public MissFisherResumeKind ResumeKind { get; set; } = MissFisherResumeKind.Collection;
}

public enum MissFisherResumeKind
{
    FishLog,
    Album,
    Collection,
}
