namespace NexMud.Client.Knowledge;

/// <summary>
/// Presentation coordinate for the visual mapper. Coordinates are intentionally non-authoritative:
/// MUD topology may fold, overlap, loop, or use non-spatial transitions which cannot be embedded in
/// a Euclidean grid without distortion.
/// </summary>
public readonly record struct MapperCoordinate(int X, int Y, int Level = 0)
{
    public static MapperCoordinate operator +(MapperCoordinate left, MapperCoordinate right) =>
        new(left.X + right.X, left.Y + right.Y, left.Level + right.Level);

    public static MapperCoordinate operator -(MapperCoordinate left, MapperCoordinate right) =>
        new(left.X - right.X, left.Y - right.Y, left.Level - right.Level);
}

public sealed record MapperLayoutRoom(
    MapperGraphRoom Room,
    MapperCoordinate Coordinate,
    int StackIndex,
    int StackCount,
    bool Disconnected);

public sealed record MapperLayoutEdge(
    MapperGraphEdge Edge,
    bool Spatial,
    bool Vertical,
    bool ConstraintConflict);

public sealed record MapperTopologySnapshot(
    string OriginRoomId,
    IReadOnlyDictionary<string, MapperLayoutRoom> Rooms,
    IReadOnlyList<MapperLayoutEdge> Edges,
    int ConstraintConflictCount,
    int CoordinateCollisionCount);

/// <summary>
/// Produces a readable MUD map without treating screen coordinates as world geometry. Cardinal
/// directions bias where a newly discovered room is drawn, but cycles and folded spaces never make
/// the underlying graph invalid. Up/down and custom exits are portal-style links rather than global
/// floors. The authoritative model is always the observed directed exit graph.
/// </summary>
public static class MapperTopologyLayout
{
    private static readonly IReadOnlyDictionary<string, MapperCoordinate> Offsets =
        new Dictionary<string, MapperCoordinate>(StringComparer.OrdinalIgnoreCase)
        {
            ["n"] = new(0, -1), ["north"] = new(0, -1),
            ["s"] = new(0, 1), ["south"] = new(0, 1),
            ["e"] = new(1, 0), ["east"] = new(1, 0),
            ["w"] = new(-1, 0), ["west"] = new(-1, 0),
            ["ne"] = new(1, -1), ["northeast"] = new(1, -1),
            ["nw"] = new(-1, -1), ["northwest"] = new(-1, -1),
            ["se"] = new(1, 1), ["southeast"] = new(1, 1),
            ["sw"] = new(-1, 1), ["southwest"] = new(-1, 1),
            ["u"] = new(0, 0, 1), ["up"] = new(0, 0, 1),
            ["d"] = new(0, 0, -1), ["down"] = new(0, 0, -1)
        };

    private static readonly MapperCoordinate[] GenericPortalSlots =
    [
        new(2, -1), new(2, 1), new(-2, -1), new(-2, 1),
        new(0, -2), new(0, 2), new(3, 0), new(-3, 0)
    ];

    private static readonly MapperCoordinate[] UpPortalSlots =
    [
        new(-1, -1), new(-2, -1), new(-1, -2), new(-2, -2), new(0, -2)
    ];

    private static readonly MapperCoordinate[] DownPortalSlots =
    [
        new(1, 1), new(2, 1), new(1, 2), new(2, 2), new(0, 2)
    ];

    public static MapperTopologySnapshot Build(MapperGraphSnapshot graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Dictionary<string, MapperGraphRoom> rooms = graph.Rooms
            .GroupBy(room => room.RoomId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToDictionary(room => room.RoomId, StringComparer.Ordinal);

        if (rooms.Count == 0)
        {
            return new MapperTopologySnapshot(
                graph.OriginRoomId,
                new Dictionary<string, MapperLayoutRoom>(StringComparer.Ordinal),
                Array.Empty<MapperLayoutEdge>(),
                0,
                0);
        }

        string origin = rooms.ContainsKey(graph.OriginRoomId)
            ? graph.OriginRoomId
            : rooms.Keys.OrderBy(value => value, StringComparer.Ordinal).First();

        Dictionary<string, List<MapperGraphEdge>> incident = BuildIncidentEdges(graph.Edges, rooms);
        Dictionary<string, MapperCoordinate> positions = new(StringComparer.Ordinal);
        HashSet<MapperCoordinate> occupied = [];
        HashSet<string> connectedToOrigin = new(StringComparer.Ordinal);

        PlaceComponent(origin, new MapperCoordinate(0, 0), connectedToOrigin, positions, occupied, incident, rooms);

        int component = 0;
        foreach (string roomId in rooms.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (positions.ContainsKey(roomId)) continue;
            MapperCoordinate anchor = FindNearestFree(new MapperCoordinate(7 + component * 6, 0), occupied);
            HashSet<string> detached = new(StringComparer.Ordinal);
            PlaceComponent(roomId, anchor, detached, positions, occupied, incident, rooms);
            component++;
        }

        Dictionary<string, MapperLayoutRoom> layoutRooms = new(StringComparer.Ordinal);
        foreach ((string roomId, MapperCoordinate coordinate) in positions)
        {
            if (!rooms.TryGetValue(roomId, out MapperGraphRoom? room)) continue;
            layoutRooms[roomId] = new MapperLayoutRoom(
                room,
                coordinate,
                0,
                1,
                !connectedToOrigin.Contains(roomId));
        }

        List<MapperLayoutEdge> layoutEdges = graph.Edges
            .Select(edge =>
            {
                bool knownDirection = TryGetOffset(edge.Direction, out MapperCoordinate offset);
                bool vertical = knownDirection && offset.Level != 0;
                bool planar = knownDirection && !vertical;
                return new MapperLayoutEdge(edge, planar, vertical, false);
            })
            .ToList();

        // Coordinate conflicts are not topology failures. The layout guarantees unique presentation
        // slots while preserving directional bias, so these legacy counters intentionally stay zero.
        return new MapperTopologySnapshot(origin, layoutRooms, layoutEdges, 0, 0);
    }

    public static bool TryGetOffset(string? direction, out MapperCoordinate offset)
    {
        if (string.IsNullOrWhiteSpace(direction))
        {
            offset = default;
            return false;
        }
        return Offsets.TryGetValue(direction.Trim(), out offset);
    }

    public static string NormalizeDirection(string direction) => direction.Trim().ToLowerInvariant() switch
    {
        "n" => "north",
        "s" => "south",
        "e" => "east",
        "w" => "west",
        "ne" => "northeast",
        "nw" => "northwest",
        "se" => "southeast",
        "sw" => "southwest",
        "u" => "up",
        "d" => "down",
        var value => value
    };

    public static string AbbreviateDirection(string direction) => NormalizeDirection(direction) switch
    {
        "north" => "N",
        "south" => "S",
        "east" => "E",
        "west" => "W",
        "northeast" => "NE",
        "northwest" => "NW",
        "southeast" => "SE",
        "southwest" => "SW",
        "up" => "U",
        "down" => "D",
        var value => value.ToUpperInvariant()
    };

    private static void PlaceComponent(
        string origin,
        MapperCoordinate anchor,
        ISet<string> reached,
        IDictionary<string, MapperCoordinate> positions,
        ISet<MapperCoordinate> occupied,
        IReadOnlyDictionary<string, List<MapperGraphEdge>> incident,
        IReadOnlyDictionary<string, MapperGraphRoom> rooms)
    {
        positions[origin] = anchor;
        occupied.Add(anchor);
        reached.Add(origin);
        Queue<string> queue = new();
        queue.Enqueue(origin);

        while (queue.Count > 0)
        {
            string currentId = queue.Dequeue();
            if (!positions.TryGetValue(currentId, out MapperCoordinate current) ||
                !incident.TryGetValue(currentId, out List<MapperGraphEdge>? edges)) continue;

            foreach (MapperGraphEdge edge in edges
                         .OrderBy(EdgeLayoutPriority)
                         .ThenBy(edge => edge.Direction, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(edge => edge.ToRoomId, StringComparer.Ordinal))
            {
                bool outgoing = string.Equals(edge.FromRoomId, currentId, StringComparison.Ordinal);
                string neighbor = outgoing ? edge.ToRoomId : edge.FromRoomId;
                if (!rooms.ContainsKey(neighbor) || positions.ContainsKey(neighbor)) continue;

                MapperCoordinate candidate = ChoosePresentationSlot(current, edge.Direction, outgoing, occupied);
                positions[neighbor] = candidate;
                occupied.Add(candidate);
                reached.Add(neighbor);
                queue.Enqueue(neighbor);
            }
        }
    }

    private static int EdgeLayoutPriority(MapperGraphEdge edge)
    {
        if (!TryGetOffset(edge.Direction, out MapperCoordinate offset)) return 2;
        return offset.Level == 0 ? 0 : 1;
    }

    private static MapperCoordinate ChoosePresentationSlot(
        MapperCoordinate anchor,
        string direction,
        bool outgoing,
        ISet<MapperCoordinate> occupied)
    {
        if (TryGetOffset(direction, out MapperCoordinate offset))
        {
            if (!outgoing) offset = new MapperCoordinate(-offset.X, -offset.Y, -offset.Level);
            if (offset.Level == 0)
                return FindDirectionalSlot(anchor, offset, occupied);

            bool up = offset.Level > 0;
            return FindPortalSlot(anchor, up ? UpPortalSlots : DownPortalSlots, occupied);
        }

        return FindPortalSlot(anchor, GenericPortalSlots, occupied);
    }

    private static MapperCoordinate FindDirectionalSlot(
        MapperCoordinate anchor,
        MapperCoordinate direction,
        ISet<MapperCoordinate> occupied)
    {
        MapperCoordinate ideal = new(anchor.X + direction.X, anchor.Y + direction.Y);
        if (!occupied.Contains(ideal)) return ideal;

        int perpendicularX = -direction.Y;
        int perpendicularY = direction.X;
        for (int depth = 1; depth <= 8; depth++)
        {
            for (int lateral = 0; lateral <= 8; lateral++)
            {
                foreach (int sign in lateral == 0 ? new[] { 1 } : new[] { -1, 1 })
                {
                    int lane = lateral * sign;
                    MapperCoordinate candidate = new(
                        anchor.X + direction.X * depth + perpendicularX * lane,
                        anchor.Y + direction.Y * depth + perpendicularY * lane);
                    if (!occupied.Contains(candidate)) return candidate;
                }
            }
        }

        return FindNearestFree(ideal, occupied);
    }

    private static MapperCoordinate FindPortalSlot(
        MapperCoordinate anchor,
        IReadOnlyList<MapperCoordinate> slots,
        ISet<MapperCoordinate> occupied)
    {
        foreach (MapperCoordinate slot in slots)
        {
            MapperCoordinate candidate = new(anchor.X + slot.X, anchor.Y + slot.Y);
            if (!occupied.Contains(candidate)) return candidate;
        }
        return FindNearestFree(new MapperCoordinate(anchor.X + 2, anchor.Y + 2), occupied);
    }

    private static MapperCoordinate FindNearestFree(MapperCoordinate desired, ISet<MapperCoordinate> occupied)
    {
        if (!occupied.Contains(desired)) return desired;
        for (int radius = 1; radius <= 64; radius++)
        {
            for (int x = -radius; x <= radius; x++)
            {
                foreach (int y in new[] { -radius, radius })
                {
                    MapperCoordinate candidate = new(desired.X + x, desired.Y + y);
                    if (!occupied.Contains(candidate)) return candidate;
                }
            }
            for (int y = -radius + 1; y < radius; y++)
            {
                foreach (int x in new[] { -radius, radius })
                {
                    MapperCoordinate candidate = new(desired.X + x, desired.Y + y);
                    if (!occupied.Contains(candidate)) return candidate;
                }
            }
        }
        return new MapperCoordinate(desired.X + occupied.Count + 1, desired.Y);
    }

    private static Dictionary<string, List<MapperGraphEdge>> BuildIncidentEdges(
        IReadOnlyList<MapperGraphEdge> edges,
        IReadOnlyDictionary<string, MapperGraphRoom> rooms)
    {
        Dictionary<string, List<MapperGraphEdge>> incident = new(StringComparer.Ordinal);
        foreach (MapperGraphEdge edge in edges)
        {
            if (!rooms.ContainsKey(edge.FromRoomId) || !rooms.ContainsKey(edge.ToRoomId)) continue;
            AddIncident(incident, edge.FromRoomId, edge);
            if (!string.Equals(edge.FromRoomId, edge.ToRoomId, StringComparison.Ordinal))
                AddIncident(incident, edge.ToRoomId, edge);
        }
        return incident;
    }

    private static void AddIncident(
        IDictionary<string, List<MapperGraphEdge>> incident,
        string roomId,
        MapperGraphEdge edge)
    {
        if (!incident.TryGetValue(roomId, out List<MapperGraphEdge>? list))
        {
            list = [];
            incident[roomId] = list;
        }
        list.Add(edge);
    }
}
