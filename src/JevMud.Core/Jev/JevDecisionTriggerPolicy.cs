using System.Collections.ObjectModel;
using JevMud.Contracts.Events;
using JevMud.Contracts.Jev;

namespace JevMud.Core.Jev;

/// <summary>
/// Maps semantic state changes to Jev domains which may need reconsideration.
/// Raw text never triggers Jev directly. Prompt telemetry drives Recovery only; Navigation is
/// resumed by the autonomy supervisor after recovery or by a completed room observation.
/// </summary>
public static class JevDecisionTriggerPolicy
{
    public static IReadOnlyList<JevDomain> GetTriggeredDomains(IMudEvent mudEvent)
    {
        ArgumentNullException.ThrowIfNull(mudEvent);

        JevDomain[] domains = mudEvent switch
        {
            CombatTargetConditionObserved => [JevDomain.Combat],
            CombatStateChanged => [JevDomain.Combat, JevDomain.Recovery, JevDomain.Navigation],
            EnemyKilled => [JevDomain.Combat, JevDomain.Recovery, JevDomain.Navigation, JevDomain.Loot],
            CharacterConditionChanged => [JevDomain.Combat, JevDomain.Recovery],
            CharacterPositionObserved => [JevDomain.Recovery],
            CharacterPromptObserved => [JevDomain.Recovery],
            RoomObservationObserved => [JevDomain.Combat, JevDomain.Recovery, JevDomain.Navigation],
            RoomOccupantDeparted => [JevDomain.Navigation],
            NavigationFailed => [JevDomain.Recovery, JevDomain.Navigation],
            RoomExitStateChanged => [JevDomain.Navigation],
            EquipmentChanged => [JevDomain.Inventory, JevDomain.Combat],
            SkillsSnapshotObserved => [JevDomain.Training],
            SpellsSnapshotObserved => [JevDomain.Training],
            SkillImproved => [JevDomain.Training],
            SkillPracticeSucceeded => [JevDomain.Training],
            _ => []
        };

        return new ReadOnlyCollection<JevDomain>(domains);
    }
}
