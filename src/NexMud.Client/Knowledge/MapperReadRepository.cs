using NexMud.Adapters.Avendar;
using NexMud.Contracts.State;
using Microsoft.Data.Sqlite;

namespace NexMud.Client.Knowledge;

/// <summary>
/// Read-only mapper query path. It deliberately does not share the live knowledge writer's
/// SQLite connection/cache and never performs schema work. Mapper browsing must not contend
/// with event ingestion or make the UI dependent on the writer connection lifecycle.
/// </summary>
public sealed class MapperReadRepository
{
    private readonly string _connectionString;

    public MapperReadRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString();
    }

    public async Task<MapperGraphSnapshot?> LoadNeighborhoodAsync(
        string originRoomId,
        int maximumDepth,
        int maximumRooms,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(originRoomId)) return null;
        maximumDepth = Math.Clamp(maximumDepth, 1, 50);
        maximumRooms = Math.Clamp(maximumRooms, 10, 250);

        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> included = new(StringComparer.Ordinal) { originRoomId };
        List<string> frontier = [originRoomId];
        Dictionary<(string From, string Direction, string To), MapperGraphEdge> edges = new();
        int maximumEdges = Math.Clamp(maximumRooms * 10, 100, 2500);

        for (int depth = 0;
             depth < maximumDepth && frontier.Count > 0 && included.Count < maximumRooms && edges.Count < maximumEdges;
             depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<string> next = [];
            foreach (string[] chunk in frontier.Chunk(100))
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandTimeout = 2;
                List<string> parameters = [];
                for (int index = 0; index < chunk.Length; index++)
                {
                    string parameter = "$from" + index;
                    parameters.Add(parameter);
                    command.Parameters.AddWithValue(parameter, chunk[index]);
                }
                command.Parameters.AddWithValue("$limit", maximumEdges - edges.Count);
                string roomSet = string.Join(",", parameters);
                command.CommandText = $"""
                    SELECT from_room_id, direction, to_room_id, traversability, door_state, block_reason
                    FROM room_exits
                    WHERE to_room_id IS NOT NULL
                      AND (from_room_id IN ({roomSet}) OR to_room_id IN ({roomSet}))
                    ORDER BY from_room_id, direction COLLATE NOCASE
                    LIMIT $limit;
                    """;
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (edges.Count < maximumEdges && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    string from = reader.GetString(0);
                    string direction = reader.GetString(1);
                    string to = reader.GetString(2);
                    string? traversability = reader.IsDBNull(3) ? null : reader.GetString(3);
                    string? doorState = reader.IsDBNull(4) ? null : reader.GetString(4);
                    string? blockReason = reader.IsDBNull(5) ? null : reader.GetString(5);
                    _ = Enum.TryParse(doorState, true, out ExitDoorState parsedDoorState);
                    edges[(from, direction, to)] = new MapperGraphEdge(
                        from, direction, to, traversability, parsedDoorState, blockReason);

                    if (included.Count < maximumRooms && included.Add(from)) next.Add(from);
                    if (included.Count < maximumRooms && included.Add(to)) next.Add(to);
                }
            }
            frontier = next;
        }

        List<MapperGraphRoom> rooms = [];
        foreach (string[] chunk in included.Chunk(100))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandTimeout = 2;
            List<string> parameters = [];
            for (int index = 0; index < chunk.Length; index++)
            {
                string parameter = "$room" + index;
                parameters.Add(parameter);
                command.Parameters.AddWithValue(parameter, chunk[index]);
            }
            command.CommandText = $"""
                SELECT r.room_id, r.name, m.label, m.area, COALESCE(m.avoid, 0), r.visit_count, r.last_seen_at,
                       (SELECT COUNT(*) FROM room_exits u WHERE u.from_room_id = r.room_id AND u.to_room_id IS NULL) AS unexplored_exits,
                       (SELECT group_concat(u.direction, '|') FROM room_exits u WHERE u.from_room_id = r.room_id AND u.to_room_id IS NULL) AS unexplored_directions
                FROM rooms r
                LEFT JOIN room_metadata m ON m.room_id = r.room_id
                WHERE r.room_id IN ({string.Join(",", parameters)});
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rooms.Add(new MapperGraphRoom(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    !reader.IsDBNull(4) && reader.GetInt64(4) != 0,
                    ToInt32(reader.GetInt64(5)),
                    ParseDate(reader.IsDBNull(6) ? null : reader.GetString(6)),
                    reader.IsDBNull(7) ? 0 : ToInt32(reader.GetInt64(7)),
                    reader.IsDBNull(8)
                        ? Array.Empty<string>()
                        : reader.GetString(8).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }
        }

        List<MapperGraphEdge> graphEdges = edges.Values
            .Where(edge => included.Contains(edge.FromRoomId) && included.Contains(edge.ToRoomId))
            .ToList();
        return new MapperGraphSnapshot(originRoomId, rooms, graphEdges);
    }

    public async Task<IReadOnlyList<MapperDestinationSearchResult>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        string normalized = query.Trim();
        if (normalized.Length == 0) return [];
        limit = Math.Clamp(limit, 1, 100);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        List<MapperDestinationSearchResult> results = [];

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandTimeout = 2;
            command.CommandText = """
                SELECT r.room_id, r.name, m.label, m.area, COALESCE(m.avoid, 0), r.visit_count, r.last_seen_at
                FROM rooms r
                LEFT JOIN room_metadata m ON m.room_id = r.room_id
                WHERE r.name LIKE $like COLLATE NOCASE
                   OR m.label LIKE $like COLLATE NOCASE
                   OR m.area LIKE $like COLLATE NOCASE
                ORDER BY CASE WHEN r.name = $exact COLLATE NOCASE THEN 0 ELSE 1 END,
                         r.visit_count DESC, r.last_seen_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$exact", normalized);
            command.Parameters.AddWithValue("$like", "%" + normalized + "%");
            command.Parameters.AddWithValue("$limit", limit);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new MapperDestinationSearchResult(
                    MapperDestinationKind.Room,
                    reader.IsDBNull(2) ? reader.IsDBNull(1) ? "Known room" : reader.GetString(1) : reader.GetString(2),
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    ToInt32(reader.GetInt64(5)),
                    ParseDate(reader.IsDBNull(6) ? null : reader.GetString(6)),
                    !reader.IsDBNull(4) && reader.GetInt64(4) != 0));
            }
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandTimeout = 2;
            command.CommandText = """
                SELECT e.room_id, r.name, m.area,
                       COALESCE(e.canonical_name, e.description) AS display_name,
                       COUNT(*) AS observation_count,
                       MAX(e.last_seen_at) AS last_seen_at,
                       COALESCE(m.avoid, 0),
                       e.kind
                FROM room_entities e
                LEFT JOIN rooms r ON r.room_id = e.room_id
                LEFT JOIN room_metadata m ON m.room_id = e.room_id
                WHERE e.kind IN ($occupantKind, $objectKind, $fixtureKind)
                  AND (e.canonical_name LIKE $like COLLATE NOCASE OR e.description LIKE $like COLLATE NOCASE)
                GROUP BY e.room_id, r.name, m.area, display_name, m.avoid, e.kind
                ORDER BY CASE WHEN display_name = $exact COLLATE NOCASE THEN 0 ELSE 1 END,
                         observation_count DESC, last_seen_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$occupantKind", RoomEntityKind.Occupant.ToString());
            command.Parameters.AddWithValue("$objectKind", RoomEntityKind.Object.ToString());
            command.Parameters.AddWithValue("$fixtureKind", RoomEntityKind.Fixture.ToString());
            command.Parameters.AddWithValue("$exact", normalized);
            command.Parameters.AddWithValue("$like", "%" + normalized + "%");
            command.Parameters.AddWithValue("$limit", limit);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                MapperDestinationKind destinationKind = reader.GetString(7) switch
                {
                    nameof(RoomEntityKind.Object) => MapperDestinationKind.Object,
                    nameof(RoomEntityKind.Fixture) => MapperDestinationKind.Fixture,
                    _ => MapperDestinationKind.Entity
                };
                results.Add(new MapperDestinationSearchResult(
                    destinationKind,
                    reader.GetString(3),
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    ToInt32(reader.GetInt64(4)),
                    ParseDate(reader.IsDBNull(5) ? null : reader.GetString(5)),
                    !reader.IsDBNull(6) && reader.GetInt64(6) != 0));
            }
        }

        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = """
                SELECT s.room_id, r.name, m.area, s.item_name, s.mob_name,
                       SUM(s.observation_count) AS observation_count,
                       MAX(s.last_seen_at) AS last_seen_at,
                       COALESCE(m.avoid, 0)
                FROM mob_item_sources s
                LEFT JOIN rooms r ON r.room_id = s.room_id
                LEFT JOIN room_metadata m ON m.room_id = s.room_id
                WHERE s.item_name LIKE $like COLLATE NOCASE
                GROUP BY s.room_id, r.name, m.area, s.item_name, s.mob_name, m.avoid
                ORDER BY CASE WHEN s.item_name = $exact COLLATE NOCASE THEN 0 ELSE 1 END,
                         observation_count DESC, last_seen_at DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$exact", normalized);
            command.Parameters.AddWithValue("$like", "%" + normalized + "%");
            command.Parameters.AddWithValue("$limit", limit);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string item = reader.GetString(3);
                string mob = reader.GetString(4);
                results.Add(new MapperDestinationSearchResult(
                    MapperDestinationKind.ItemSource,
                    $"{item} · from {mob}",
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    ToInt32(reader.GetInt64(5)),
                    ParseDate(reader.IsDBNull(6) ? null : reader.GetString(6)),
                    !reader.IsDBNull(7) && reader.GetInt64(7) != 0));
            }
        }
        return results
            .GroupBy(
                result => (result.Kind, result.RoomId, result.DisplayName),
                EqualityComparer<(MapperDestinationKind Kind, string RoomId, string DisplayName)>.Default)
            .Select(group => group.First())
            .OrderBy(result => SearchMatchRank(result.DisplayName, normalized))
            .ThenBy(result => result.Kind)
            .ThenByDescending(result => result.ObservationCount)
            .ThenByDescending(result => result.LastSeenAt)
            .Take(limit)
            .ToArray();
    }

    public Task<KnowledgeRoute?> FindRouteAsync(
        string fromRoomId,
        string toRoomId,
        int maximumDepth,
        bool avoidBlockedExits,
        bool allowUnknownTraversability,
        CancellationToken cancellationToken = default) =>
        FindRouteAsync(
            fromRoomId,
            toRoomId,
            new RoutePlanningOptions(
                maximumDepth,
                avoidBlockedExits,
                allowUnknownTraversability,
                AvoidClosedDoors: false,
                PreferKnownTraversableExits: true,
                AvoidAreas: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                AvoidTerrains: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                AvoidMobNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            cancellationToken);

    public async Task<KnowledgeRoute?> FindRouteAsync(
        string fromRoomId,
        string toRoomId,
        RoutePlanningOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fromRoomId) || string.IsNullOrWhiteSpace(toRoomId)) return null;
        if (fromRoomId.Equals(toRoomId, StringComparison.Ordinal)) return new KnowledgeRoute(fromRoomId, toRoomId, []);
        int maximumDepth = Math.Clamp(options.MaximumDepth, 1, 5000);
        await using SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        HashSet<string> dangerousRooms = await LoadAvoidMobRoomsAsync(connection, options.AvoidMobNames, cancellationToken).ConfigureAwait(false);

        Dictionary<string, KnowledgeRouteStep?> previous = new(StringComparer.Ordinal) { [fromRoomId] = null };
        List<string> frontier = [fromRoomId];
        for (int depth = 0; depth < maximumDepth && frontier.Count > 0; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<string> next = [];
            foreach (string[] chunk in frontier.Chunk(100))
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandTimeout = 2;
                List<string> parameters = [];
                for (int index = 0; index < chunk.Length; index++)
                {
                    string parameter = "$from" + index;
                    parameters.Add(parameter);
                    command.Parameters.AddWithValue(parameter, chunk[index]);
                }
                command.CommandText = $"""
                    SELECT e.from_room_id, e.direction, e.to_room_id, r.name, e.traversability,
                           COALESCE(m.avoid, 0), r.terrain, m.area, e.door_state, e.block_reason
                    FROM room_exits e
                    LEFT JOIN rooms r ON r.room_id = e.to_room_id
                    LEFT JOIN room_metadata m ON m.room_id = e.to_room_id
                    WHERE e.to_room_id IS NOT NULL
                      AND e.from_room_id IN ({string.Join(",", parameters)})
                    ORDER BY e.from_room_id,
                             CASE WHEN $preferKnown = 1 AND e.traversability = $traversable THEN 0 ELSE 1 END,
                             e.direction COLLATE NOCASE;
                    """;
                command.Parameters.AddWithValue("$preferKnown", options.PreferKnownTraversableExits ? 1 : 0);
                command.Parameters.AddWithValue("$traversable", ExitTraversability.Traversable.ToString());
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    string from = reader.GetString(0);
                    string direction = reader.GetString(1);
                    string to = reader.GetString(2);
                    string? name = reader.IsDBNull(3) ? null : reader.GetString(3);
                    string? traversability = reader.IsDBNull(4) ? null : reader.GetString(4);
                    bool avoidRoom = !reader.IsDBNull(5) && reader.GetInt64(5) != 0;
                    string? terrain = reader.IsDBNull(6) ? null : reader.GetString(6);
                    string? area = reader.IsDBNull(7) ? null : reader.GetString(7);
                    string? doorState = reader.IsDBNull(8) ? null : reader.GetString(8);
                    string? blockReason = reader.IsDBNull(9) ? null : reader.GetString(9);
                    bool closedDoor = string.Equals(doorState, ExitDoorState.Closed.ToString(), StringComparison.OrdinalIgnoreCase);
                    bool lockedDoor = string.Equals(doorState, ExitDoorState.Locked.ToString(), StringComparison.OrdinalIgnoreCase);
                    bool blocked = string.Equals(traversability, ExitTraversability.Blocked.ToString(), StringComparison.OrdinalIgnoreCase);
                    if (lockedDoor) continue;
                    if (options.AvoidBlockedExits && blocked && !(closedDoor && options.CanOpenClosedDoors)) continue;
                    if (!options.AllowUnknownTraversability && (string.IsNullOrWhiteSpace(traversability) ||
                        string.Equals(traversability, ExitTraversability.Unknown.ToString(), StringComparison.OrdinalIgnoreCase))) continue;
                    if (options.AvoidClosedDoors && closedDoor) continue;
                    if (avoidRoom && !to.Equals(toRoomId, StringComparison.Ordinal)) continue;
                    if (!to.Equals(toRoomId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(area) && options.AvoidAreas.Contains(area)) continue;
                    if (!to.Equals(toRoomId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(terrain) && options.AvoidTerrains.Contains(terrain)) continue;
                    if (!to.Equals(toRoomId, StringComparison.Ordinal) && dangerousRooms.Contains(to)) continue;
                    if (previous.ContainsKey(to)) continue;

                    _ = Enum.TryParse(doorState, true, out ExitDoorState parsedDoorState);
                    _ = Enum.TryParse(traversability, true, out ExitTraversability parsedTraversability);
                    KnowledgeRouteStep step = new(
                        from,
                        direction,
                        to,
                        name,
                        parsedDoorState,
                        parsedTraversability,
                        blockReason);
                    previous[to] = step;
                    if (to.Equals(toRoomId, StringComparison.Ordinal)) return BuildRoute(fromRoomId, toRoomId, previous);
                    next.Add(to);
                }
            }
            frontier = next;
        }
        return null;
    }

    public async Task<(MapperDestinationSearchResult Destination, KnowledgeRoute Route)?> FindNearestAsync(
        string fromRoomId,
        string query,
        RoutePlanningOptions options,
        int candidateLimit = 30,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MapperDestinationSearchResult> candidates = await SearchAsync(query, candidateLimit, cancellationToken).ConfigureAwait(false);
        (MapperDestinationSearchResult Destination, KnowledgeRoute Route)? best = null;
        foreach (MapperDestinationSearchResult candidate in candidates
                     .GroupBy(candidate => candidate.RoomId, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            KnowledgeRoute? route = await FindRouteAsync(fromRoomId, candidate.RoomId, options, cancellationToken).ConfigureAwait(false);
            if (route is null) continue;
            if (best is null || route.Steps.Count < best.Value.Route.Steps.Count)
                best = (candidate, route);
            if (route.Steps.Count == 0) break;
        }
        return best;
    }

    private static async Task<HashSet<string>> LoadAvoidMobRoomsAsync(
        SqliteConnection connection,
        IReadOnlySet<string> mobNames,
        CancellationToken cancellationToken)
    {
        HashSet<string> rooms = new(StringComparer.Ordinal);
        if (mobNames.Count == 0) return rooms;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 2;
        List<string> predicates = [];
        int index = 0;
        foreach (string mob in mobNames)
        {
            string parameter = "$mob" + index++;
            predicates.Add($"instr(lower(COALESCE(canonical_name, description)), lower({parameter})) > 0");
            command.Parameters.AddWithValue(parameter, mob.Trim());
        }
        command.CommandText = $"""
            SELECT DISTINCT room_id
            FROM room_entities
            WHERE kind = $kind
              AND ({string.Join(" OR ", predicates)});
            """;
        command.Parameters.AddWithValue("$kind", RoomEntityKind.Occupant.ToString());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) rooms.Add(reader.GetString(0));
        return rooms;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=500;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static KnowledgeRoute BuildRoute(
        string fromRoomId,
        string toRoomId,
        IReadOnlyDictionary<string, KnowledgeRouteStep?> previous)
    {
        List<KnowledgeRouteStep> route = [];
        string cursor = toRoomId;
        while (!cursor.Equals(fromRoomId, StringComparison.Ordinal))
        {
            KnowledgeRouteStep edge = previous[cursor]!;
            route.Add(edge);
            cursor = edge.FromRoomId;
        }
        route.Reverse();
        return new KnowledgeRoute(fromRoomId, toRoomId, route);
    }

    private static int SearchMatchRank(string displayName, string query)
    {
        if (displayName.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (displayName.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    private static int ToInt32(long value) => value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : null;
}
