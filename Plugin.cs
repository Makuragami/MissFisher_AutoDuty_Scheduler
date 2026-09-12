using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace FisherDutyScheduler;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/fds";
    private const uint FisherJobId = 18;
    private const int LevelCap = 100;
    private const double MissFisherFallbackSeconds = 4200d;
    private const int AutoDutyStartTimeoutSeconds = 30;

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IPlayerState playerState;
    private readonly IPluginLog log;
    private readonly PluginIpc ipc;
    private readonly JobSelector jobSelector;
    private readonly MissFisherTargetReader targetReader = new();
    private readonly HashSet<int> failedGearsets = [];

    private Configuration configuration;
    private SchedulerState state = SchedulerState.Idle;
    private DateTime stateSinceUtc = DateTime.UtcNow;
    private DateTime nextUpdateUtc = DateTime.MinValue;
    private DateTime nextDryRunNoticeUtc = DateTime.MinValue;
    private DateTime? trackedWindowStartUtc;
    private int? fisherGearsetId;
    private JobCandidate? selectedJob;
    private IReadOnlyList<DutyOption> dutyCandidates = [];
    private int dutyCandidateIndex;
    private DutyOption? selectedDuty;
    private bool managedFisherPause;
    private bool dutyWasObservedRunning;
    private bool autoDutyOwned;
    private bool inventoryFullFallbackCycle;
    private bool observedMissFisherRunning;
    private DateTime? autoDutyStartUtc;
    private string cycleChecklistId = string.Empty;
    private string cycleChecklistName = string.Empty;
    private MissFisherResumeKind cycleResumeKind = MissFisherResumeKind.Collection;
    private bool resumeCommandSent;
    private bool stopRequested;
    private bool recoveryTestMode;
    private DateTime? jobDataUnavailableSinceUtc;
    private DateTime? emptyCandidateSinceUtc;
    private DateTime? fisherRestoreReadySinceUtc;
    private bool configWindowOpen;
    private string status = "等待启用";
    private string lastError = string.Empty;
    private MissFisherSnapshot? lastMissFisher;
    private double? lastResolvedRemainingSeconds;
    private string lastWindowTimeSource = "尚未读取";
    private float? lowestFisherGearPercent;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IFramework framework,
        ICondition condition,
        IPlayerState playerState,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.framework = framework;
        this.condition = condition;
        this.playerState = playerState;
        this.log = log;

        configuration = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        var previousConfigurationVersion = configuration.Version;
        configuration.ExcludedDutyTerritories ??= [];
        configuration.ExcludedDutyTerritories = configuration.ExcludedDutyTerritories.Distinct().ToList();
        configuration.ExcludedJobIds ??= [];
        configuration.ExcludedJobIds = configuration.ExcludedJobIds.Distinct().ToList();
        if (previousConfigurationVersion < 3 && !configuration.ExcludedDutyTerritories.Contains(978))
            configuration.ExcludedDutyTerritories.Add(978);
        if (previousConfigurationVersion < 4)
        {
            if (string.IsNullOrWhiteSpace(configuration.MissFisherChecklistId)
                || string.IsNullOrWhiteSpace(configuration.MissFisherChecklistName))
            {
                configuration.MissFisherResumeKind = MissFisherResumeKind.FishLog;
                configuration.MissFisherChecklistId = "fish-log";
                configuration.MissFisherChecklistName = "鱼类图鉴（非副本）";
            }
            else
            {
                configuration.MissFisherResumeKind = MissFisherResumeKind.Collection;
            }
        }
        configuration.FisherRepairThresholdPercent = Math.Clamp(configuration.FisherRepairThresholdPercent, 1, 99);
        configuration.Version = 7;
        configuration.Checkpoint ??= new CycleCheckpoint();
        ipc = new PluginIpc(pluginInterface, log);
        jobSelector = new JobSelector(dataManager, playerState);
        RestoreCheckpoint();
        SaveConfiguration();

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/fds - open configuration; enable | disable | status | reset | abort | test-restore",
        });
        framework.Update += OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw += DrawConfig;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        pluginInterface.UiBuilder.OpenMainUi += OpenConfig;
    }

    public string Name => "Fisher Duty Scheduler";

    public void Dispose()
    {
        if (configuration.Checkpoint.Active)
            PersistCheckpoint();
        framework.Update -= OnFrameworkUpdate;
        pluginInterface.UiBuilder.Draw -= DrawConfig;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        pluginInterface.UiBuilder.OpenMainUi -= OpenConfig;
        commandManager.RemoveHandler(CommandName);
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        var now = DateTime.UtcNow;
        if (now < nextUpdateUtc)
            return;

        nextUpdateUtc = now.AddSeconds(1);
        try
        {
            Tick(now);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Scheduler update failed");
            Fail("调度器发生未处理错误，已停止自动调度");
        }
    }

    private void Tick(DateTime now)
    {
        if (state == SchedulerState.Faulted)
            return;

        MissFisherSnapshot? fisher = null;
        if (ipc.TryGetMissFisher(out var fisherSnapshot))
        {
            fisher = fisherSnapshot;
            lastMissFisher = fisherSnapshot;
        }

        if (!configuration.Enabled && state == SchedulerState.Idle)
        {
            if (fisher is null)
            {
                status = "调度已关闭；MissFisher IPC 不可用";
                return;
            }

            var resolved = TryResolveWindowRemaining(now, fisher.Value, out var remainingSeconds, out var timeSource);
            lastResolvedRemainingSeconds = resolved ? remainingSeconds : null;
            lastWindowTimeSource = timeSource;
            status = resolved
                ? $"调度已关闭；当前目标还有 {FormatDuration(remainingSeconds)}（{timeSource}）"
                : $"调度已关闭；无法取得可靠窗口时间（{timeSource}）";
            return;
        }

        if (stopRequested)
        {
            if (state == SchedulerState.Reconciling)
            {
                if (autoDutyOwned) ipc.TryStopAutoDuty();
                BeginFisherRestore("对账期间收到停止请求，正在恢复捕鱼职业");
                return;
            }

            if (state is SchedulerState.PausingFisher
                or SchedulerState.PausingFisherForRepair
                or SchedulerState.SelectingCombatJob
                or SchedulerState.EquippingCombatJob
                or SchedulerState.StartingDuty)
            {
                BeginFisherRestore("已请求停止，正在恢复捕鱼职业");
                return;
            }

            if (state == SchedulerState.WaitingForDutyStart)
            {
                if (autoDutyOwned) ipc.TryStopAutoDuty();
                BeginFisherRestore("已取消 AutoDuty 启动，正在恢复捕鱼职业");
                return;
            }

            if (state == SchedulerState.RunningDuty)
            {
                if (autoDutyOwned) ipc.TryStopAutoDuty();
                BeginFisherRestore("已请求停止 AutoDuty，离本后恢复捕鱼职业");
                return;
            }
        }

        switch (state)
        {
            case SchedulerState.Idle:
                if (fisher is { } idleFisher)
                    TickIdle(now, idleFisher);
                else
                    status = "等待 MissFisher IPC；不会启动新任务";
                break;
            case SchedulerState.Reconciling:
                TickReconciling(now);
                break;
            case SchedulerState.PausingFisherForRepair:
                if (fisher is { } repairPausingFisher)
                    TickPausingFisherForRepair(now, repairPausingFisher);
                else if (Elapsed(now) > TimeSpan.FromSeconds(20))
                    BeginFisherRestore("MissFisher 在修理暂停阶段失联，开始恢复");
                else
                    status = "等待 MissFisher IPC 确认修理前暂停";
                break;
            case SchedulerState.RepairingFisherGear:
                TickRepairingFisherGear(now);
                break;
            case SchedulerState.PausingFisher:
                if (fisher is { } pausingFisher)
                    TickPausingFisher(now, pausingFisher);
                else if (Elapsed(now) > TimeSpan.FromSeconds(20))
                    BeginFisherRestore("MissFisher 在暂停确认阶段失联，开始恢复");
                else
                    status = "等待 MissFisher IPC 确认暂停";
                break;
            case SchedulerState.SelectingCombatJob:
                TickSelectingJob(now);
                break;
            case SchedulerState.EquippingCombatJob:
                TickEquippingJob(now);
                break;
            case SchedulerState.StartingDuty:
                TickStartingDuty();
                break;
            case SchedulerState.WaitingForDutyStart:
                TickWaitingForDutyStart(now);
                break;
            case SchedulerState.RunningDuty:
                TickRunningDuty(now, fisher);
                break;
            case SchedulerState.RestoringFisher:
                TickRestoringFisher(now);
                break;
            case SchedulerState.ResumingFisher:
                TickResumingFisher(now, fisher);
                break;
            case SchedulerState.RestartingFisher:
                TickRestartingFisher(now, fisher);
                break;
        }
    }

    private void TickReconciling(DateTime now)
    {
        if (ipc.TryGetAutoDuty(out var autoDuty))
        {
            if (IsBoundByDuty() || (autoDutyOwned && (!autoDuty.IsStopped || autoDuty.IsLooping)))
            {
                dutyWasObservedRunning = true;
                PersistCheckpoint();
                Transition(SchedulerState.RunningDuty, "已从持久化检查点接管本轮 AutoDuty 监控");
                return;
            }

            BeginFisherRestore("检查点对账完成，正在恢复捕鱼职业");
            return;
        }

        status = IsBoundByDuty()
            ? "正在副本中，等待 AutoDuty IPC 恢复"
            : "等待 AutoDuty IPC 对账；超时后继续恢复捕鱼";
        if (!IsBoundByDuty() && Elapsed(now) > TimeSpan.FromSeconds(15))
            BeginFisherRestore("AutoDuty IPC 未恢复且角色不在副本，继续恢复捕鱼职业");
    }

    private void TickIdle(DateTime now, MissFisherSnapshot fisher)
    {
        if (!configuration.Enabled)
            return;

        UpdateInventoryFullFallback(fisher);

        if (!ipc.TryGetAutoDuty(out var autoDuty))
        {
            status = "等待 AutoDuty IPC";
            return;
        }

        if (TryHandleInventoryFullFallback(fisher, autoDuty))
            return;

        if (TryBeginFisherGearRepair(fisher, autoDuty))
            return;

        var requiredGapSeconds = GetRequiredGapSeconds();
        var validRemaining = TryResolveWindowRemaining(now, fisher, out var remainingSeconds, out var timeSource);
        lastResolvedRemainingSeconds = validRemaining ? remainingSeconds : null;
        lastWindowTimeSource = timeSource;
        var eligible = fisher.IsRunning
            && fisher.IsWaiting
            && !fisher.IsPaused
            && !fisher.IsWindowActive
            && !fisher.IsInWindow
            && !fisher.IsAutoPreparing
            && validRemaining
            && remainingSeconds > requiredGapSeconds
            && autoDuty.IsStopped
            && !autoDuty.IsLooping
            && playerState.IsLoaded
            && playerState.ClassJob.RowId == FisherJobId
            && IsSafeForGearsetChange();

        status = eligible
            ? $"检测到等待 {FormatDuration(remainingSeconds)}（{timeSource}）"
            : DescribeIdle(fisher, autoDuty, validRemaining, remainingSeconds, timeSource);

        if (!eligible)
            return;

        if (configuration.DryRun)
        {
            status += "；只观察模式不会执行";
            if (now >= nextDryRunNoticeUtc)
            {
                log.Information(
                    "Dry run: eligible MissFisher gap detected ({Seconds:F0}s, source {Source})",
                    remainingSeconds,
                    timeSource);
                nextDryRunNoticeUtc = now.AddMinutes(1);
            }
            return;
        }

        fisherGearsetId = jobSelector.CurrentGearsetId;
        if (fisherGearsetId is null)
        {
            Fail("无法读取当前捕鱼套装");
            return;
        }

        failedGearsets.Clear();
        selectedJob = null;
        stopRequested = false;
        dutyWasObservedRunning = false;
        trackedWindowStartUtc = AddSecondsClamped(now, remainingSeconds);
        cycleChecklistId = configuration.MissFisherChecklistId;
        cycleChecklistName = configuration.MissFisherChecklistName.Trim();
        cycleResumeKind = configuration.MissFisherResumeKind;
        managedFisherPause = true;
        inventoryFullFallbackCycle = false;
        autoDutyOwned = false;
        autoDutyStartUtc = null;
        configuration.Checkpoint.Active = true;
        PersistCheckpoint();
        log.Information(
            "Captured MissFisher window deadline {Deadline:u} from {Seconds:F0}s remaining ({Source})",
            trackedWindowStartUtc.Value,
            remainingSeconds,
            timeSource);
        commandManager.ProcessCommand("/mf pause");
        Transition(SchedulerState.PausingFisher, "正在暂停 MissFisher");
    }

    private void UpdateInventoryFullFallback(MissFisherSnapshot fisher)
    {
        if (fisher.IsRunning)
        {
            observedMissFisherRunning = true;
            if (configuration.InventoryFullFallbackActive)
            {
                configuration.InventoryFullFallbackActive = false;
                SaveConfiguration();
                log.Information("MissFisher is running again; inventory-full fallback cleared");
            }
            return;
        }

        if (!configuration.ContinueDutiesWhenInventoryFull)
        {
            configuration.InventoryFullFallbackActive = false;
            return;
        }

        if (!configuration.InventoryFullFallbackActive
            && observedMissFisherRunning
            && InventoryCapacity.TryGetEmptyBagSlots(out var emptySlots)
            && emptySlots == 0)
        {
            observedMissFisherRunning = false;
            configuration.InventoryFullFallbackActive = true;
            SaveConfiguration();
            log.Warning("MissFisher stopped while the main inventory was full; duty fallback activated");
        }
    }

    private bool TryHandleInventoryFullFallback(MissFisherSnapshot fisher, AutoDutySnapshot autoDuty)
    {
        if (!configuration.ContinueDutiesWhenInventoryFull
            || !configuration.InventoryFullFallbackActive
            || fisher.IsRunning)
            return false;

        if (!InventoryCapacity.TryGetEmptyBagSlots(out var emptySlots))
        {
            status = "背包满兜底已激活，等待背包数据";
            return true;
        }

        if (emptySlots > 0)
        {
            if (targetReader.TryStartResumeTarget(
                    configuration.MissFisherResumeKind,
                    configuration.MissFisherChecklistId,
                    configuration.MissFisherChecklistName,
                    out var failureMessage))
            {
                status = $"背包已有 {emptySlots} 个空格，正在恢复 MissFisher“{configuration.MissFisherChecklistName}”";
                log.Information(
                    "Inventory has {EmptySlots} empty slots; restarted MissFisher {Checklist}",
                    emptySlots,
                    configuration.MissFisherChecklistName);
                configuration.InventoryFullFallbackActive = false;
                SaveConfiguration();
            }
            else
            {
                status = $"背包已有空格，但恢复 MissFisher 失败：{failureMessage}";
            }
            return true;
        }

        if (!autoDuty.IsStopped || autoDuty.IsLooping)
        {
            status = "背包已满；等待 AutoDuty 空闲";
            return true;
        }

        if (!playerState.IsLoaded || playerState.ClassJob.RowId != FisherJobId)
        {
            status = "背包已满；等待切回捕鱼职业";
            return true;
        }

        if (!IsSafeForGearsetChange())
        {
            status = "背包已满；等待角色可安全切换套装";
            return true;
        }

        if (configuration.DryRun)
        {
            status = "背包已满；只观察模式不会启动 AutoDuty";
            return true;
        }

        fisherGearsetId = jobSelector.CurrentGearsetId;
        if (fisherGearsetId is null)
        {
            Fail("背包满兜底调度无法读取当前捕鱼套装");
            return true;
        }

        failedGearsets.Clear();
        selectedJob = null;
        trackedWindowStartUtc = null;
        cycleChecklistId = configuration.MissFisherChecklistId;
        cycleChecklistName = configuration.MissFisherChecklistName.Trim();
        cycleResumeKind = configuration.MissFisherResumeKind;
        managedFisherPause = true;
        inventoryFullFallbackCycle = true;
        autoDutyOwned = false;
        stopRequested = false;
        configuration.Checkpoint.Active = true;
        PersistCheckpoint();
        Transition(SchedulerState.SelectingCombatJob, "MissFisher 因背包满停止，继续 AutoDuty 调度");
        return true;
    }

    private bool TryBeginFisherGearRepair(MissFisherSnapshot fisher, AutoDutySnapshot autoDuty)
    {
        lowestFisherGearPercent = null;
        if (!configuration.RepairFisherGear
            || !playerState.IsLoaded
            || playerState.ClassJob.RowId != FisherJobId
            || !EquipmentDurability.TryGetLowestEquippedPercent(out var lowestPercent))
            return false;

        lowestFisherGearPercent = lowestPercent;
        var repairThreshold = configuration.FisherRepairThresholdPercent;
        if (ipc.TryGetAutoDutyRepairSettings(out var selfRepair, out var autoDutyThreshold) && selfRepair)
            repairThreshold = Math.Min(repairThreshold, autoDutyThreshold);
        if (lowestPercent > repairThreshold)
            return false;

        var urgent = lowestPercent <= 0.01f;
        var safeFishingGap = fisher.IsWaiting
            && !fisher.IsWindowActive
            && !fisher.IsInWindow
            && !fisher.IsAutoPreparing;
        if (!urgent && !safeFishingGap)
        {
            status = $"捕鱼装备最低耐久 {lowestPercent:F0}%，等待钓鱼空档后修理";
            return true;
        }

        if (fisher.IsPaused)
        {
            status = $"捕鱼装备最低耐久 {lowestPercent:F0}%；MissFisher 已手动暂停，不自动修理";
            return true;
        }

        if (!autoDuty.IsStopped || autoDuty.IsLooping)
        {
            status = $"捕鱼装备最低耐久 {lowestPercent:F0}%，等待 AutoDuty 空闲后修理";
            return true;
        }

        if (!IsSafeForGearsetChange())
        {
            status = $"捕鱼装备最低耐久 {lowestPercent:F0}%，等待角色可操作后修理";
            return true;
        }

        if (configuration.DryRun)
        {
            status = $"捕鱼装备最低耐久 {lowestPercent:F0}%；只观察模式不会修理";
            return true;
        }

        fisherGearsetId = jobSelector.CurrentGearsetId;
        if (fisherGearsetId is null)
        {
            Fail("修理前无法读取当前捕鱼套装");
            return true;
        }

        cycleChecklistId = configuration.MissFisherChecklistId;
        cycleChecklistName = configuration.MissFisherChecklistName.Trim();
        cycleResumeKind = configuration.MissFisherResumeKind;
        managedFisherPause = true;
        autoDutyOwned = false;
        stopRequested = false;
        configuration.Checkpoint.Active = true;
        PersistCheckpoint();

        if (fisher.IsRunning)
        {
            commandManager.ProcessCommand("/mf pause");
            Transition(SchedulerState.PausingFisherForRepair, $"捕鱼装备最低耐久 {lowestPercent:F0}%，正在暂停 MissFisher");
        }
        else
        {
            StartFisherGearRepair(lowestPercent);
        }

        return true;
    }

    private void TickPausingFisherForRepair(DateTime now, MissFisherSnapshot fisher)
    {
        if (fisher.IsPaused || !fisher.IsRunning)
        {
            var percent = EquipmentDurability.TryGetLowestEquippedPercent(out var current) ? current : 0;
            StartFisherGearRepair(percent);
            return;
        }

        if (Elapsed(now) > TimeSpan.FromSeconds(20))
            Fail("MissFisher 未在 20 秒内进入修理暂停状态");
    }

    private void StartFisherGearRepair(float lowestPercent)
    {
        commandManager.ProcessCommand("/ad repair");
        Transition(SchedulerState.RepairingFisherGear, $"捕鱼装备最低耐久 {lowestPercent:F0}%，已请求 AutoDuty 修理");
    }

    private void TickRepairingFisherGear(DateTime now)
    {
        if (!EquipmentDurability.TryGetLowestEquippedPercent(out var lowestPercent))
        {
            status = "正在等待捕鱼装备耐久数据";
            if (Elapsed(now) > TimeSpan.FromMinutes(10))
                FailFisherGearRepair("十分钟内未能确认捕鱼装备修理完成");
            return;
        }

        lowestFisherGearPercent = lowestPercent;
        var completionThreshold = configuration.FisherRepairThresholdPercent;
        if (ipc.TryGetAutoDutyRepairSettings(out var selfRepair, out var autoDutyThreshold) && selfRepair)
            completionThreshold = Math.Min(completionThreshold, autoDutyThreshold);
        if (lowestPercent > completionThreshold)
        {
            log.Information("Fisher gear repair completed; lowest durability is {Percent:F0}%", lowestPercent);
            Transition(SchedulerState.ResumingFisher, $"捕鱼装备已修理，最低耐久 {lowestPercent:F0}%");
            return;
        }

        status = $"正在修理捕鱼装备；当前最低耐久 {lowestPercent:F0}%";
        if (Elapsed(now) > TimeSpan.FromMinutes(10))
            FailFisherGearRepair("AutoDuty 十分钟内未完成捕鱼装备修理；请检查金币或修理设置");
    }

    private void FailFisherGearRepair(string message)
    {
        managedFisherPause = false;
        configuration.Checkpoint = new CycleCheckpoint();
        FailTerminal(message);
        SaveConfiguration();
    }

    private void TickPausingFisher(DateTime now, MissFisherSnapshot fisher)
    {
        if (fisher.IsPaused)
        {
            Transition(SchedulerState.SelectingCombatJob, "MissFisher 已暂停，正在选择职业");
            return;
        }

        if (Elapsed(now) > TimeSpan.FromSeconds(20))
            Fail("MissFisher 未在 20 秒内进入暂停状态");
    }

    private void TickSelectingJob(DateTime now)
    {
        if (stopRequested)
        {
            BeginFisherRestore("已请求停止，正在恢复捕鱼职业");
            return;
        }

        if (!IsSafeForGearsetChange())
        {
            jobDataUnavailableSinceUtc ??= now;
            emptyCandidateSinceUtc = null;
            status = "等待角色与区域加载稳定后读取战斗职业";
            if (now - jobDataUnavailableSinceUtc > TimeSpan.FromSeconds(30))
            {
                configuration.Enabled = false;
                SaveConfiguration();
                BeginFisherRestore("角色或区域 30 秒内未就绪，已停止调度并恢复捕鱼");
            }
            return;
        }

        if (!jobSelector.TryGetEligibleCombatGearsets(
                LevelCap,
                failedGearsets,
                configuration.ExcludedJobIds.ToHashSet(),
                out var candidates))
        {
            jobDataUnavailableSinceUtc ??= now;
            emptyCandidateSinceUtc = null;
            status = "战斗职业套装数据暂不可用，正在重试";
            if (now - jobDataUnavailableSinceUtc > TimeSpan.FromSeconds(30))
            {
                configuration.Enabled = false;
                SaveConfiguration();
                BeginFisherRestore("战斗职业套装数据 30 秒内未就绪，已停止调度并恢复捕鱼");
            }
            return;
        }

        jobDataUnavailableSinceUtc = null;
        var candidate = candidates.FirstOrDefault();
        if (candidate == default)
        {
            emptyCandidateSinceUtc ??= now;
            var confirmedFor = now - emptyCandidateSinceUtc.Value;
            if (confirmedFor < TimeSpan.FromSeconds(15))
            {
                var remaining = Math.Max(1, 15 - (int)confirmedFor.TotalSeconds);
                status = failedGearsets.Count > 0
                    ? $"当前候选职业均无法装备，继续复核 {remaining} 秒"
                    : $"当前未读取到 15-99 级战斗职业，继续复核 {remaining} 秒";
                return;
            }

            configuration.Enabled = false;
            SaveConfiguration();
            BeginFisherRestore(failedGearsets.Count > 0
                ? "所有候选战斗职业均无法装备，已停止调度"
                : "连续 15 秒确认没有等级 15-99 且带套装的可用战斗职业");
            return;
        }

        emptyCandidateSinceUtc = null;
        selectedJob = candidate;
        dutyCandidates = [];
        dutyCandidateIndex = 0;
        selectedDuty = null;
        if (jobSelector.CurrentGearsetId == candidate.GearsetId && jobSelector.CurrentJobId == candidate.JobId)
        {
            Transition(SchedulerState.StartingDuty, $"使用 {candidate.GearsetName}（{candidate.Level}级）");
            return;
        }

        if (!IsSafeForGearsetChange() || !jobSelector.Equip(candidate.GearsetId))
        {
            failedGearsets.Add(candidate.GearsetId);
            status = $"无法装备 {candidate.GearsetName}，尝试下一个职业";
            return;
        }

        Transition(SchedulerState.EquippingCombatJob, $"正在切换至 {candidate.GearsetName}（{candidate.Level}级）");
    }

    private void TickEquippingJob(DateTime now)
    {
        if (selectedJob is null)
        {
            Fail("职业选择状态丢失");
            return;
        }

        if (jobSelector.CurrentJobId == selectedJob.Value.JobId)
        {
            if (recoveryTestMode)
            {
                if (Elapsed(now) < TimeSpan.FromSeconds(3))
                {
                    status = $"恢复测试：已切换至 {selectedJob.Value.GearsetName}，等待 MissFisher 处理职业变化";
                    return;
                }

                BeginFisherRestore("恢复测试：正在切回捕鱼职业");
                return;
            }

            Transition(SchedulerState.StartingDuty, $"已切换至 {selectedJob.Value.GearsetName}");
            return;
        }

        if (Elapsed(now) > TimeSpan.FromSeconds(20))
        {
            failedGearsets.Add(selectedJob.Value.GearsetId);
            selectedJob = null;
            Transition(SchedulerState.SelectingCombatJob, "职业切换超时，尝试下一个职业");
        }
    }

    private void TickStartingDuty()
    {
        if (recoveryTestMode)
        {
            BeginFisherRestore("恢复测试：正在切回捕鱼职业");
            return;
        }

        if (!IsSafeForGearsetChange())
        {
            status = "等待角色可操作后启动 AutoDuty";
            return;
        }

        if (!ipc.TryGetAutoDuty(out var autoDuty) || !autoDuty.IsStopped)
        {
            status = "等待 AutoDuty 停止状态";
            return;
        }

        if (selectedJob is null)
        {
            Fail("启动副本时职业信息已丢失");
            return;
        }

        if (dutyCandidates.Count == 0)
        {
            var candidates = new List<DutyOption>();
            foreach (var duty in DutyCatalog.SupportLevelingDuties
                         .Where(duty => duty.MinimumLevel <= selectedJob.Value.Level)
                         .Where(duty => !configuration.ExcludedDutyTerritories.Contains(duty.TerritoryId))
                         .OrderByDescending(duty => duty.MinimumLevel))
            {
                if (!ipc.TryContentHasPath(duty.TerritoryId, out var hasPath))
                {
                    status = "无法读取 AutoDuty 路径清单，等待 IPC 恢复";
                    return;
                }

                if (hasPath)
                    candidates.Add(duty);
            }

            dutyCandidates = candidates;
            dutyCandidateIndex = 0;
        }

        if (dutyCandidateIndex >= dutyCandidates.Count)
        {
            configuration.Enabled = false;
            SaveConfiguration();
            BeginFisherRestore("没有未排除且带路径的可用练级副本，已关闭调度");
            return;
        }

        selectedDuty = dutyCandidates[dutyCandidateIndex];

        autoDutyOwned = true;
        autoDutyStartUtc = DateTime.UtcNow;
        dutyWasObservedRunning = false;
        PersistCheckpoint();
        commandManager.ProcessCommand("/ad config leveling None");
        commandManager.ProcessCommand($"/ad run Support {selectedDuty.Value.TerritoryId} 1");
        Transition(
            SchedulerState.WaitingForDutyStart,
            $"已请求 AutoDuty 运行 {selectedDuty.Value.Name}（{selectedDuty.Value.TerritoryId}）");
    }

    private void TickWaitingForDutyStart(DateTime now)
    {
        if (ipc.TryGetAutoDuty(out var autoDuty)
            && (autoDuty.IsLooping || autoDuty.IsNavigating || !autoDuty.IsStopped))
        {
            dutyWasObservedRunning = true;
            PersistCheckpoint();
            Transition(SchedulerState.RunningDuty, $"AutoDuty 已启动：{selectedDuty?.Name ?? "未知副本"}");
            return;
        }

        if (Elapsed(now) <= TimeSpan.FromSeconds(AutoDutyStartTimeoutSeconds))
            return;

        var failedDuty = selectedDuty;
        autoDutyOwned = false;
        autoDutyStartUtc = null;
        selectedDuty = null;
        dutyCandidateIndex++;
        if (dutyCandidateIndex < dutyCandidates.Count)
        {
            Transition(
                SchedulerState.StartingDuty,
                $"{failedDuty?.Name ?? "候选副本"} 未启动，尝试下一个未排除副本");
            return;
        }

        configuration.Enabled = false;
        SaveConfiguration();
        BeginFisherRestore("所有未排除的候选副本均未能启动，已关闭调度并恢复捕鱼职业");
    }

    private void TickRunningDuty(DateTime now, MissFisherSnapshot? fisher)
    {
        var dutyElapsed = now - (autoDutyStartUtc ?? stateSinceUtc);
        if (dutyElapsed > TimeSpan.FromMinutes(configuration.DutyTimeoutMinutes))
        {
            if (autoDutyOwned)
            {
                ipc.TryStopAutoDuty();
                commandManager.ProcessCommand("/ad exitduty");
            }
            configuration.Enabled = false;
            SaveConfiguration();
            BeginFisherRestore("AutoDuty 超过设定时限，已请求停止并退出副本；离本后自动恢复捕鱼");
            return;
        }

        if (!ipc.TryGetAutoDuty(out var autoDuty))
        {
            status = "AutoDuty 运行中；暂时无法读取 IPC";
            return;
        }

        if (autoDuty.IsLooping || autoDuty.IsNavigating || !autoDuty.IsStopped)
        {
            dutyWasObservedRunning = true;
            status = "AutoDuty 正在运行";
            return;
        }

        if (!dutyWasObservedRunning || IsBoundByDuty())
        {
            status = "AutoDuty 已结束，等待离开副本";
            return;
        }

        var trackedRemaining = GetTrackedRemainingSeconds(now);
        log.Information(
            "Duty finished: tracked remaining {TrackedSeconds:F0}s; MissFisher IPC returned {IpcSeconds:F0}s",
            trackedRemaining,
            fisher?.SecondsUntilNextWindow ?? double.NaN);

        if (!stopRequested
            && inventoryFullFallbackCycle
            && configuration.RepeatWhileWindowIsFar
            && InventoryCapacity.TryGetEmptyBagSlots(out var emptySlots)
            && emptySlots == 0)
        {
            selectedJob = null;
            failedGearsets.Clear();
            Transition(SchedulerState.SelectingCombatJob, "背包仍满，准备下一轮");
            return;
        }

        if (!stopRequested
            && configuration.RepeatWhileWindowIsFar
            && double.IsFinite(trackedRemaining)
            && trackedRemaining > GetRequiredGapSeconds())
        {
            selectedJob = null;
            failedGearsets.Clear();
            Transition(SchedulerState.SelectingCombatJob, $"窗口估算仍有 {FormatDuration(trackedRemaining)}，准备下一轮");
            return;
        }

        BeginFisherRestore("副本结束，正在恢复捕鱼职业");
    }

    private void BeginFisherRestore(string message)
    {
        if (fisherGearsetId is null)
        {
            FailTerminal("原捕鱼套装未知，无法自动恢复");
            return;
        }

        fisherRestoreReadySinceUtc = null;
        Transition(SchedulerState.RestoringFisher, message);
    }

    private void TickRestoringFisher(DateTime now)
    {
        if (IsBoundByDuty())
        {
            status = "等待离开副本后恢复捕鱼职业";
            return;
        }

        fisherRestoreReadySinceUtc ??= now;
        var restoreElapsed = now - fisherRestoreReadySinceUtc.Value;

        if (autoDutyOwned)
        {
            if (!ipc.TryGetAutoDuty(out var autoDuty))
            {
                if (restoreElapsed <= TimeSpan.FromSeconds(15))
                {
                    status = "AutoDuty IPC 不可用；确认角色已离本后继续恢复";
                    return;
                }
            }
            else if (!autoDuty.IsStopped || autoDuty.IsLooping)
            {
                status = "等待 AutoDuty 完全停止后恢复捕鱼职业";
                return;
            }
        }

        if (jobSelector.CurrentJobId == FisherJobId)
        {
            Transition(SchedulerState.ResumingFisher, "捕鱼职业已恢复");
            return;
        }

        if (fisherGearsetId is not null && IsSafeForGearsetChange() && jobSelector.Equip(fisherGearsetId.Value))
        {
            status = "已发送捕鱼套装切换请求";
            return;
        }

        if (restoreElapsed > TimeSpan.FromSeconds(30))
            Fail($"无法恢复原捕鱼套装；请手动切回捕鱼职业并恢复“{cycleChecklistName}”");
    }

    private void TickResumingFisher(DateTime now, MissFisherSnapshot? fisher)
    {
        if (inventoryFullFallbackCycle
            && InventoryCapacity.TryGetEmptyBagSlots(out var emptySlots)
            && emptySlots == 0)
        {
            CompleteCycle("背包仍满，保持 AutoDuty 兜底调度");
            return;
        }

        if (!managedFisherPause)
        {
            CompleteCycle("任务完成；MissFisher 并非由调度器暂停");
            return;
        }

        if (fisher is null)
        {
            status = "捕鱼职业已恢复，等待 MissFisher IPC 后恢复清单";
            if (Elapsed(now) > TimeSpan.FromMinutes(2))
                FailTerminal("MissFisher IPC 两分钟内未恢复；捕鱼职业已恢复，请手动启动清单");
            return;
        }

        if (!fisher.Value.IsRunning)
        {
            if (Elapsed(now) < TimeSpan.FromSeconds(2))
            {
                status = "正在确认 MissFisher 运行状态";
                return;
            }

            if (string.IsNullOrWhiteSpace(cycleChecklistName))
            {
                Fail("MissFisher 恢复目标名称为空");
                return;
            }

            if (!targetReader.TryStartResumeTarget(
                    cycleResumeKind,
                    cycleChecklistId,
                    cycleChecklistName,
                    out var failureMessage))
            {
                FailTerminal($"无法重新启动 MissFisher“{cycleChecklistName}”：{failureMessage}");
                return;
            }

            Transition(
                SchedulerState.RestartingFisher,
                $"MissFisher 会话已丢失，正在重新启动“{cycleChecklistName}”");
            return;
        }

        if (!fisher.Value.IsPaused)
        {
            CompleteCycle($"已恢复 MissFisher“{cycleChecklistName}”");
            return;
        }

        if (Elapsed(now) < TimeSpan.FromSeconds(2))
            return;

        if (!resumeCommandSent)
        {
            commandManager.ProcessCommand("/mf pause");
            resumeCommandSent = true;
            status = "正在恢复 MissFisher";
        }

        if (Elapsed(now) > TimeSpan.FromSeconds(20))
            Fail($"MissFisher 未能恢复；请手动打开并继续“{cycleChecklistName}”");
    }

    private void TickRestartingFisher(DateTime now, MissFisherSnapshot? fisher)
    {
        if (fisher is null)
        {
            status = "等待 MissFisher IPC 确认清单启动";
            if (Elapsed(now) > TimeSpan.FromMinutes(2))
                FailTerminal("无法确认 MissFisher 清单启动；捕鱼职业已恢复，请手动检查");
            return;
        }

        if (fisher.Value.IsRunning)
        {
            if (fisher.Value.IsPaused)
            {
                Transition(SchedulerState.ResumingFisher, "清单已重新启动但仍处于暂停状态，正在继续");
                return;
            }

            CompleteCycle($"已重新启动 MissFisher“{cycleChecklistName}”");
            return;
        }

        status = $"等待 MissFisher 启动“{cycleChecklistName}”";
        if (Elapsed(now) > TimeSpan.FromSeconds(30))
            FailTerminal($"MissFisher 未能启动“{cycleChecklistName}”；请手动检查清单");
    }

    private void CompleteCycle(string message)
    {
        log.Information("{Message}", message);
        status = message;
        lastError = string.Empty;
        trackedWindowStartUtc = null;
        fisherGearsetId = null;
        selectedJob = null;
        dutyCandidates = [];
        dutyCandidateIndex = 0;
        selectedDuty = null;
        managedFisherPause = false;
        dutyWasObservedRunning = false;
        resumeCommandSent = false;
        stopRequested = false;
        recoveryTestMode = false;
        fisherRestoreReadySinceUtc = null;
        autoDutyOwned = false;
        autoDutyStartUtc = null;
        cycleChecklistId = string.Empty;
        cycleChecklistName = string.Empty;
        cycleResumeKind = MissFisherResumeKind.Collection;
        configuration.Checkpoint = new CycleCheckpoint();
        SaveConfiguration();
        Transition(SchedulerState.Idle, message);
        nextUpdateUtc = DateTime.UtcNow.AddSeconds(5);
    }

    private void Fail(string message)
    {
        lastError = message;
        configuration.Enabled = false;
        SaveConfiguration();
        log.Error("{Message}", message);

        if (configuration.Checkpoint.Active
            && fisherGearsetId is not null
            && state is not SchedulerState.RestoringFisher
                and not SchedulerState.ResumingFisher
                and not SchedulerState.RestartingFisher)
        {
            if (autoDutyOwned)
                ipc.TryStopAutoDuty();
            BeginFisherRestore($"{message}；正在执行恢复");
            return;
        }

        FailTerminal(message);
    }

    private void FailTerminal(string message)
    {
        lastError = message;
        status = message;
        configuration.Enabled = false;
        state = SchedulerState.Faulted;
        stateSinceUtc = DateTime.UtcNow;
        PersistCheckpoint();
        log.Error("{Message}", message);
    }

    private void ResetFault()
    {
        lastError = string.Empty;
        stopRequested = false;
        selectedJob = null;
        dutyCandidates = [];
        dutyCandidateIndex = 0;
        selectedDuty = null;
        failedGearsets.Clear();
        recoveryTestMode = false;
        fisherRestoreReadySinceUtc = null;
        jobDataUnavailableSinceUtc = null;
        emptyCandidateSinceUtc = null;
        if (configuration.Checkpoint.Active)
        {
            RestoreCheckpoint();
            return;
        }

        trackedWindowStartUtc = null;
        state = SchedulerState.Idle;
        stateSinceUtc = DateTime.UtcNow;
        status = "状态已重置";
    }

    private void Transition(SchedulerState next, string message)
    {
        state = next;
        stateSinceUtc = DateTime.UtcNow;
        if (next == SchedulerState.ResumingFisher)
            resumeCommandSent = false;
        if (next == SchedulerState.SelectingCombatJob)
        {
            jobDataUnavailableSinceUtc = null;
            emptyCandidateSinceUtc = null;
        }
        status = message;
        log.Information("State -> {State}: {Message}", next, message);
        if (configuration.Checkpoint.Active)
            PersistCheckpoint();
    }

    private void RestoreCheckpoint()
    {
        configuration.Checkpoint ??= new CycleCheckpoint();
        var checkpoint = configuration.Checkpoint;
        if (!checkpoint.Active)
            return;

        trackedWindowStartUtc = checkpoint.WindowStartUtc;
        fisherGearsetId = checkpoint.FisherGearsetId;
        managedFisherPause = checkpoint.ManagedFisherPause;
        autoDutyOwned = checkpoint.AutoDutyOwned;
        autoDutyStartUtc = checkpoint.AutoDutyStartUtc;
        dutyWasObservedRunning = checkpoint.DutyWasObservedRunning;
        inventoryFullFallbackCycle = checkpoint.InventoryFullFallbackCycle;
        selectedDuty = checkpoint.ExpectedDutyTerritoryId is { } territoryId
            ? DutyCatalog.Find(territoryId)
            : null;
        cycleChecklistId = checkpoint.ChecklistId;
        cycleChecklistName = checkpoint.ChecklistName;
        cycleResumeKind = checkpoint.ResumeKind;
        state = SchedulerState.Reconciling;
        stateSinceUtc = DateTime.UtcNow;
        status = $"发现未完成周期（{checkpoint.Phase}），正在与实际状态对账";
        log.Warning("Recovered active checkpoint from phase {Phase}, updated {Updated:u}", checkpoint.Phase, checkpoint.UpdatedUtc);
    }

    private void PersistCheckpoint()
    {
        var checkpoint = configuration.Checkpoint;
        if (!checkpoint.Active)
            return;

        checkpoint.Phase = state.ToString();
        checkpoint.UpdatedUtc = DateTime.UtcNow;
        checkpoint.WindowStartUtc = trackedWindowStartUtc;
        checkpoint.AutoDutyStartUtc = autoDutyStartUtc;
        checkpoint.FisherGearsetId = fisherGearsetId;
        checkpoint.ManagedFisherPause = managedFisherPause;
        checkpoint.AutoDutyOwned = autoDutyOwned;
        checkpoint.DutyWasObservedRunning = dutyWasObservedRunning;
        checkpoint.InventoryFullFallbackCycle = inventoryFullFallbackCycle;
        checkpoint.ExpectedDutyTerritoryId = selectedDuty?.TerritoryId;
        checkpoint.ChecklistId = cycleChecklistId;
        checkpoint.ChecklistName = cycleChecklistName;
        checkpoint.ResumeKind = cycleResumeKind;
        SaveConfiguration();
    }

    private bool IsSafeForGearsetChange() =>
        playerState.IsLoaded
        && !condition[ConditionFlag.InCombat]
        && !condition[ConditionFlag.Casting]
        && !condition[ConditionFlag.BetweenAreas]
        && !condition[ConditionFlag.BetweenAreas51]
        && !condition[ConditionFlag.OccupiedInCutSceneEvent]
        && !condition[ConditionFlag.OccupiedInQuestEvent]
        && !condition[ConditionFlag.WatchingCutscene]
        && !condition[ConditionFlag.WatchingCutscene78]
        && !condition[ConditionFlag.WaitingForDuty]
        && !condition[ConditionFlag.WaitingForDutyFinder]
        && !IsBoundByDuty();

    private bool IsBoundByDuty() =>
        condition[ConditionFlag.BoundByDuty]
        || condition[ConditionFlag.BoundByDuty56]
        || condition[ConditionFlag.BoundByDuty95];

    private TimeSpan Elapsed(DateTime now) => now - stateSinceUtc;

    private bool TryResolveWindowRemaining(
        DateTime now,
        MissFisherSnapshot fisher,
        out double remainingSeconds,
        out string source)
    {
        if (targetReader.TryGetCurrentTargetWindowStart(out var windowStart))
        {
            remainingSeconds = Math.Max(0d, (windowStart.UtcDateTime - now).TotalSeconds);
            source = "当前目标窗口";
            return true;
        }

        if (Math.Abs(fisher.SecondsUntilNextWindow - MissFisherFallbackSeconds) < 0.5d)
        {
            remainingSeconds = double.NaN;
            source = "IPC 兜底值，已禁止进本";
            return false;
        }

        remainingSeconds = fisher.SecondsUntilNextWindow;
        source = "MissFisher IPC";
        return double.IsFinite(remainingSeconds) && remainingSeconds >= 0d;
    }

    private double GetTrackedRemainingSeconds(DateTime now) =>
        trackedWindowStartUtc is { } deadline
            ? Math.Max(0d, (deadline - now).TotalSeconds)
            : double.NaN;

    private double GetRequiredGapSeconds() => Math.Max(
        configuration.WaitThresholdMinutes,
        configuration.EstimatedDutyMinutes
            + configuration.RecoveryReserveMinutes
            + configuration.SafetyMarginMinutes) * 60d;

    private static DateTime AddSecondsClamped(DateTime start, double seconds)
    {
        var maximumSeconds = (DateTime.MaxValue - start).TotalSeconds;
        return start.AddSeconds(Math.Min(seconds, maximumSeconds));
    }

    private string DescribeIdle(
        MissFisherSnapshot fisher,
        AutoDutySnapshot autoDuty,
        bool validRemaining,
        double remainingSeconds,
        string timeSource)
    {
        if (!fisher.IsRunning) return "MissFisher 未运行";
        if (fisher.IsPaused) return "MissFisher 已暂停";
        if (fisher.IsWindowActive || fisher.IsInWindow) return "MissFisher 正处于目标窗口";
        if (fisher.IsAutoPreparing) return "MissFisher 正在自动准备";
        if (!fisher.IsWaiting) return "MissFisher 当前不是等待状态";
        if (!validRemaining) return $"无法取得可靠窗口时间（{lastWindowTimeSource}）";
        if (!autoDuty.IsStopped || autoDuty.IsLooping) return "AutoDuty 已在运行";
        if (!playerState.IsLoaded || playerState.ClassJob.RowId != FisherJobId) return "等待切回捕鱼职业";
        if (!IsSafeForGearsetChange()) return "角色当前无法安全切换套装";
        return $"当前目标还有 {FormatDuration(remainingSeconds)}（{timeSource}），不进入副本";
    }

    private static string FormatDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            return "未知";
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}小时{span.Minutes}分"
            : $"{span.Minutes}分{span.Seconds}秒";
    }

    private void OnCommand(string command, string arguments)
    {
        switch (arguments.Trim().ToLowerInvariant())
        {
            case "enable":
                configuration.Enabled = true;
                ResetFault();
                SaveConfiguration();
                status = configuration.DryRun ? "调度已启用（只观察）" : "调度已启用";
                break;
            case "disable":
                configuration.Enabled = false;
                stopRequested = true;
                SaveConfiguration();
                status = state == SchedulerState.Idle ? "调度已关闭" : "将在当前安全点恢复捕鱼并停止";
                break;
            case "abort":
                configuration.Enabled = false;
                stopRequested = true;
                SaveConfiguration();
                if (autoDutyOwned)
                {
                    ipc.TryStopAutoDuty();
                    status = "已停止本调度器启动的 AutoDuty；离本后将恢复捕鱼";
                }
                else
                {
                    status = "AutoDuty 不属于本调度周期，未发送停止命令";
                }
                break;
            case "reset":
                ResetFault();
                break;
            case "status":
                log.Information("State={State}, Status={Status}, Error={Error}", state, status, lastError);
                configWindowOpen = true;
                break;
            case "test-restore":
                StartRecoveryTest();
                configWindowOpen = true;
                break;
            default:
                configWindowOpen = true;
                break;
        }
    }

    private void StartRecoveryTest()
    {
        if (configuration.Enabled)
        {
            status = "快速恢复测试未开始：请先关闭正常调度";
            return;
        }

        if (state is not SchedulerState.Idle and not SchedulerState.Faulted)
        {
            status = $"快速恢复测试未开始：调度器当前处于 {state}";
            return;
        }

        if (configuration.Checkpoint.Active)
        {
            status = "快速恢复测试未开始：存在未完成周期，请先重置并完成恢复";
            return;
        }

        if (!ipc.TryGetAutoDuty(out var autoDuty) || !autoDuty.IsStopped || autoDuty.IsLooping || IsBoundByDuty())
        {
            status = "快速恢复测试未开始：请先停止 AutoDuty 并离开副本";
            return;
        }

        if (!ipc.TryGetMissFisher(out var fisher) || !fisher.IsRunning || fisher.IsPaused)
        {
            status = "快速恢复测试未开始：请先让 MissFisher 正常运行所选恢复目标";
            return;
        }

        if (!playerState.IsLoaded || playerState.ClassJob.RowId != FisherJobId || !IsSafeForGearsetChange())
        {
            status = "快速恢复测试未开始：请保持捕鱼职业并等待角色可安全切换套装";
            return;
        }

        var currentGearsetId = jobSelector.CurrentGearsetId;
        if (currentGearsetId is null)
        {
            status = "快速恢复测试未开始：无法读取当前捕鱼套装";
            return;
        }

        ResetFault();
        fisherGearsetId = currentGearsetId;
        selectedJob = null;
        managedFisherPause = true;
        dutyWasObservedRunning = false;
        autoDutyOwned = false;
        autoDutyStartUtc = null;
        cycleChecklistId = configuration.MissFisherChecklistId;
        cycleChecklistName = configuration.MissFisherChecklistName.Trim();
        cycleResumeKind = configuration.MissFisherResumeKind;
        resumeCommandSent = false;
        stopRequested = false;
        recoveryTestMode = true;
        configuration.Checkpoint.Active = true;
        PersistCheckpoint();
        commandManager.ProcessCommand("/mf pause");
        Transition(SchedulerState.PausingFisher, "快速恢复测试：正在暂停 MissFisher");
    }

    private void OpenConfig() => configWindowOpen = true;

    private void DrawConfig()
    {
        if (!configWindowOpen)
            return;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, 540), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Fisher Duty Scheduler", ref configWindowOpen))
        {
            ImGui.End();
            return;
        }

        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("启用调度", ref enabled))
        {
            configuration.Enabled = enabled;
            if (enabled) ResetFault(); else stopRequested = true;
            SaveConfiguration();
        }

        var dryRun = configuration.DryRun;
        if (ImGui.Checkbox("只观察（不暂停、不切职业、不进本）", ref dryRun))
        {
            configuration.DryRun = dryRun;
            SaveConfiguration();
        }

        var threshold = configuration.WaitThresholdMinutes;
        if (ImGui.InputInt("等待阈值（分钟）", ref threshold))
        {
            configuration.WaitThresholdMinutes = Math.Clamp(threshold, 20, 240);
            SaveConfiguration();
        }

        var repeat = configuration.RepeatWhileWindowIsFar;
        if (ImGui.Checkbox("出本后仍超过阈值则再运行一轮", ref repeat))
        {
            configuration.RepeatWhileWindowIsFar = repeat;
            SaveConfiguration();
        }

        var timeout = configuration.DutyTimeoutMinutes;
        if (ImGui.InputInt("单次副本超时（分钟）", ref timeout))
        {
            configuration.DutyTimeoutMinutes = Math.Clamp(timeout, 30, 180);
            SaveConfiguration();
        }

        var estimatedDuty = configuration.EstimatedDutyMinutes;
        if (ImGui.InputInt("预计单次副本耗时（分钟）", ref estimatedDuty))
        {
            configuration.EstimatedDutyMinutes = Math.Clamp(estimatedDuty, 10, 120);
            SaveConfiguration();
        }

        var recoveryReserve = configuration.RecoveryReserveMinutes;
        if (ImGui.InputInt("排队与恢复预留（分钟）", ref recoveryReserve))
        {
            configuration.RecoveryReserveMinutes = Math.Clamp(recoveryReserve, 1, 30);
            SaveConfiguration();
        }

        var safetyMargin = configuration.SafetyMarginMinutes;
        if (ImGui.InputInt("窗口安全余量（分钟）", ref safetyMargin))
        {
            configuration.SafetyMarginMinutes = Math.Clamp(safetyMargin, 0, 30);
            SaveConfiguration();
        }

        var repairFisherGear = configuration.RepairFisherGear;
        if (ImGui.Checkbox("自动维护捕鱼装备", ref repairFisherGear))
        {
            configuration.RepairFisherGear = repairFisherGear;
            SaveConfiguration();
        }

        if (configuration.RepairFisherGear)
        {
            var repairThreshold = configuration.FisherRepairThresholdPercent;
            if (ImGui.SliderInt("捕鱼装备修理阈值", ref repairThreshold, 1, 99, "%d%%"))
            {
                configuration.FisherRepairThresholdPercent = repairThreshold;
                SaveConfiguration();
            }
        }

        var continueWhenInventoryFull = configuration.ContinueDutiesWhenInventoryFull;
        if (ImGui.Checkbox("背包满时继续 AutoDuty 调度", ref continueWhenInventoryFull))
        {
            configuration.ContinueDutiesWhenInventoryFull = continueWhenInventoryFull;
            if (!continueWhenInventoryFull)
                configuration.InventoryFullFallbackActive = false;
            SaveConfiguration();
        }

        var excludedSummary = configuration.ExcludedDutyTerritories.Count == 0
            ? "未排除副本"
            : $"已排除 {configuration.ExcludedDutyTerritories.Count} 个副本";
        if (ImGui.BeginCombo("排除副本（可多选）", excludedSummary))
        {
            foreach (var duty in DutyCatalog.SupportLevelingDuties.OrderByDescending(duty => duty.MinimumLevel))
            {
                var excluded = configuration.ExcludedDutyTerritories.Contains(duty.TerritoryId);
                if (!ImGui.Checkbox($"Lv{duty.MinimumLevel} {duty.Name} ({duty.TerritoryId})", ref excluded))
                    continue;

                if (excluded)
                {
                    if (!configuration.ExcludedDutyTerritories.Contains(duty.TerritoryId))
                        configuration.ExcludedDutyTerritories.Add(duty.TerritoryId);
                }
                else
                {
                    configuration.ExcludedDutyTerritories.RemoveAll(id => id == duty.TerritoryId);
                }
                SaveConfiguration();
            }
            ImGui.EndCombo();
        }

        var excludedJobsSummary = configuration.ExcludedJobIds.Count == 0
            ? "不排除职业"
            : $"已排除 {configuration.ExcludedJobIds.Count} 个职业";
        if (ImGui.BeginCombo("排除职业（可多选）", excludedJobsSummary))
        {
            foreach (var jobOption in jobSelector.GetSupportedCombatJobs())
            {
                var excluded = configuration.ExcludedJobIds.Contains(jobOption.JobId);
                if (!ImGui.Checkbox($"{jobOption.Name}##job-{jobOption.JobId}", ref excluded))
                    continue;

                if (excluded)
                {
                    if (!configuration.ExcludedJobIds.Contains(jobOption.JobId))
                        configuration.ExcludedJobIds.Add(jobOption.JobId);
                }
                else
                {
                    configuration.ExcludedJobIds.RemoveAll(id => id == jobOption.JobId);
                }
                SaveConfiguration();
            }
            ImGui.EndCombo();
        }

        var resumePreview = string.IsNullOrWhiteSpace(configuration.MissFisherChecklistName)
            ? "请选择"
            : configuration.MissFisherChecklistName;
        if (ImGui.BeginCombo("恢复目标", resumePreview))
        {
            if (targetReader.TryGetResumeOptions(out var resumeOptions))
            {
                for (var index = 0; index < resumeOptions.Count; index++)
                {
                    var option = resumeOptions[index];
                    var selected = option.Kind == configuration.MissFisherResumeKind
                        && option.Key == configuration.MissFisherChecklistId;
                    if (!ImGui.Selectable($"{option.Label}##resume-{index}", selected))
                        continue;

                    configuration.MissFisherResumeKind = option.Kind;
                    configuration.MissFisherChecklistId = option.Key;
                    configuration.MissFisherChecklistName = option.Name;
                    SaveConfiguration();
                }
            }
            else
            {
                ImGui.TextDisabled("暂时无法读取 MissFisher 目标列表");
            }
            ImGui.EndCombo();
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"状态：{state}");
        ImGui.TextWrapped(status);
        ImGui.TextWrapped($"MissFisher 目标适配：{targetReader.Status}");
        ImGui.TextUnformatted($"最低进本空档：{FormatDuration(GetRequiredGapSeconds())}");
        if (configuration.Checkpoint.Active)
            ImGui.TextUnformatted($"恢复检查点：{configuration.Checkpoint.Phase} / {configuration.Checkpoint.UpdatedUtc.ToLocalTime():HH:mm:ss}");
        if (lastMissFisher is { } fisher)
            ImGui.TextUnformatted($"MissFisher IPC：{FormatDuration(fisher.SecondsUntilNextWindow)}");
        if (lastResolvedRemainingSeconds is { } resolved)
            ImGui.TextUnformatted($"当前目标剩余：{FormatDuration(resolved)}（{lastWindowTimeSource}）");
        else
            ImGui.TextUnformatted($"当前目标剩余：未知（{lastWindowTimeSource}）");
        if (trackedWindowStartUtc is not null)
            ImGui.TextUnformatted($"调度估算剩余：{FormatDuration(GetTrackedRemainingSeconds(DateTime.UtcNow))}");
        if (lowestFisherGearPercent is { } durability)
            ImGui.TextUnformatted($"捕鱼装备最低耐久：{durability:F0}%");
        if (selectedJob is { } job)
            ImGui.TextUnformatted($"战斗套装：{job.GearsetName} / {job.Level}级 / i{job.ItemLevel}");
        if (selectedDuty is { } activeDuty)
            ImGui.TextWrapped($"本轮副本：{activeDuty.Name}（{activeDuty.TerritoryId}）");
        if (!string.IsNullOrEmpty(lastError))
            ImGui.TextWrapped($"错误：{lastError}");

        ImGui.Separator();
        if (ImGui.Button("重置状态"))
            ResetFault();
        ImGui.SameLine();
        if (ImGui.Button("紧急停止 AutoDuty"))
            OnCommand(CommandName, "abort");

        if (ImGui.Button("快速测试 MissFisher 恢复（不进副本）"))
            StartRecoveryTest();

        ImGui.TextWrapped("职业规则：从未排除的 15-99 级正式战斗职业中选择等级最低者；同等级按套装顺序。青魔法师固定排除。副本从未排除且存在 AutoDuty 路径的候选中按等级由高到低尝试。返回捕鱼职业后，如果原会话已丢失，调度器会重新启动所选的 MissFisher 图鉴、分组或合集。MissFisher 因背包满停止时，可继续运行副本；清出背包空间后自动恢复钓鱼。");
        ImGui.End();
    }

    private void SaveConfiguration() => pluginInterface.SavePluginConfig(configuration);
}
