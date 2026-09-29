using System.Security.Cryptography;
using System.Text;
using NexMud.Client.Knowledge;
using NexMud.Client.Settings;
using NexMud.Core.State;
using NexMud.Scripting.Host;

namespace NexMud.Client.Navigation;

/// <summary>
/// Read-only Mapper domain boundary used by orchestration. Graph/pathfinding remain C# concerns;
/// scripts receive immutable route DTOs only.
/// </summary>
public sealed class MapperQueryService
{
    private readonly StateReducer _state;
    private readonly MapperReadRepository _routes;
    private readonly Func<MapperPreferences> _settings;

    public MapperQueryService(StateReducer state, WorldKnowledgeStore knowledge, Func<MapperPreferences> settings)
    {
        _state = state;
        _routes = new MapperReadRepository(knowledge.DatabasePath);
        _settings = settings;
    }

    public Task<ScriptRoomSnapshot?> CurrentRoomAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? id = _state.Current.Room.Id;
        return Task.FromResult(string.IsNullOrWhiteSpace(id)
            ? null
            : new ScriptRoomSnapshot(id!, _state.Current.Room.Name));
    }

    public async Task<ScriptRoutePlan?> FindPathAsync(
        string destinationRoomId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoomId);
        string? currentRoomId = _state.Current.Room.Id;
        if (string.IsNullOrWhiteSpace(currentRoomId)) return null;

        MapperPreferences settings = _settings();
        KnowledgeRoute? route = await _routes.FindRouteAsync(
            currentRoomId,
            destinationRoomId.Trim(),
            RoutePlanningOptions.From(settings),
            cancellationToken).ConfigureAwait(false);
        if (route is null) return null;

        ScriptRouteStep[] steps = route.Steps.Select((step, index) => new ScriptRouteStep(
            index + 1,
            step.FromRoomId,
            step.Direction,
            step.ToRoomId,
            step.DoorState.ToString(),
            step.Traversability.ToString(),
            RecoveryCommand(step, settings))).ToArray();

        return new ScriptRoutePlan(
            BuildRouteId(route),
            route.FromRoomId,
            route.ToRoomId,
            GraphVersion: 0,
            steps);
    }

    private static string? RecoveryCommand(KnowledgeRouteStep step, MapperPreferences settings)
    {
        if (step.DoorState != NexMud.Contracts.State.ExitDoorState.Closed || !settings.AutoOpenDoors) return null;
        string template = string.IsNullOrWhiteSpace(settings.DoorOpenCommandTemplate)
            ? "open {direction}"
            : settings.DoorOpenCommandTemplate;
        string command = template.Replace("{direction}", step.Direction, StringComparison.OrdinalIgnoreCase).Trim();
        return command.Length == 0 ? null : command;
    }

    private static string BuildRouteId(KnowledgeRoute route)
    {
        StringBuilder canonical = new();
        canonical.Append(route.FromRoomId).Append('\n').Append(route.ToRoomId).Append('\n');
        foreach (KnowledgeRouteStep step in route.Steps)
            canonical.Append(step.FromRoomId).Append('|').Append(step.Direction).Append('|').Append(step.ToRoomId).Append('\n');
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
        return $"route-{hash[..16]}";
    }
}
