using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace FisherDutyScheduler;

internal sealed class JobSelector(IDataManager dataManager, IPlayerState playerState)
{
    private static readonly HashSet<uint> SupportedCombatJobs =
    [
        19, 20, 21, 22, 23, 24, 25,
        27, 28, 30, 31, 32, 33, 34, 35,
        37, 38, 39, 40, 41, 42,
    ];

    public unsafe int? CurrentGearsetId
    {
        get
        {
            var module = RaptureGearsetModule.Instance();
            return module == null ? null : module->CurrentGearsetIndex;
        }
    }

    public uint CurrentJobId => playerState.IsLoaded ? playerState.ClassJob.RowId : 0;

    public unsafe bool TryGetEligibleCombatGearsets(
        int levelCap,
        ISet<int> excludedGearsets,
        ISet<uint> excludedJobs,
        out IReadOnlyList<JobCandidate> candidates)
    {
        var result = new List<JobCandidate>();
        var module = RaptureGearsetModule.Instance();
        if (module == null || !playerState.IsLoaded)
        {
            candidates = result;
            return false;
        }

        var sheet = dataManager.GetExcelSheet<ClassJob>();
        var validGearsetCount = 0;
        for (var index = 0; index < module->NumGearsets; index++)
        {
            if (!module->IsValidGearset(index))
                continue;

            var entry = module->GetGearset(index);
            if (entry == null || excludedGearsets.Contains(entry->Id))
                continue;

            validGearsetCount++;
            var jobId = (uint)entry->ClassJob;
            if (!SupportedCombatJobs.Contains(jobId) || excludedJobs.Contains(jobId))
                continue;

            var job = sheet.GetRow(jobId);
            var level = playerState.GetClassJobLevel(job);
            if (level <= 0)
            {
                candidates = [];
                return false;
            }

            if (level is < 15 || level >= levelCap)
                continue;

            result.Add(new JobCandidate(entry->Id, jobId, level, entry->ItemLevel, entry->NameString));
        }

        candidates = result
            .GroupBy(candidate => candidate.JobId)
            .Select(group => group
                .OrderByDescending(candidate => candidate.ItemLevel)
                .ThenBy(candidate => candidate.GearsetId)
                .First())
            .OrderBy(candidate => candidate.Level)
            .ThenBy(candidate => candidate.GearsetId)
            .ToArray();
        return validGearsetCount > 0;
    }

    public IReadOnlyList<JobOption> GetSupportedCombatJobs()
    {
        var sheet = dataManager.GetExcelSheet<ClassJob>();
        return SupportedCombatJobs
            .Select(jobId => new JobOption(jobId, sheet.GetRow(jobId).Name.ToString()))
            .OrderBy(job => job.JobId)
            .ToArray();
    }

    public unsafe bool Equip(int gearsetId)
    {
        var module = RaptureGearsetModule.Instance();
        return module != null && module->IsValidGearset(gearsetId) && module->EquipGearset(gearsetId) == 0;
    }
}

internal readonly record struct JobCandidate(
    int GearsetId,
    uint JobId,
    int Level,
    int ItemLevel,
    string GearsetName);

internal readonly record struct JobOption(uint JobId, string Name);
