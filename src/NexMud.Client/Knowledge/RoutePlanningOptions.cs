using NexMud.Client.Settings;

namespace NexMud.Client.Knowledge;

public sealed record RoutePlanningOptions(
    int MaximumDepth,
    bool AvoidBlockedExits,
    bool AllowUnknownTraversability,
    bool AvoidClosedDoors,
    bool PreferKnownTraversableExits,
    IReadOnlySet<string> AvoidAreas,
    IReadOnlySet<string> AvoidTerrains,
    IReadOnlySet<string> AvoidMobNames,
    bool CanOpenClosedDoors = false)
{
    public static RoutePlanningOptions From(MapperPreferences preferences) => new(
        Math.Clamp(preferences.MaximumRouteDepth, 1, 5000),
        preferences.AvoidBlockedExits,
        preferences.AllowUnknownTraversability,
        preferences.AvoidClosedDoors,
        preferences.PreferKnownTraversableExits,
        preferences.GetAvoidAreaSet(),
        preferences.GetAvoidTerrainSet(),
        preferences.GetAvoidMobSet(),
        preferences.AutoOpenDoors);
}
