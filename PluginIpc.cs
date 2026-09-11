using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace FisherDutyScheduler;

internal sealed class PluginIpc
{
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool> missFisherIsRunning;
    private readonly ICallGateSubscriber<bool> missFisherIsWindowActive;
    private readonly ICallGateSubscriber<bool> missFisherIsPaused;
    private readonly ICallGateSubscriber<bool> missFisherIsWaiting;
    private readonly ICallGateSubscriber<bool> missFisherIsAutoPreparing;
    private readonly ICallGateSubscriber<bool> missFisherIsInWindow;
    private readonly ICallGateSubscriber<double> missFisherSecondsUntilNextWindow;
    private readonly ICallGateSubscriber<bool> autoDutyIsStopped;
    private readonly ICallGateSubscriber<bool> autoDutyIsLooping;
    private readonly ICallGateSubscriber<bool> autoDutyIsNavigating;
    private readonly ICallGateSubscriber<uint, bool> autoDutyContentHasPath;
    private readonly ICallGateSubscriber<object> autoDutyStop;
    private readonly ICallGateSubscriber<string, string> autoDutyGetConfig;
    private DateTime nextMissFisherWarningUtc;
    private DateTime nextAutoDutyWarningUtc;

    public PluginIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        missFisherIsRunning = pluginInterface.GetIpcSubscriber<bool>("MissFisher.Manager.IsRunning");
        missFisherIsWindowActive = pluginInterface.GetIpcSubscriber<bool>("MissFisher.Manager.IsWindowActive");
        missFisherIsPaused = pluginInterface.GetIpcSubscriber<bool>("MissFisher.Manager.AngleWindowState.IsPaused");
        missFisherIsWaiting = pluginInterface.GetIpcSubscriber<bool>("MissFisher.Manager.AngleWindowState.IsWaiting");
        missFisherIsAutoPreparing = pluginInterface.GetIpcSubscriber<bool>("MissFisher.Manager.AngleWindowState.IsAutoPreparing");
        missFisherIsInWindow = pluginInterface.GetIpcSubscriber<bool>("MissFisher.Manager.AngleWindowState.IsInWindow");
        missFisherSecondsUntilNextWindow = pluginInterface.GetIpcSubscriber<double>("MissFisher.Manager.SecondsUntilNextWindow");
        autoDutyIsStopped = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsStopped");
        autoDutyIsLooping = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsLooping");
        autoDutyIsNavigating = pluginInterface.GetIpcSubscriber<bool>("AutoDuty.IsNavigating");
        autoDutyContentHasPath = pluginInterface.GetIpcSubscriber<uint, bool>("AutoDuty.ContentHasPath");
        autoDutyStop = pluginInterface.GetIpcSubscriber<object>("AutoDuty.Stop");
        autoDutyGetConfig = pluginInterface.GetIpcSubscriber<string, string>("AutoDuty.GetConfig");
    }

    public bool TryGetMissFisher(out MissFisherSnapshot snapshot)
    {
        try
        {
            snapshot = new MissFisherSnapshot(
                missFisherIsRunning.InvokeFunc(),
                missFisherIsWindowActive.InvokeFunc(),
                missFisherIsPaused.InvokeFunc(),
                missFisherIsWaiting.InvokeFunc(),
                missFisherIsAutoPreparing.InvokeFunc(),
                missFisherIsInWindow.InvokeFunc(),
                missFisherSecondsUntilNextWindow.InvokeFunc());
            return true;
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow >= nextMissFisherWarningUtc)
            {
                log.Warning(ex, "MissFisher IPC snapshot failed; further warnings throttled for 30 seconds");
                nextMissFisherWarningUtc = DateTime.UtcNow.AddSeconds(30);
            }
            snapshot = default;
            return false;
        }
    }

    public bool TryGetAutoDuty(out AutoDutySnapshot snapshot)
    {
        try
        {
            snapshot = new AutoDutySnapshot(
                autoDutyIsStopped.InvokeFunc(),
                autoDutyIsLooping.InvokeFunc(),
                autoDutyIsNavigating.InvokeFunc());
            return true;
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow >= nextAutoDutyWarningUtc)
            {
                log.Warning(ex, "AutoDuty IPC snapshot failed; further warnings throttled for 30 seconds");
                nextAutoDutyWarningUtc = DateTime.UtcNow.AddSeconds(30);
            }
            snapshot = default;
            return false;
        }
    }

    public bool TryStopAutoDuty()
    {
        try
        {
            autoDutyStop.InvokeAction();
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "AutoDuty IPC stop failed");
            return false;
        }
    }

    public bool TryContentHasPath(uint territoryId, out bool hasPath)
    {
        try
        {
            hasPath = autoDutyContentHasPath.InvokeFunc(territoryId);
            return true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "AutoDuty ContentHasPath IPC failed for territory {TerritoryId}", territoryId);
            hasPath = false;
            return false;
        }
    }

    public bool TryGetAutoDutyRepairSettings(out bool selfRepair, out int thresholdPercent)
    {
        try
        {
            selfRepair = bool.TryParse(autoDutyGetConfig.InvokeFunc("AutoRepairSelf"), out var self) && self;
            thresholdPercent = int.TryParse(autoDutyGetConfig.InvokeFunc("AutoRepairPct"), out var threshold)
                ? Math.Clamp(threshold, 0, 99)
                : 99;
            return true;
        }
        catch
        {
            selfRepair = false;
            thresholdPercent = 99;
            return false;
        }
    }
}

internal readonly record struct MissFisherSnapshot(
    bool IsRunning,
    bool IsWindowActive,
    bool IsPaused,
    bool IsWaiting,
    bool IsAutoPreparing,
    bool IsInWindow,
    double SecondsUntilNextWindow);

internal readonly record struct AutoDutySnapshot(bool IsStopped, bool IsLooping, bool IsNavigating);
