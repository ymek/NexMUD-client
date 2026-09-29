using NexMud.Contracts.Events;

namespace NexMud.Gui;

internal static class AutoMoveDisplay
{
    public static string Format(AutoMoveStateChanged state) => state.Status switch
    {
        AutoMoveStatus.Moving => state.TotalSteps > 0
            ? $"AUTO {state.CompletedSteps:N0}/{state.TotalSteps:N0}" +
              (string.IsNullOrWhiteSpace(state.CurrentDirection) ? string.Empty : $" · {state.CurrentDirection}")
            : "AUTO MOVING",
        AutoMoveStatus.Paused => "AUTO PAUSED",
        AutoMoveStatus.Planning => "PLANNING",
        AutoMoveStatus.Recovering => "RECOVERING",
        AutoMoveStatus.Replanning => "REPLANNING",
        AutoMoveStatus.Completed => "ARRIVED",
        AutoMoveStatus.Failed => "AUTO FAILED",
        AutoMoveStatus.Aborted => "AUTO ABORTED",
        AutoMoveStatus.Stopped => "AUTO STOPPED",
        _ => "AUTO READY"
    };
}
