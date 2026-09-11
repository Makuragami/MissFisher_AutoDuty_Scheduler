namespace FisherDutyScheduler;

internal enum SchedulerState
{
    Idle,
    Reconciling,
    PausingFisherForRepair,
    RepairingFisherGear,
    PausingFisher,
    SelectingCombatJob,
    EquippingCombatJob,
    StartingDuty,
    WaitingForDutyStart,
    RunningDuty,
    RestoringFisher,
    ResumingFisher,
    RestartingFisher,
    Faulted,
}
