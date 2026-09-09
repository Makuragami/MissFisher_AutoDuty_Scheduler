namespace FisherDutyScheduler;

internal enum SchedulerState
{
    Idle,
    Reconciling,
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
