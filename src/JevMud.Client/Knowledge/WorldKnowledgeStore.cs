using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using JevMud.Adapters.Avendar;
using JevMud.Client.Settings;
using JevMud.Contracts.Events;
using JevMud.Contracts.Jev;
using JevMud.Contracts.State;
using JevMud.Core.Events;
using JevMud.Core.State;
using Microsoft.Data.Sqlite;

namespace JevMud.Client.Knowledge;

public sealed record KnowledgeSummary(
    string DatabasePath,
    long Events,
    long Sessions,
    long Rooms,
    long Entities,
    long CombatEvents,
    long Communications,
    long Commands,
    long JevDecisions,
    long Items,
    long AbilityHelpDocuments,
    DateTimeOffset? LastUpdatedAt)
{
    public static KnowledgeSummary Empty(string path) => new(path, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null);
}

public sealed record RoomKnowledge(
    string RoomId,
    string? Name,
    string? Description,
    string? Terrain,
    string? Light,
    int VisitCount,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? LastSeenAt,
    IReadOnlyList<KnowledgeExit> Exits,
    IReadOnlyList<KnowledgeEntity> FrequentEntities);

public sealed record KnowledgeExit(
    string Direction,
    string? ToRoomId,
    string? ToRoomName,
    string? DoorState,
    string? Traversability,
    string? BlockReason,
    DateTimeOffset? LastSeenAt);

public sealed record MapperRoomMetadata(
    string RoomId,
    string? Label,
    string? Area,
    string? Notes,
    bool Avoid);


public sealed record MapperRoomSearchResult(
    string RoomId,
    string? Name,
    string? Label,
    string? Area,
    bool Avoid);

public enum MapperDestinationKind
{
    Room,
    Entity,
    Object,
    Fixture,
    ItemSource
}

public sealed record MapperDestinationSearchResult(
    MapperDestinationKind Kind,
    string DisplayName,
    string RoomId,
    string? RoomName,
    string? Area,
    int ObservationCount,
    DateTimeOffset? LastSeenAt,
    bool Avoid);

public sealed record MapperGraphRoom(
    string RoomId,
    string? Name,
    string? Label,
    string? Area,
    bool Avoid,
    int VisitCount,
    DateTimeOffset? LastSeenAt,
    int UnexploredExitCount = 0,
    IReadOnlyList<string>? UnexploredDirections = null);

public sealed record MapperGraphEdge(
    string FromRoomId,
    string Direction,
    string ToRoomId,
    string? Traversability,
    ExitDoorState DoorState = ExitDoorState.Unknown,
    string? BlockReason = null);

public sealed record MapperGraphSnapshot(
    string OriginRoomId,
    IReadOnlyList<MapperGraphRoom> Rooms,
    IReadOnlyList<MapperGraphEdge> Edges);

public enum CodexEntryKind
{
    Room,
    Entity,
    Item,
    Ability,
    Combat
}

public sealed record CodexEntrySummary(
    CodexEntryKind Kind,
    string Key,
    string Title,
    string? Subtitle,
    int ObservationCount,
    DateTimeOffset? LastSeenAt);

public sealed record CodexLocation(
    string RoomId,
    string? RoomName,
    string? Area,
    int ObservationCount,
    DateTimeOffset? LastSeenAt,
    string? EntityName = null,
    string? EntityKey = null);

public sealed record CodexRelatedItem(
    string ItemName,
    int ObservationCount,
    DateTimeOffset? LastSeenAt);

public sealed record CodexEntryDetail(
    CodexEntryKind Kind,
    string Key,
    string Title,
    string? Subtitle,
    string? Description,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<CodexLocation> Locations,
    string? RawText,
    int ObservationCount,
    DateTimeOffset? LastSeenAt)
{
    public IReadOnlyList<CodexRelatedItem> RelatedItems { get; init; } = Array.Empty<CodexRelatedItem>();
}

public sealed record KnowledgeRouteStep(
    string FromRoomId,
    string Direction,
    string ToRoomId,
    string? ToRoomName,
    ExitDoorState DoorState = ExitDoorState.Unknown,
    ExitTraversability Traversability = ExitTraversability.Unknown,
    string? BlockReason = null);

public sealed record KnowledgeRoute(
    string FromRoomId,
    string ToRoomId,
    IReadOnlyList<KnowledgeRouteStep> Steps)
{
    public string Speedwalk => string.Join(" ", Steps.Select(step => step.Direction));
}

public sealed record KnowledgeEntity(
    string Kind,
    string Description,
    string? CanonicalName,
    int ObservationCount,
    DateTimeOffset? LastSeenAt);

public sealed record CombatMemoryEvent(
    DateTimeOffset? ObservedAt,
    string EventType,
    string? RoomId,
    JsonElement Payload);

public sealed record CombatKnowledge(
    string Target,
    int EncounterEvents,
    int KillCount,
    DateTimeOffset? LastSeenAt,
    IReadOnlyList<CombatMemoryEvent> RecentEvents);

public sealed record JevMemorySummary(
    long Sessions,
    long Rooms,
    long Entities,
    long CombatEvents,
    long JevDecisions);

public sealed record ItemKnowledge(
    string Name,
    IReadOnlyList<string> Flags,
    decimal? Weight,
    IReadOnlyList<string> WearLocations,
    int? Level,
    string? Material,
    string? ItemType,
    string? WeaponType,
    IReadOnlyList<string> WeaponFlags,
    string? DamageType,
    string? DamageDice,
    decimal? DamageAverage,
    IReadOnlyDictionary<string, string> ExtraFields,
    int ObservationCount,
    DateTimeOffset? LastSeenAt);

public sealed record AbilityHelpKnowledge(
    string Name,
    AbilityHelpKind Kind,
    decimal? ActivationLagRounds,
    int? ActivationManaCost,
    string? Syntax,
    string Description,
    IReadOnlyDictionary<string, string> Fields,
    int ObservationCount,
    DateTimeOffset? LastSeenAt);

public sealed record ExecutedCommandKnowledge(
    string Command,
    string Source,
    DateTimeOffset? ExecutedAt);

public sealed record CommunicationKnowledge(
    string Channel,
    string? Speaker,
    string Message,
    DateTimeOffset? ObservedAt);

public sealed record JevPersistentContext(
    RoomKnowledge? CurrentRoom,
    CombatKnowledge? TargetHistory,
    IReadOnlyList<ExecutedCommandKnowledge> RecentCommands,
    IReadOnlyList<CommunicationKnowledge> RecentCommunications,
    IReadOnlyList<ItemKnowledge> EquippedItems,
    IReadOnlyList<AbilityHelpKnowledge> AbilityReference,
    JevMemorySummary Summary);

/// <summary>
/// Durable, append-friendly semantic memory for the client. The event stream remains authoritative
/// for live state; this store preserves observations across sessions for reference and Jev context.
/// </summary>
public sealed class WorldKnowledgeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ChannelReader<EventEnvelope> _events;
    private readonly StateReducer _state;
    private readonly string _connectionString;
    private readonly Func<MapperPreferences> _mapperSettings;
    private readonly SemaphoreSlim _schemaInitializationGate = new(1, 1);
    private int _schemaInitialized;
    private KnowledgeSummary _summary;
    private RoomKnowledge? _currentRoomKnowledge;
    private string? _sessionId;
    private string? _currentRoomId;
    private string? _previousRoomId;
    private readonly Queue<PendingTraversal> _pendingTraversals = new();
    private int _eventsSinceSummary;

    private static readonly TimeSpan PendingTraversalLifetime = TimeSpan.FromSeconds(30);

    private sealed record PendingTraversal(string Direction, string? SourceRoomId, DateTimeOffset AttemptedAt, Guid? ActionId);

    public WorldKnowledgeStore(
        ChannelReader<EventEnvelope> events,
        StateReducer state,
        string? databasePath = null,
        Func<MapperPreferences>? mapperSettings = null)
    {
        _events = events;
        _state = state;
        _mapperSettings = mapperSettings ?? (() => new MapperPreferences());
        DatabasePath = databasePath ?? GetDefaultPath();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private
        }.ToString();
        _summary = KnowledgeSummary.Empty(DatabasePath);
    }

    public string DatabasePath { get; }
    public KnowledgeSummary Summary => Volatile.Read(ref _summary);
    public RoomKnowledge? CurrentRoom => Volatile.Read(ref _currentRoomKnowledge);

    /// <summary>Raised after mapper-relevant knowledge has been committed to SQLite.</summary>
    public event Action? MapperKnowledgeChanged;

    private void NotifyMapperKnowledgeChanged()
    {
        try
        {
            MapperKnowledgeChanged?.Invoke();
        }
        catch
        {
            // Mapper refresh is advisory. A UI observer must never take down durable ingestion.
        }
    }

    private bool MapperRecordingEnabled()
    {
        MapperPreferences settings = _mapperSettings();
        return settings.Enabled && settings.AutoMap;
    }

    private bool MapperPersistenceEnabled()
    {
        MapperPreferences settings = _mapperSettings();
        return settings.Enabled && settings.AutoMap && settings.PersistKnowledge;
    }

    public static string GetDefaultPath() =>
        JevMud.Client.Paths.NexMudDataPaths.GetFilePath("knowledge.db", includeSqliteSidecars: true);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
        await RefreshSummaryAsync(connection, cancellationToken).ConfigureAwait(false);

        try
        {
            await foreach (EventEnvelope envelope in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await PersistEventAsync(connection, envelope, cancellationToken).ConfigureAwait(false);
                    _eventsSinceSummary++;
                    if (_eventsSinceSummary >= 100 || envelope.Payload is RoomObservationObserved or EnemyKilled or ConnectionStateChanged or JevDecisionProduced or ItemIdentified or ItemAcquired or AbilityHelpObserved)
                    {
                        _eventsSinceSummary = 0;
                        await RefreshSummaryAsync(connection, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (SqliteException) when (!cancellationToken.IsCancellationRequested)
                {
                    // SQLite is durable context, never part of the live control path. A transient
                    // lock or filesystem failure may lose one observation, but must not kill the
                    // long-running memory consumer for the rest of the session.
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task<IReadOnlyList<MapperRoomSearchResult>> FindRoomsAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<MapperRoomSearchResult>();
        limit = Math.Clamp(limit, 1, 100);
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.room_id, r.name, m.label, m.area, COALESCE(m.avoid, 0)
            FROM rooms r
            LEFT JOIN room_metadata m ON m.room_id = r.room_id
            WHERE r.room_id = $exact OR r.name LIKE $like COLLATE NOCASE OR m.label LIKE $like COLLATE NOCASE
            ORDER BY CASE WHEN r.room_id = $exact THEN 0
                          WHEN m.label = $exact COLLATE NOCASE THEN 1
                          WHEN r.name = $exact COLLATE NOCASE THEN 2
                          ELSE 3 END,
                     COALESCE(m.label, r.name, r.room_id)
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$exact", query.Trim());
        command.Parameters.AddWithValue("$like", "%" + query.Trim() + "%");
        command.Parameters.AddWithValue("$limit", limit);
        List<MapperRoomSearchResult> results = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new MapperRoomSearchResult(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                !reader.IsDBNull(4) && reader.GetInt64(4) != 0));
        }
        return results;
    }

    public async Task<IReadOnlyList<MapperDestinationSearchResult>> FindMapDestinationsAsync(
        string query,
        int limit = 24,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<MapperDestinationSearchResult>();
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        MapperReadRepository mapper = new(DatabasePath);
        return await mapper.SearchAsync(query, Math.Clamp(limit, 1, 100), cancellationToken).ConfigureAwait(false);
    }

    public async Task<MapperGraphSnapshot?> GetMapGraphAsync(
        string originRoomId,
        int maximumDepth = 8,
        int maximumRooms = 120,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(originRoomId)) return null;
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        MapperReadRepository mapper = new(DatabasePath);
        return await mapper.LoadNeighborhoodAsync(
            originRoomId,
            Math.Clamp(maximumDepth, 1, 50),
            Math.Clamp(maximumRooms, 10, 250),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveRoomMetadataAsync(MapperRoomMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(metadata.RoomId)) throw new ArgumentException("Room ID is required.", nameof(metadata));
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO room_metadata(room_id, label, area, notes, avoid)
            VALUES($roomId, $label, $area, $notes, $avoid)
            ON CONFLICT(room_id) DO UPDATE SET
                label = excluded.label,
                area = excluded.area,
                notes = excluded.notes,
                avoid = excluded.avoid;
            """;
        command.Parameters.AddWithValue("$roomId", metadata.RoomId);
        command.Parameters.AddWithValue("$label", (object?)metadata.Label ?? DBNull.Value);
        command.Parameters.AddWithValue("$area", (object?)metadata.Area ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)metadata.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$avoid", metadata.Avoid ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        NotifyMapperKnowledgeChanged();
    }

    public async Task SaveMapExitAsync(
        string fromRoomId,
        string directionOrCommand,
        string toRoomId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fromRoomId)) throw new ArgumentException("Source room ID is required.", nameof(fromRoomId));
        if (string.IsNullOrWhiteSpace(directionOrCommand)) throw new ArgumentException("Exit direction/command is required.", nameof(directionOrCommand));
        if (string.IsNullOrWhiteSpace(toRoomId)) throw new ArgumentException("Destination room ID is required.", nameof(toRoomId));
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO room_exits(from_room_id, direction, to_room_id, door_state, traversability, block_reason, last_seen_at)
            VALUES($from, $direction, $to, $doorState, $traversability, NULL, $observedAt)
            ON CONFLICT(from_room_id, direction) DO UPDATE SET
                to_room_id = excluded.to_room_id,
                door_state = excluded.door_state,
                traversability = excluded.traversability,
                block_reason = NULL,
                last_seen_at = excluded.last_seen_at;
            """;
        command.Parameters.AddWithValue("$from", fromRoomId.Trim());
        command.Parameters.AddWithValue("$direction", directionOrCommand.Trim());
        command.Parameters.AddWithValue("$to", toRoomId.Trim());
        command.Parameters.AddWithValue("$doorState", ExitDoorState.Unknown.ToString());
        command.Parameters.AddWithValue("$traversability", ExitTraversability.Traversable.ToString());
        command.Parameters.AddWithValue("$observedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        NotifyMapperKnowledgeChanged();
    }

    public async Task<KnowledgeRoute?> FindRouteAsync(
        string fromRoomId,
        string toRoomId,
        int maximumDepth = 250,
        bool avoidBlockedExits = true,
        bool allowUnknownTraversability = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fromRoomId) || string.IsNullOrWhiteSpace(toRoomId)) return null;
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        MapperReadRepository mapper = new(DatabasePath);
        return await mapper.FindRouteAsync(
            fromRoomId,
            toRoomId,
            maximumDepth,
            avoidBlockedExits,
            allowUnknownTraversability,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<JevPersistentContext?> GetDecisionContextAsync(
        StateSnapshot state,
        CancellationToken cancellationToken = default)
    {
        if (state.Session.ConnectionStatus != ConnectionStatus.Connected)
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
            await using SqliteConnection connection = new(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);

            RoomKnowledge? room = !string.IsNullOrWhiteSpace(state.Room.Id)
                ? await ReadRoomAsync(connection, state.Room.Id!, cancellationToken).ConfigureAwait(false)
                : null;

            string? target = state.Combat.TargetName ?? state.Combat.TargetId;
            CombatKnowledge? combat = !string.IsNullOrWhiteSpace(target)
                ? await ReadCombatKnowledgeAsync(connection, target!, cancellationToken).ConfigureAwait(false)
                : null;
            IReadOnlyList<ExecutedCommandKnowledge> recentCommands = await ReadRecentExecutedCommandsAsync(
                connection,
                limit: 16,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            IReadOnlyList<CommunicationKnowledge> recentCommunications = await ReadRecentCommunicationsAsync(
                connection,
                state.Room.Id,
                limit: 8,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            List<ItemKnowledge> equippedItems = [];
            foreach (string itemName in state.Character.Equipment.Slots
                         .Where(slot => !slot.IsEmpty)
                         .Select(slot => slot.Item!)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Take(12))
            {
                ItemKnowledge? knownItem = await ReadItemKnowledgeAsync(connection, itemName, cancellationToken)
                    .ConfigureAwait(false);
                if (knownItem is not null)
                {
                    equippedItems.Add(knownItem);
                }
            }

            List<AbilityHelpKnowledge> abilityReference = [];
            foreach (SkillState skill in state.Character.Skills
                         .Where(skill => skill.Availability == SkillAvailability.Available &&
                                         AvendarActionCapabilities.KnownActions.ContainsKey(skill.Name))
                         .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase))
            {
                AbilityHelpKnowledge? knownHelp = await ReadAbilityHelpKnowledgeAsync(connection, skill.Name, cancellationToken)
                    .ConfigureAwait(false);
                if (knownHelp is not null)
                {
                    abilityReference.Add(knownHelp with
                    {
                        Description = Truncate(NormalizeSemanticText(knownHelp.Description), 1200) ?? string.Empty,
                        Syntax = NormalizeSemanticText(knownHelp.Syntax),
                        Fields = NormalizeFields(knownHelp.Fields)
                    });
                }
            }

            KnowledgeSummary summary = Summary;
            RoomKnowledge? boundedRoom = room is null ? null : BoundRoomForJev(room);
            IReadOnlyList<ExecutedCommandKnowledge> boundedCommands = recentCommands
                .Select(command => command with { Command = Truncate(command.Command, 256)! })
                .ToArray();
            return new JevPersistentContext(
                boundedRoom,
                combat,
                boundedCommands,
                recentCommunications,
                equippedItems,
                abilityReference,
                new JevMemorySummary(
                    summary.Sessions,
                    summary.Rooms,
                    summary.Entities,
                    summary.CombatEvents,
                    summary.JevDecisions));
        }
        catch (SqliteException)
        {
            // Durable memory is an enhancement. Jev must remain usable if the local DB is unavailable.
            return null;
        }
    }

    public Task<JevPersistentContext?> GetCombatContextAsync(
        StateSnapshot state,
        CancellationToken cancellationToken = default) =>
        GetDecisionContextAsync(state, cancellationToken);

    public async Task<RoomKnowledge?> GetRoomAsync(string roomId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roomId))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
        return await ReadRoomAsync(connection, roomId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MapperRoomMetadata?> GetRoomMetadataAsync(
        string roomId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roomId)) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT label, area, notes, avoid FROM room_metadata WHERE room_id = $roomId LIMIT 1;";
        command.Parameters.AddWithValue("$roomId", roomId.Trim());
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;

        return new MapperRoomMetadata(
            roomId.Trim(),
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            !reader.IsDBNull(3) && reader.GetInt64(3) != 0);
    }

    public async Task<IReadOnlyList<CodexEntrySummary>> SearchCodexAsync(
        CodexEntryKind? kind,
        string? query,
        int limit = 72,
        CancellationToken cancellationToken = default)
    {
        int boundedLimit = Math.Clamp(limit, 1, 200);
        string normalizedQuery = query?.Trim() ?? string.Empty;

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);

        if (kind is not null)
        {
            return kind.Value switch
            {
                CodexEntryKind.Room => await SearchCodexRoomsAsync(connection, normalizedQuery, boundedLimit, cancellationToken).ConfigureAwait(false),
                CodexEntryKind.Entity => await SearchCodexEntitiesAsync(connection, normalizedQuery, boundedLimit, cancellationToken).ConfigureAwait(false),
                CodexEntryKind.Item => await SearchCodexItemsAsync(connection, normalizedQuery, boundedLimit, cancellationToken).ConfigureAwait(false),
                CodexEntryKind.Ability => await SearchCodexAbilitiesAsync(connection, normalizedQuery, boundedLimit, cancellationToken).ConfigureAwait(false),
                CodexEntryKind.Combat => await SearchCodexCombatAsync(connection, normalizedQuery, boundedLimit, cancellationToken).ConfigureAwait(false),
                _ => Array.Empty<CodexEntrySummary>()
            };
        }

        List<CodexEntrySummary> results = [];
        results.AddRange(await SearchCodexRoomsAsync(connection, normalizedQuery, boundedLimit, cancellationToken).ConfigureAwait(false));
        results.AddRange(await SearchCodexEntitiesAsync(connection, normalizedQuery, boundedLimit, cancellationToken).ConfigureAwait(false));
        results.AddRange(await SearchCodexItemsAsync(connection, normalizedQuery, boundedLimit, cancellationToken).ConfigureAwait(false));
        results.AddRange(await SearchCodexAbilitiesAsync(connection, normalizedQuery, boundedLimit, cancellationToken).ConfigureAwait(false));
        results.AddRange(await SearchCodexCombatAsync(connection, normalizedQuery, boundedLimit, cancellationToken).ConfigureAwait(false));

        return results
            .OrderByDescending(entry => entry.LastSeenAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(entry => entry.ObservationCount)
            .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
            .Take(boundedLimit)
            .ToArray();
    }

    public async Task<CodexEntryDetail?> GetCodexEntryAsync(
        CodexEntryKind kind,
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);

        string normalizedKey = key.Trim();
        return kind switch
        {
            CodexEntryKind.Room => await ReadCodexRoomAsync(connection, normalizedKey, cancellationToken).ConfigureAwait(false),
            CodexEntryKind.Entity => await ReadCodexEntityAsync(connection, normalizedKey, cancellationToken).ConfigureAwait(false),
            CodexEntryKind.Item => await ReadCodexItemAsync(connection, normalizedKey, cancellationToken).ConfigureAwait(false),
            CodexEntryKind.Ability => await ReadCodexAbilityAsync(connection, normalizedKey, cancellationToken).ConfigureAwait(false),
            CodexEntryKind.Combat => await ReadCodexCombatAsync(connection, normalizedKey, cancellationToken).ConfigureAwait(false),
            _ => null
        };
    }

    public async Task<ItemKnowledge?> GetItemAsync(string itemName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
        return await ReadItemKnowledgeAsync(connection, itemName.Trim(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<AbilityHelpKnowledge?> GetAbilityHelpAsync(string abilityName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(abilityName))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
        return await ReadAbilityHelpKnowledgeAsync(connection, abilityName.Trim(), cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordCommandAsync(
        string command,
        bool sentToMud,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);

        using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO command_history(entered_at, command, sent_to_mud) VALUES($enteredAt, $command, $sentToMud);";
        insert.Parameters.AddWithValue("$enteredAt", DateTimeOffset.UtcNow.ToString("O"));
        insert.Parameters.AddWithValue("$command", command);
        insert.Parameters.AddWithValue("$sentToMud", sentToMud ? 1 : 0);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        KnowledgeSummary current = Summary;
        Volatile.Write(ref _summary, current with
        {
            Commands = current.Commands + 1,
            LastUpdatedAt = DateTimeOffset.UtcNow
        });
    }

    public async Task<IReadOnlyList<string>> GetRecentCommandsAsync(
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        int boundedLimit = Math.Clamp(limit, 1, 1000);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
        return await ReadRecentCommandsAsync(connection, boundedLimit, sentToMudOnly: false, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<CodexEntrySummary>> SearchCodexRoomsAsync(
        SqliteConnection connection,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        List<CodexEntrySummary> results = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.room_id, COALESCE(m.label, r.name, 'Known room'), m.area, r.visit_count, r.last_seen_at
            FROM rooms r
            LEFT JOIN room_metadata m ON m.room_id = r.room_id
            WHERE $query = ''
               OR r.room_id = $query
               OR r.name LIKE $like COLLATE NOCASE
               OR m.label LIKE $like COLLATE NOCASE
               OR m.area LIKE $like COLLATE NOCASE
               OR r.description LIKE $like COLLATE NOCASE
            ORDER BY r.last_seen_at DESC, r.visit_count DESC
            LIMIT $limit;
            """;
        AddSearchParameters(command, query, limit);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CodexEntrySummary(
                CodexEntryKind.Room,
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? "Room" : $"Room · {reader.GetString(2)}",
                ToInt32(reader.GetInt64(3)),
                ParseDate(reader.IsDBNull(4) ? null : reader.GetString(4))));
        }
        return results;
    }

    private static async Task<IReadOnlyList<CodexEntrySummary>> SearchCodexEntitiesAsync(
        SqliteConnection connection,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        List<CodexEntrySummary> results = [];
        using SqliteCommand command = connection.CreateCommand();
        string entityExpression = "COALESCE(e.canonical_name, e.description)";
        string scopeKindExpression = "CASE WHEN TRIM(COALESCE(m.area, '')) <> '' THEN 'area' ELSE 'room' END";
        string scopeValueExpression = "CASE WHEN TRIM(COALESCE(m.area, '')) <> '' THEN m.area ELSE e.room_id END";

        if (string.IsNullOrWhiteSpace(query))
        {
            command.CommandText = $"""
                WITH recent AS (
                    SELECT e.room_id, e.kind, e.description, e.canonical_name, e.observation_count,
                           e.last_seen_at, m.area, r.name AS room_name
                    FROM room_entities e
                    LEFT JOIN room_metadata m ON m.room_id = e.room_id
                    LEFT JOIN rooms r ON r.room_id = e.room_id
                    WHERE e.kind = $occupantKind
                    ORDER BY e.last_seen_at DESC
                    LIMIT $scanLimit
                )
                SELECT COALESCE(e.canonical_name, e.description) AS entity_name,
                       CASE WHEN TRIM(COALESCE(e.area, '')) <> '' THEN 'area' ELSE 'room' END AS scope_kind,
                       CASE WHEN TRIM(COALESCE(e.area, '')) <> '' THEN e.area ELSE e.room_id END AS scope_value,
                       MAX(e.kind), COUNT(DISTINCT e.room_id), SUM(e.observation_count), MAX(e.last_seen_at),
                       MAX(e.room_name)
                FROM recent e
                GROUP BY entity_name COLLATE NOCASE, scope_kind, scope_value COLLATE NOCASE
                ORDER BY MAX(e.last_seen_at) DESC, SUM(e.observation_count) DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$scanLimit", Math.Clamp(limit * 32, 256, 4096));
            command.Parameters.AddWithValue("$occupantKind", RoomEntityKind.Occupant.ToString());
            command.Parameters.AddWithValue("$limit", limit);
        }
        else
        {
            command.CommandText = $"""
                SELECT {entityExpression} AS entity_name,
                       {scopeKindExpression} AS scope_kind,
                       {scopeValueExpression} AS scope_value,
                       MAX(e.kind), COUNT(DISTINCT e.room_id), SUM(e.observation_count), MAX(e.last_seen_at),
                       MAX(r.name)
                FROM room_entities e
                LEFT JOIN room_metadata m ON m.room_id = e.room_id
                LEFT JOIN rooms r ON r.room_id = e.room_id
                WHERE e.kind = $occupantKind
                  AND (e.canonical_name LIKE $like COLLATE NOCASE
                    OR e.description LIKE $like COLLATE NOCASE
                    OR m.area LIKE $like COLLATE NOCASE
                    OR r.name LIKE $like COLLATE NOCASE)
                GROUP BY entity_name COLLATE NOCASE, scope_kind, scope_value COLLATE NOCASE
                ORDER BY MAX(e.last_seen_at) DESC, SUM(e.observation_count) DESC
                LIMIT $limit;
                """;
            AddSearchParameters(command, query, limit);
            command.Parameters.AddWithValue("$occupantKind", RoomEntityKind.Occupant.ToString());
        }

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string entityName = reader.GetString(0);
            string scopeKind = reader.GetString(1);
            string scopeValue = reader.GetString(2);
            int locations = ToInt32(reader.GetInt64(4));
            string? roomName = reader.IsDBNull(7) ? null : reader.GetString(7);
            string scopeLabel = scopeKind == "area"
                ? scopeValue
                : roomName ?? "Area not yet identified";
            results.Add(new CodexEntrySummary(
                CodexEntryKind.Entity,
                BuildCodexEntityKey(entityName, scopeKind, scopeValue),
                entityName,
                $"MOB · {scopeLabel} · {locations:N0} {(locations == 1 ? "location" : "locations")}",
                ToInt32(reader.GetInt64(5)),
                ParseDate(reader.IsDBNull(6) ? null : reader.GetString(6))));
        }
        return results;
    }

    private static async Task<IReadOnlyList<CodexEntrySummary>> SearchCodexItemsAsync(
        SqliteConnection connection,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        List<CodexEntrySummary> results = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_name, item_type, material, observation_count, last_seen_at
            FROM item_knowledge
            WHERE $query = ''
               OR item_name LIKE $like COLLATE NOCASE
               OR item_type LIKE $like COLLATE NOCASE
               OR material LIKE $like COLLATE NOCASE
               OR raw_text LIKE $like COLLATE NOCASE
            ORDER BY last_seen_at DESC, observation_count DESC
            LIMIT $limit;
            """;
        AddSearchParameters(command, query, limit);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            List<string> meta = ["Item"];
            if (!reader.IsDBNull(1)) meta.Add(reader.GetString(1));
            if (!reader.IsDBNull(2)) meta.Add(reader.GetString(2));
            results.Add(new CodexEntrySummary(
                CodexEntryKind.Item,
                reader.GetString(0),
                reader.GetString(0),
                string.Join(" · ", meta),
                ToInt32(reader.GetInt64(3)),
                ParseDate(reader.IsDBNull(4) ? null : reader.GetString(4))));
        }
        return results;
    }

    private static async Task<IReadOnlyList<CodexEntrySummary>> SearchCodexAbilitiesAsync(
        SqliteConnection connection,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        List<CodexEntrySummary> results = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ability_name, ability_kind, observation_count, last_seen_at
            FROM ability_help
            WHERE $query = ''
               OR ability_name LIKE $like COLLATE NOCASE
               OR ability_kind LIKE $like COLLATE NOCASE
               OR description LIKE $like COLLATE NOCASE
               OR syntax LIKE $like COLLATE NOCASE
            ORDER BY last_seen_at DESC, observation_count DESC
            LIMIT $limit;
            """;
        AddSearchParameters(command, query, limit);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new CodexEntrySummary(
                CodexEntryKind.Ability,
                reader.GetString(0),
                reader.GetString(0),
                reader.GetString(1),
                ToInt32(reader.GetInt64(2)),
                ParseDate(reader.IsDBNull(3) ? null : reader.GetString(3))));
        }
        return results;
    }

    private static async Task<IReadOnlyList<CodexEntrySummary>> SearchCodexCombatAsync(
        SqliteConnection connection,
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        List<CodexEntrySummary> results = [];
        using SqliteCommand command = connection.CreateCommand();
        if (string.IsNullOrWhiteSpace(query))
        {
            command.CommandText = """
                WITH recent AS (
                    SELECT observed_at, event_type, target
                    FROM combat_events
                    WHERE target IS NOT NULL
                    ORDER BY observed_at DESC
                    LIMIT $scanLimit
                )
                SELECT target, COUNT(*), SUM(CASE WHEN event_type = 'EnemyKilled' THEN 1 ELSE 0 END), MAX(observed_at)
                FROM recent
                GROUP BY target COLLATE NOCASE
                ORDER BY MAX(observed_at) DESC, COUNT(*) DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$scanLimit", Math.Clamp(limit * 64, 512, 8192));
            command.Parameters.AddWithValue("$limit", limit);
        }
        else
        {
            command.CommandText = """
                SELECT target, COUNT(*), SUM(CASE WHEN event_type = 'EnemyKilled' THEN 1 ELSE 0 END), MAX(observed_at)
                FROM combat_events
                WHERE target IS NOT NULL
                  AND target LIKE $like COLLATE NOCASE
                GROUP BY target COLLATE NOCASE
                ORDER BY MAX(observed_at) DESC, COUNT(*) DESC
                LIMIT $limit;
                """;
            AddSearchParameters(command, query, limit);
        }
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            int kills = reader.IsDBNull(2) ? 0 : ToInt32(reader.GetInt64(2));
            results.Add(new CodexEntrySummary(
                CodexEntryKind.Combat,
                reader.GetString(0),
                reader.GetString(0),
                $"Combat · {kills:N0} {(kills == 1 ? "kill" : "kills")}",
                ToInt32(reader.GetInt64(1)),
                ParseDate(reader.IsDBNull(3) ? null : reader.GetString(3))));
        }
        return results;
    }

    private static void AddSearchParameters(SqliteCommand command, string query, int limit)
    {
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$like", "%" + query + "%");
        command.Parameters.AddWithValue("$limit", limit);
    }

    private static async Task<CodexEntryDetail?> ReadCodexRoomAsync(
        SqliteConnection connection,
        string roomId,
        CancellationToken cancellationToken)
    {
        RoomKnowledge? room = await ReadRoomAsync(connection, roomId, cancellationToken).ConfigureAwait(false);
        if (room is null) return null;
        string? label = null;
        string? area = null;
        string? notes = null;
        bool avoid = false;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT label, area, notes, avoid FROM room_metadata WHERE room_id = $roomId;";
            command.Parameters.AddWithValue("$roomId", roomId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                label = reader.IsDBNull(0) ? null : reader.GetString(0);
                area = reader.IsDBNull(1) ? null : reader.GetString(1);
                notes = reader.IsDBNull(2) ? null : reader.GetString(2);
                avoid = !reader.IsDBNull(3) && reader.GetInt64(3) != 0;
            }
        }
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Visits"] = room.VisitCount.ToString("N0"),
            ["Known exits"] = room.Exits.Count.ToString("N0")
        };
        if (!string.IsNullOrWhiteSpace(area)) fields["Area"] = area;
        if (!string.IsNullOrWhiteSpace(room.Terrain)) fields["Terrain"] = room.Terrain;
        if (!string.IsNullOrWhiteSpace(room.Light)) fields["Light"] = room.Light;
        if (avoid) fields["Routing"] = "Avoid";
        if (!string.IsNullOrWhiteSpace(notes)) fields["Notes"] = notes;
        return new CodexEntryDetail(
            CodexEntryKind.Room,
            roomId,
            label ?? room.Name ?? "Known room",
            string.IsNullOrWhiteSpace(area) ? "Room" : $"Room · {area}",
            room.Description,
            fields,
            [new CodexLocation(roomId, room.Name, area, room.VisitCount, room.LastSeenAt)],
            null,
            room.VisitCount,
            room.LastSeenAt);
    }

    private static async Task<CodexEntryDetail?> ReadCodexEntityAsync(
        SqliteConnection connection,
        string entityKey,
        CancellationToken cancellationToken)
    {
        DecodeCodexEntityKey(entityKey, out string entityName, out string? scopeKind, out string? scopeValue);
        List<CodexLocation> locations = [];
        string? kind = null;
        string? description = null;
        int observations = 0;
        DateTimeOffset? lastSeen = null;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.room_id, r.name, m.area, e.kind, e.description, e.observation_count, e.last_seen_at
            FROM room_entities e
            LEFT JOIN rooms r ON r.room_id = e.room_id
            LEFT JOIN room_metadata m ON m.room_id = e.room_id
            WHERE e.kind = $occupantKind
              AND COALESCE(e.canonical_name, e.description) = $name COLLATE NOCASE
              AND ($scopeKind IS NULL
                   OR ($scopeKind = 'area' AND m.area = $scopeValue COLLATE NOCASE)
                   OR ($scopeKind = 'room' AND e.room_id = $scopeValue))
            ORDER BY e.last_seen_at DESC;
            """;
        command.Parameters.AddWithValue("$name", entityName);
        command.Parameters.AddWithValue("$occupantKind", RoomEntityKind.Occupant.ToString());
        command.Parameters.AddWithValue("$scopeKind", DbValue(scopeKind));
        command.Parameters.AddWithValue("$scopeValue", DbValue(scopeValue));
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                kind ??= reader.GetString(3);
                description ??= reader.GetString(4);
                int count = ToInt32(reader.GetInt64(5));
                DateTimeOffset? observed = ParseDate(reader.IsDBNull(6) ? null : reader.GetString(6));
                observations += count;
                if (observed is not null && (lastSeen is null || observed > lastSeen)) lastSeen = observed;
                locations.Add(new CodexLocation(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    count,
                    observed,
                    entityName,
                    entityKey));
            }
        }
        if (locations.Count == 0) return null;

        IReadOnlyList<CodexRelatedItem> relatedItems = await ReadMobRelatedItemsAsync(
            connection, entityName, scopeKind, scopeValue, cancellationToken).ConfigureAwait(false);
        string? area = scopeKind == "area" ? scopeValue : locations.Select(location => location.Area).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Type"] = kind ?? "MOB",
            ["Known locations"] = locations.Count.ToString("N0")
        };
        if (!string.IsNullOrWhiteSpace(area)) fields["Area"] = area;
        if (relatedItems.Count > 0) fields["Known loot"] = relatedItems.Count.ToString("N0");

        return new CodexEntryDetail(
            CodexEntryKind.Entity,
            entityKey,
            entityName,
            string.IsNullOrWhiteSpace(area) ? "MOB" : $"MOB · {area}",
            description,
            fields,
            locations,
            null,
            observations,
            lastSeen)
        {
            RelatedItems = relatedItems
        };
    }

    private static async Task<IReadOnlyList<CodexRelatedItem>> ReadMobRelatedItemsAsync(
        SqliteConnection connection,
        string mobName,
        string? scopeKind,
        string? scopeValue,
        CancellationToken cancellationToken)
    {
        List<CodexRelatedItem> items = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.item_name, SUM(s.observation_count), MAX(s.last_seen_at)
            FROM mob_item_sources s
            LEFT JOIN room_metadata m ON m.room_id = s.room_id
            WHERE s.mob_name = $mobName COLLATE NOCASE
              AND ($scopeKind IS NULL
                   OR ($scopeKind = 'area' AND m.area = $scopeValue COLLATE NOCASE)
                   OR ($scopeKind = 'room' AND s.room_id = $scopeValue))
            GROUP BY s.item_name COLLATE NOCASE
            ORDER BY MAX(s.last_seen_at) DESC, SUM(s.observation_count) DESC
            LIMIT 64;
            """;
        command.Parameters.AddWithValue("$mobName", mobName);
        command.Parameters.AddWithValue("$scopeKind", DbValue(scopeKind));
        command.Parameters.AddWithValue("$scopeValue", DbValue(scopeValue));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new CodexRelatedItem(
                reader.GetString(0),
                ToInt32(reader.GetInt64(1)),
                ParseDate(reader.IsDBNull(2) ? null : reader.GetString(2))));
        }
        return items;
    }

    private static async Task<CodexEntryDetail?> ReadCodexItemAsync(
        SqliteConnection connection,
        string itemName,
        CancellationToken cancellationToken)
    {
        string? resolvedName = null;
        string? itemType = null;
        string? rawText = null;
        int observations = 0;
        DateTimeOffset? lastSeen = null;
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT item_name, flags_json, weight, wear_locations_json, level, material,
                       item_type, weapon_type, weapon_flags_json, damage_type, damage_dice,
                       damage_average, extra_fields_json, raw_text, observation_count, last_seen_at
                FROM item_knowledge WHERE item_name = $name COLLATE NOCASE LIMIT 1;
                """;
            command.Parameters.AddWithValue("$name", itemName);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                resolvedName = reader.GetString(0);
                fields = DeserializeStringDictionary(reader.GetString(12))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                void Add(string label, string? value)
                {
                    if (!string.IsNullOrWhiteSpace(value)) fields[label] = value;
                }
                Add("Type", reader.IsDBNull(6) ? null : reader.GetString(6));
                Add("Material", reader.IsDBNull(5) ? null : reader.GetString(5));
                Add("Weapon", reader.IsDBNull(7) ? null : reader.GetString(7));
                Add("Damage type", reader.IsDBNull(9) ? null : reader.GetString(9));
                Add("Damage dice", reader.IsDBNull(10) ? null : reader.GetString(10));
                if (!reader.IsDBNull(4)) fields["Level"] = reader.GetInt64(4).ToString();
                if (!reader.IsDBNull(2)) fields["Weight"] = reader.GetDouble(2).ToString("0.##");
                if (!reader.IsDBNull(11)) fields["Average damage"] = reader.GetDouble(11).ToString("0.##");
                Add("Flags", string.Join(", ", DeserializeStrings(reader.GetString(1))));
                Add("Wear", string.Join(", ", DeserializeStrings(reader.GetString(3))));
                Add("Weapon flags", string.Join(", ", DeserializeStrings(reader.GetString(8))));
                itemType = reader.IsDBNull(6) ? null : reader.GetString(6);
                rawText = reader.GetString(13);
                observations = ToInt32(reader.GetInt64(14));
                lastSeen = ParseDate(reader.IsDBNull(15) ? null : reader.GetString(15));
            }
        }

        IReadOnlyList<CodexLocation> mobSources = await ReadItemMobSourcesAsync(
            connection, itemName, cancellationToken).ConfigureAwait(false);
        if (resolvedName is null && mobSources.Count == 0) return null;
        resolvedName ??= itemName;
        if (mobSources.Count > 0) fields["Known MOB sources"] = mobSources
            .Select(source => source.EntityKey ?? source.EntityName ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ToString("N0");

        return new CodexEntryDetail(
            CodexEntryKind.Item,
            resolvedName,
            resolvedName,
            string.IsNullOrWhiteSpace(itemType) ? "Item" : itemType,
            null,
            fields,
            mobSources,
            rawText,
            observations,
            lastSeen);
    }

    private static async Task<IReadOnlyList<CodexLocation>> ReadItemMobSourcesAsync(
        SqliteConnection connection,
        string itemName,
        CancellationToken cancellationToken)
    {
        List<CodexLocation> locations = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            WITH source_rows AS (
                SELECT s.mob_name, s.room_id, r.name AS room_name, m.area, s.observation_count, s.last_seen_at,
                       CASE WHEN TRIM(COALESCE(m.area, '')) <> '' THEN 'area' ELSE 'room' END AS scope_kind,
                       CASE WHEN TRIM(COALESCE(m.area, '')) <> '' THEN m.area ELSE s.room_id END AS scope_value
                FROM mob_item_sources s
                LEFT JOIN rooms r ON r.room_id = s.room_id
                LEFT JOIN room_metadata m ON m.room_id = s.room_id
                WHERE s.item_name = $itemName COLLATE NOCASE
            ),
            ranked AS (
                SELECT *,
                       ROW_NUMBER() OVER (
                           PARTITION BY mob_name COLLATE NOCASE, scope_kind, scope_value COLLATE NOCASE
                           ORDER BY last_seen_at DESC) AS row_rank,
                       SUM(observation_count) OVER (
                           PARTITION BY mob_name COLLATE NOCASE, scope_kind, scope_value COLLATE NOCASE) AS total_observations,
                       MAX(last_seen_at) OVER (
                           PARTITION BY mob_name COLLATE NOCASE, scope_kind, scope_value COLLATE NOCASE) AS aggregate_last_seen
                FROM source_rows
            )
            SELECT mob_name, room_id, room_name, area, total_observations, aggregate_last_seen, scope_kind, scope_value
            FROM ranked
            WHERE row_rank = 1
            ORDER BY aggregate_last_seen DESC, total_observations DESC
            LIMIT 64;
            """;
        command.Parameters.AddWithValue("$itemName", itemName);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            string mobName = reader.GetString(0);
            string roomId = reader.GetString(1);
            string? area = reader.IsDBNull(3) ? null : reader.GetString(3);
            string entityKey = BuildCodexEntityKey(mobName, reader.GetString(6), reader.GetString(7));
            locations.Add(new CodexLocation(
                roomId,
                reader.IsDBNull(2) ? null : reader.GetString(2),
                area,
                ToInt32(reader.GetInt64(4)),
                ParseDate(reader.IsDBNull(5) ? null : reader.GetString(5)),
                mobName,
                entityKey));
        }
        return locations;
    }

    private static async Task<CodexEntryDetail?> ReadCodexAbilityAsync(
        SqliteConnection connection,
        string abilityName,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ability_name, ability_kind, activation_lag, activation_mana_cost,
                   syntax, description, fields_json, raw_text, observation_count, last_seen_at
            FROM ability_help
            WHERE ability_name = $name COLLATE NOCASE
            ORDER BY last_seen_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", abilityName);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        Dictionary<string, string> fields = DeserializeStringDictionary(reader.GetString(6))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (!reader.IsDBNull(2)) fields["Activation lag"] = $"{reader.GetDouble(2):0.##} rounds";
        if (!reader.IsDBNull(3)) fields["Mana cost"] = reader.GetInt64(3).ToString();
        if (!reader.IsDBNull(4)) fields["Syntax"] = reader.GetString(4);
        return new CodexEntryDetail(
            CodexEntryKind.Ability,
            reader.GetString(0),
            reader.GetString(0),
            reader.GetString(1),
            NormalizeSemanticText(reader.GetString(5)),
            fields,
            Array.Empty<CodexLocation>(),
            reader.GetString(7),
            ToInt32(reader.GetInt64(8)),
            ParseDate(reader.IsDBNull(9) ? null : reader.GetString(9)));
    }

    private static async Task<CodexEntryDetail?> ReadCodexCombatAsync(
        SqliteConnection connection,
        string target,
        CancellationToken cancellationToken)
    {
        CombatKnowledge? combat = await ReadCombatKnowledgeAsync(connection, target, cancellationToken).ConfigureAwait(false);
        if (combat is null) return null;
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Encounter events"] = combat.EncounterEvents.ToString("N0"),
            ["Kills"] = combat.KillCount.ToString("N0")
        };
        List<CodexLocation> locations = [];
        foreach (IGrouping<string, CombatMemoryEvent> group in combat.RecentEvents
                     .Where(item => !string.IsNullOrWhiteSpace(item.RoomId))
                     .GroupBy(item => item.RoomId!, StringComparer.Ordinal))
        {
            locations.Add(new CodexLocation(
                group.Key,
                null,
                null,
                group.Count(),
                group.Max(item => item.ObservedAt)));
        }
        return new CodexEntryDetail(
            CodexEntryKind.Combat,
            target,
            target,
            "Combat history",
            null,
            fields,
            locations,
            null,
            combat.EncounterEvents,
            combat.LastSeenAt);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath) ?? ".");
        await using SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await InitializeAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Connection-local pragmas belong on every connection. Schema creation and index
        // migration do not: rerunning the complete DDL batch for every Codex/mapper read
        // creates needless lock contention and makes browse operations scale poorly.
        using (SqliteCommand connectionCommand = connection.CreateCommand())
        {
            connectionCommand.CommandText = """
                PRAGMA synchronous=NORMAL;
                PRAGMA foreign_keys=ON;
                PRAGMA busy_timeout=2000;
                """;
            await connectionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Volatile.Read(ref _schemaInitialized) != 0) return;

        await _schemaInitializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _schemaInitialized) != 0) return;

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS mud_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                session_id TEXT,
                event_sequence INTEGER NOT NULL,
                event_id TEXT NOT NULL UNIQUE,
                observed_at TEXT NOT NULL,
                source TEXT NOT NULL,
                event_type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                state_version INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_mud_events_type_time ON mud_events(event_type, observed_at);
            CREATE INDEX IF NOT EXISTS ix_mud_events_session_sequence ON mud_events(session_id, event_sequence);

            CREATE TABLE IF NOT EXISTS sessions (
                session_id TEXT PRIMARY KEY,
                started_at TEXT NOT NULL,
                ended_at TEXT,
                host TEXT,
                port INTEGER,
                character_name TEXT
            );

            CREATE TABLE IF NOT EXISTS rooms (
                room_id TEXT PRIMARY KEY,
                name TEXT,
                description_fingerprint TEXT,
                description TEXT,
                terrain TEXT,
                light TEXT,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                visit_count INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS ix_rooms_name ON rooms(name);
            CREATE INDEX IF NOT EXISTS ix_rooms_last_seen ON rooms(last_seen_at DESC, visit_count DESC);

            CREATE TABLE IF NOT EXISTS room_metadata (
                room_id TEXT PRIMARY KEY,
                label TEXT,
                area TEXT,
                notes TEXT,
                avoid INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_room_metadata_area ON room_metadata(area);

            CREATE TABLE IF NOT EXISTS room_exits (
                from_room_id TEXT NOT NULL,
                direction TEXT NOT NULL,
                to_room_id TEXT,
                door_state TEXT,
                traversability TEXT,
                block_reason TEXT,
                last_seen_at TEXT NOT NULL,
                PRIMARY KEY(from_room_id, direction)
            );

            CREATE INDEX IF NOT EXISTS ix_room_exits_to ON room_exits(to_room_id);

            CREATE TABLE IF NOT EXISTS room_transitions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                from_room_id TEXT NOT NULL,
                to_room_id TEXT NOT NULL,
                transition_kind TEXT NOT NULL,
                direction TEXT,
                observed_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_room_transitions_time ON room_transitions(observed_at DESC);
            CREATE INDEX IF NOT EXISTS ix_room_transitions_rooms ON room_transitions(from_room_id, to_room_id);

            CREATE TABLE IF NOT EXISTS room_entities (
                room_id TEXT NOT NULL,
                entity_key TEXT NOT NULL,
                kind TEXT NOT NULL,
                description TEXT NOT NULL,
                canonical_name TEXT,
                traits INTEGER NOT NULL,
                target_keywords_json TEXT,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                observation_count INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY(room_id, entity_key)
            );
            CREATE INDEX IF NOT EXISTS ix_room_entities_room_kind ON room_entities(room_id, kind);
            CREATE INDEX IF NOT EXISTS ix_room_entities_last_seen ON room_entities(last_seen_at DESC);
            CREATE INDEX IF NOT EXISTS ix_room_entities_canonical_name ON room_entities(canonical_name COLLATE NOCASE, last_seen_at DESC);
            CREATE INDEX IF NOT EXISTS ix_room_entities_display_name_time
                ON room_entities(COALESCE(canonical_name, description) COLLATE NOCASE, last_seen_at DESC);

            CREATE TABLE IF NOT EXISTS character_observations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                observed_at TEXT NOT NULL,
                state_version INTEGER NOT NULL,
                character_name TEXT,
                hp INTEGER,
                max_hp INTEGER,
                mana INTEGER,
                max_mana INTEGER,
                movement INTEGER,
                max_movement INTEGER,
                position TEXT,
                level INTEGER,
                experience INTEGER,
                experience_to_level INTEGER,
                room_id TEXT,
                combat_active INTEGER NOT NULL,
                target TEXT,
                target_condition TEXT
            );
            CREATE INDEX IF NOT EXISTS ix_character_observations_time ON character_observations(observed_at);

            CREATE TABLE IF NOT EXISTS abilities (
                character_key TEXT NOT NULL,
                ability_kind TEXT NOT NULL,
                name TEXT NOT NULL,
                required_level INTEGER NOT NULL,
                availability TEXT NOT NULL,
                proficiency INTEGER,
                domain TEXT NOT NULL,
                is_fresh INTEGER NOT NULL,
                last_seen_at TEXT NOT NULL,
                PRIMARY KEY(character_key, ability_kind, name)
            );

            CREATE TABLE IF NOT EXISTS equipment (
                character_key TEXT NOT NULL,
                slot TEXT NOT NULL,
                item TEXT,
                last_seen_at TEXT NOT NULL,
                PRIMARY KEY(character_key, slot)
            );

            CREATE TABLE IF NOT EXISTS item_knowledge (
                item_name TEXT PRIMARY KEY COLLATE NOCASE,
                flags_json TEXT NOT NULL,
                weight REAL,
                wear_locations_json TEXT NOT NULL,
                level INTEGER,
                material TEXT,
                item_type TEXT,
                weapon_type TEXT,
                weapon_flags_json TEXT NOT NULL,
                damage_type TEXT,
                damage_dice TEXT,
                damage_average REAL,
                extra_fields_json TEXT NOT NULL,
                raw_text TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                observation_count INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS ix_item_knowledge_last_seen ON item_knowledge(last_seen_at DESC);

            CREATE TABLE IF NOT EXISTS mob_item_sources (
                item_name TEXT NOT NULL COLLATE NOCASE,
                mob_name TEXT NOT NULL COLLATE NOCASE,
                room_id TEXT NOT NULL,
                source_kind TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                observation_count INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY(item_name, mob_name, room_id, source_kind)
            );
            CREATE INDEX IF NOT EXISTS ix_mob_item_sources_item
                ON mob_item_sources(item_name COLLATE NOCASE, last_seen_at DESC);
            CREATE INDEX IF NOT EXISTS ix_mob_item_sources_mob
                ON mob_item_sources(mob_name COLLATE NOCASE, room_id, last_seen_at DESC);

            CREATE TABLE IF NOT EXISTS ability_help (
                ability_name TEXT NOT NULL COLLATE NOCASE,
                ability_kind TEXT NOT NULL,
                activation_lag REAL,
                activation_mana_cost INTEGER,
                syntax TEXT,
                description TEXT NOT NULL,
                fields_json TEXT NOT NULL,
                raw_text TEXT NOT NULL,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                observation_count INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY(ability_name, ability_kind)
            );
            CREATE INDEX IF NOT EXISTS ix_ability_help_last_seen ON ability_help(last_seen_at DESC);

            CREATE TABLE IF NOT EXISTS combat_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                observed_at TEXT NOT NULL,
                event_type TEXT NOT NULL,
                target TEXT,
                room_id TEXT,
                state_version INTEGER NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_combat_target_time ON combat_events(target, observed_at);
            CREATE INDEX IF NOT EXISTS ix_combat_time ON combat_events(observed_at DESC);
            CREATE INDEX IF NOT EXISTS ix_combat_target_nocase_time
                ON combat_events(target COLLATE NOCASE, observed_at DESC, event_type);

            CREATE TABLE IF NOT EXISTS communications (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                observed_at TEXT NOT NULL,
                channel TEXT NOT NULL,
                speaker TEXT,
                message TEXT NOT NULL,
                room_id TEXT
            );

            CREATE TABLE IF NOT EXISTS jev_decisions (
                decision_id TEXT PRIMARY KEY,
                observed_at TEXT NOT NULL,
                state_version INTEGER NOT NULL,
                domain TEXT NOT NULL,
                selected_action TEXT NOT NULL,
                confidence REAL NOT NULL,
                authority TEXT NOT NULL,
                outcome TEXT NOT NULL,
                model TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS command_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                entered_at TEXT NOT NULL,
                command TEXT NOT NULL,
                sent_to_mud INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_command_history_time ON command_history(entered_at DESC);

            CREATE TABLE IF NOT EXISTS action_history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                executed_at TEXT NOT NULL,
                action_id TEXT NOT NULL,
                command TEXT NOT NULL,
                source TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_action_history_time ON action_history(executed_at DESC);
            """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await RepairLegacyRoomEntityKindsAsync(connection, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _schemaInitialized, 1);
        }
        finally
        {
            _schemaInitializationGate.Release();
        }
    }

    private static async Task RepairLegacyRoomEntityKindsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        List<(string RoomId, string EntityKey, string Description)> fixtures = [];
        using (SqliteCommand select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT room_id, entity_key, description
                FROM room_entities
                WHERE kind = $occupantKind
                  AND (description LIKE '%fountain%' COLLATE NOCASE
                    OR description LIKE '% board%' COLLATE NOCASE
                    OR description LIKE '% sign%' COLLATE NOCASE
                    OR description LIKE '% plaque%' COLLATE NOCASE
                    OR description LIKE '% note%' COLLATE NOCASE
                    OR description LIKE '% bin%' COLLATE NOCASE
                    OR description LIKE '% volume%' COLLATE NOCASE);
                """;
            select.Parameters.AddWithValue("$occupantKind", RoomEntityKind.Occupant.ToString());
            await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string description = reader.GetString(2);
                if (AvendarRoomEntityClassifier.IsFixtureSubject(description))
                {
                    fixtures.Add((reader.GetString(0), reader.GetString(1), description));
                }
            }
        }

        foreach ((string roomId, string entityKey, string description) in fixtures)
        {
            RoomEntityTraits traits = RoomEntityTraits.Fixture | RoomEntityTraits.Examinable;
            if (description.Contains("fountain", StringComparison.OrdinalIgnoreCase)) traits |= RoomEntityTraits.Drinkable;
            if (description.Contains("bin", StringComparison.OrdinalIgnoreCase)) traits |= RoomEntityTraits.Container;
            if (description.Contains("sign", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("board", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("plaque", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("note", StringComparison.OrdinalIgnoreCase) ||
                description.Contains("volume", StringComparison.OrdinalIgnoreCase))
            {
                traits |= RoomEntityTraits.Readable;
            }

            using SqliteCommand update = connection.CreateCommand();
            update.CommandText = """
                UPDATE room_entities
                SET kind = $fixtureKind, traits = $traits
                WHERE room_id = $roomId AND entity_key = $entityKey AND kind = $occupantKind;
                """;
            update.Parameters.AddWithValue("$fixtureKind", RoomEntityKind.Fixture.ToString());
            update.Parameters.AddWithValue("$traits", (long)traits);
            update.Parameters.AddWithValue("$roomId", roomId);
            update.Parameters.AddWithValue("$entityKey", entityKey);
            update.Parameters.AddWithValue("$occupantKind", RoomEntityKind.Occupant.ToString());
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PersistEventAsync(
        SqliteConnection connection,
        EventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        StateSnapshot state = _state.Current;
        string payloadJson = SerializeEventPayload(envelope.Payload);

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT OR IGNORE INTO mud_events(session_id, event_sequence, event_id, observed_at, source, event_type, payload_json, state_version)
                VALUES($sessionId, $sequence, $eventId, $observedAt, $source, $eventType, $payload, $stateVersion);
                """;
            command.Parameters.AddWithValue("$sessionId", DbValue(_sessionId));
            command.Parameters.AddWithValue("$sequence", envelope.Sequence);
            command.Parameters.AddWithValue("$eventId", envelope.EventId.ToString("D"));
            command.Parameters.AddWithValue("$observedAt", envelope.Timestamp.ToString("O"));
            command.Parameters.AddWithValue("$source", envelope.Source);
            command.Parameters.AddWithValue("$eventType", envelope.Payload.GetType().Name);
            command.Parameters.AddWithValue("$payload", payloadJson);
            command.Parameters.AddWithValue("$stateVersion", state.Version);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        switch (envelope.Payload)
        {
            case ConnectionStateChanged connectionState:
                await PersistConnectionAsync(connection, connectionState, envelope.Timestamp, cancellationToken).ConfigureAwait(false);
                if (connectionState.Status != ConnectionStatus.Connected)
                    _pendingTraversals.Clear();
                break;
            case CharacterPromptObserved:
            case CharacterPromptSnapshotObserved:
            case CharacterScoreObserved:
            case CharacterVitalsChanged:
                await PersistCharacterObservationAsync(connection, state, envelope.Timestamp, cancellationToken).ConfigureAwait(false);
                break;
            case SkillsSnapshotObserved skills:
                await PersistAbilitiesAsync(connection, state, "skill", skills.Skills, null, envelope.Timestamp, cancellationToken).ConfigureAwait(false);
                break;
            case SpellsSnapshotObserved spells:
                await PersistAbilitiesAsync(connection, state, "spell", null, spells.Spells, envelope.Timestamp, cancellationToken).ConfigureAwait(false);
                break;
            case EquipmentSnapshotObserved equipment:
                await PersistEquipmentAsync(connection, state, equipment.Slots, envelope.Timestamp, cancellationToken).ConfigureAwait(false);
                break;
            case EquipmentChanged equipment:
                await PersistEquipmentChangeAsync(connection, state, equipment, envelope.Timestamp, cancellationToken).ConfigureAwait(false);
                break;
            case ItemIdentified item:
                await PersistItemKnowledgeAsync(connection, item.Item, envelope.Timestamp, cancellationToken).ConfigureAwait(false);
                break;
            case ItemAcquired acquired:
                await PersistItemAcquisitionAsync(
                    connection, acquired, _currentRoomId ?? state.Room.Id, envelope.Timestamp, cancellationToken).ConfigureAwait(false);
                break;
            case AbilityHelpObserved help:
                await PersistAbilityHelpAsync(connection, help.Help, envelope.Timestamp, cancellationToken).ConfigureAwait(false);
                break;
            case NavigationAttempted navigation when MapperRecordingEnabled():
                PruneExpiredTraversals(envelope.Timestamp);
                _pendingTraversals.Enqueue(new PendingTraversal(
                    navigation.Direction,
                    _currentRoomId ?? state.Room.Id,
                    envelope.Timestamp,
                    navigation.ActionId));
                break;
            case NavigationResponseCompleted response when MapperRecordingEnabled() && !response.RoomObserved:
                TakePendingTraversalByAction(response.ActionId, response.Direction, envelope.Timestamp);
                break;
            case NavigationFailed failed when MapperRecordingEnabled():
            {
                string? failedRoomId = _currentRoomId ?? state.Room.Id;
                PendingTraversal? failedTraversal = TakePendingTraversal(
                    failed.Direction,
                    failedRoomId,
                    envelope.Timestamp);
                string? failedDirection = failedTraversal?.Direction ?? failed.Direction;
                if (MapperPersistenceEnabled() && !string.IsNullOrWhiteSpace(failedRoomId) && !string.IsNullOrWhiteSpace(failedDirection))
                {
                    ExitDoorState doorState = failed.Reason.Contains("locked", StringComparison.OrdinalIgnoreCase)
                        ? ExitDoorState.Locked
                        : failed.Reason.Contains("closed", StringComparison.OrdinalIgnoreCase)
                            ? ExitDoorState.Closed
                            : ExitDoorState.Unknown;
                    await PersistExitStateAsync(
                        connection,
                        failedRoomId!,
                        new RoomExitStateChanged(failedDirection!, doorState, ExitTraversability.Blocked, failed.Reason),
                        envelope.Timestamp,
                        cancellationToken).ConfigureAwait(false);
                    NotifyMapperKnowledgeChanged();
                }
                break;
            }
            case RoomObservationObserved room when MapperPersistenceEnabled():
                await PersistRoomAsync(connection, room, state, envelope.Timestamp, cancellationToken).ConfigureAwait(false);
                NotifyMapperKnowledgeChanged();
                break;
            case AreaObserved area when MapperPersistenceEnabled() && !string.IsNullOrWhiteSpace(_currentRoomId):
                await PersistExplicitAreaAsync(connection, _currentRoomId!, area.Area, cancellationToken).ConfigureAwait(false);
                NotifyMapperKnowledgeChanged();
                break;
            case RoomExitStateChanged exit when MapperPersistenceEnabled() && !string.IsNullOrWhiteSpace(_currentRoomId):
                await PersistExitStateAsync(connection, _currentRoomId!, exit, envelope.Timestamp, cancellationToken).ConfigureAwait(false);
                NotifyMapperKnowledgeChanged();
                break;
            case CombatStateChanged or CombatDamageObserved or CombatAttackObserved or CombatTargetConditionObserved or EnemyKilled:
                await PersistCombatEventAsync(connection, envelope, state, payloadJson, cancellationToken).ConfigureAwait(false);
                break;
            case CommunicationObserved communication:
                await PersistCommunicationAsync(connection, communication, envelope.Timestamp, state.Room.Id, cancellationToken).ConfigureAwait(false);
                break;
            case JevDecisionProduced produced:
                await PersistJevDecisionAsync(connection, produced.Decision, envelope.Timestamp, payloadJson, cancellationToken).ConfigureAwait(false);
                break;
            case ActionExecuted action when !action.Sensitive:
                await PersistExecutedCommandAsync(connection, action, envelope.Timestamp, cancellationToken).ConfigureAwait(false);
                break;
        }

        if (_sessionId is not null && !string.IsNullOrWhiteSpace(state.Character.Profile.Name))
        {
            using SqliteCommand update = connection.CreateCommand();
            update.CommandText = "UPDATE sessions SET character_name = COALESCE(character_name, $name) WHERE session_id = $sessionId;";
            update.Parameters.AddWithValue("$name", state.Character.Profile.Name!);
            update.Parameters.AddWithValue("$sessionId", _sessionId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private PendingTraversal? TakePendingTraversalByAction(
        Guid actionId,
        string direction,
        DateTimeOffset observedAt)
    {
        PruneExpiredTraversals(observedAt);
        if (_pendingTraversals.Count == 0) return null;

        int count = _pendingTraversals.Count;
        PendingTraversal? removed = null;
        for (int index = 0; index < count; index++)
        {
            PendingTraversal pending = _pendingTraversals.Dequeue();
            bool actionMatches = pending.ActionId == actionId;
            bool legacyMatch = pending.ActionId is null &&
                               pending.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase);
            if (removed is null && (actionMatches || legacyMatch))
            {
                removed = pending;
                continue;
            }
            _pendingTraversals.Enqueue(pending);
        }
        return removed;
    }

    private PendingTraversal? TakePendingTraversal(
        string? direction,
        string? sourceRoomId,
        DateTimeOffset observedAt)
    {
        PruneExpiredTraversals(observedAt);
        if (_pendingTraversals.Count == 0) return null;

        int count = _pendingTraversals.Count;
        PendingTraversal? removed = null;
        for (int index = 0; index < count; index++)
        {
            PendingTraversal pending = _pendingTraversals.Dequeue();
            bool sourceMatches = string.IsNullOrWhiteSpace(sourceRoomId) ||
                                 string.IsNullOrWhiteSpace(pending.SourceRoomId) ||
                                 string.Equals(pending.SourceRoomId, sourceRoomId, StringComparison.Ordinal);
            bool directionMatches = string.IsNullOrWhiteSpace(direction) ||
                                    pending.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase);
            if (removed is null && sourceMatches && directionMatches)
            {
                removed = pending;
                continue;
            }
            _pendingTraversals.Enqueue(pending);
        }
        return removed;
    }

    private PendingTraversal? TakeTraversalFromRoom(string sourceRoomId, DateTimeOffset observedAt)
    {
        PruneExpiredTraversals(observedAt);
        if (_pendingTraversals.Count == 0) return null;

        int count = _pendingTraversals.Count;
        PendingTraversal? removed = null;
        for (int index = 0; index < count; index++)
        {
            PendingTraversal pending = _pendingTraversals.Dequeue();
            if (removed is null && string.Equals(pending.SourceRoomId, sourceRoomId, StringComparison.Ordinal))
            {
                removed = pending;
                continue;
            }

            // Once the character has left a source room, an old traversal attempt from some other
            // source can never explain a future room change. Discard it rather than manufacturing
            // an exit later.
            if (!string.IsNullOrWhiteSpace(pending.SourceRoomId) &&
                !string.Equals(pending.SourceRoomId, sourceRoomId, StringComparison.Ordinal))
                continue;

            _pendingTraversals.Enqueue(pending);
        }
        return removed;
    }

    private void RebaseQueuedTraversalBatch(string fromRoomId, string toRoomId)
    {
        if (_pendingTraversals.Count == 0) return;
        int count = _pendingTraversals.Count;
        for (int index = 0; index < count; index++)
        {
            PendingTraversal pending = _pendingTraversals.Dequeue();
            _pendingTraversals.Enqueue(string.Equals(pending.SourceRoomId, fromRoomId, StringComparison.Ordinal)
                ? pending with { SourceRoomId = toRoomId }
                : pending);
        }
    }

    private void PruneExpiredTraversals(DateTimeOffset observedAt)
    {
        if (_pendingTraversals.Count == 0) return;
        int count = _pendingTraversals.Count;
        for (int index = 0; index < count; index++)
        {
            PendingTraversal pending = _pendingTraversals.Dequeue();
            TimeSpan age = observedAt - pending.AttemptedAt;
            if (age >= TimeSpan.Zero && age <= PendingTraversalLifetime)
                _pendingTraversals.Enqueue(pending);
        }
    }

    private async Task PersistConnectionAsync(
        SqliteConnection connection,
        ConnectionStateChanged change,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        if (change.Status == ConnectionStatus.Connected)
        {
            _sessionId = Guid.NewGuid().ToString("N");
            _currentRoomId = null;
            Volatile.Write(ref _currentRoomKnowledge, null);
            _previousRoomId = null;
            _pendingTraversals.Clear();
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO sessions(session_id, started_at, host, port)
                VALUES($id, $startedAt, $host, $port);
                """;
            insert.Parameters.AddWithValue("$id", _sessionId);
            insert.Parameters.AddWithValue("$startedAt", observedAt.ToString("O"));
            insert.Parameters.AddWithValue("$host", (object?)change.Host ?? DBNull.Value);
            insert.Parameters.AddWithValue("$port", (object?)change.Port ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (change.Status == ConnectionStatus.Disconnected)
        {
            _pendingTraversals.Clear();
            if (_sessionId is not null)
            {
                using SqliteCommand update = connection.CreateCommand();
                update.CommandText = "UPDATE sessions SET ended_at = $endedAt WHERE session_id = $id;";
                update.Parameters.AddWithValue("$endedAt", observedAt.ToString("O"));
                update.Parameters.AddWithValue("$id", _sessionId);
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                _sessionId = null;
            }
        }
    }

    private static async Task PersistCharacterObservationAsync(
        SqliteConnection connection,
        StateSnapshot state,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO character_observations(
                observed_at, state_version, character_name, hp, max_hp, mana, max_mana,
                movement, max_movement, position, level, experience, experience_to_level,
                room_id, combat_active, target, target_condition)
            VALUES(
                $observedAt, $stateVersion, $characterName, $hp, $maxHp, $mana, $maxMana,
                $movement, $maxMovement, $position, $level, $experience, $experienceToLevel,
                $roomId, $combatActive, $target, $targetCondition);
            """;
        command.Parameters.AddWithValue("$observedAt", observedAt.ToString("O"));
        command.Parameters.AddWithValue("$stateVersion", state.Version);
        command.Parameters.AddWithValue("$characterName", DbValue(state.Character.Profile.Name));
        command.Parameters.AddWithValue("$hp", DbValue(state.Character.HitPoints.Current));
        command.Parameters.AddWithValue("$maxHp", DbValue(state.Character.HitPoints.Maximum));
        command.Parameters.AddWithValue("$mana", DbValue(state.Character.Mana.Current));
        command.Parameters.AddWithValue("$maxMana", DbValue(state.Character.Mana.Maximum));
        command.Parameters.AddWithValue("$movement", DbValue(state.Character.Movement.Current));
        command.Parameters.AddWithValue("$maxMovement", DbValue(state.Character.Movement.Maximum));
        command.Parameters.AddWithValue("$position", DbValue(state.Character.Position));
        command.Parameters.AddWithValue("$level", DbValue(state.Character.Profile.Level));
        command.Parameters.AddWithValue("$experience", DbValue(state.Character.Experience));
        command.Parameters.AddWithValue("$experienceToLevel", DbValue(state.Character.ExperienceToLevel));
        command.Parameters.AddWithValue("$roomId", DbValue(state.Room.Id));
        command.Parameters.AddWithValue("$combatActive", state.Combat.Active ? 1 : 0);
        command.Parameters.AddWithValue("$target", DbValue(state.Combat.TargetName ?? state.Combat.TargetId));
        command.Parameters.AddWithValue("$targetCondition", DbValue(state.Combat.TargetCondition));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistAbilitiesAsync(
        SqliteConnection connection,
        StateSnapshot state,
        string kind,
        IReadOnlyList<SkillState>? skills,
        IReadOnlyList<SpellState>? spells,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        string characterKey = CharacterKey(state);
        if (skills is not null)
        {
            foreach (SkillState ability in skills)
            {
                await UpsertAbilityAsync(connection, characterKey, kind, ability.Name, ability.RequiredLevel,
                    ability.Availability.ToString(), ability.ProficiencyPercent, ability.Domain.ToString(), ability.IsFresh,
                    observedAt, cancellationToken).ConfigureAwait(false);
            }
        }
        if (spells is not null)
        {
            foreach (SpellState ability in spells)
            {
                await UpsertAbilityAsync(connection, characterKey, kind, ability.Name, ability.RequiredLevel,
                    ability.Availability.ToString(), ability.ProficiencyPercent, ability.Domain.ToString(), ability.IsFresh,
                    observedAt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task UpsertAbilityAsync(
        SqliteConnection connection,
        string characterKey,
        string kind,
        string name,
        int requiredLevel,
        string availability,
        int? proficiency,
        string domain,
        bool isFresh,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO abilities(character_key, ability_kind, name, required_level, availability, proficiency, domain, is_fresh, last_seen_at)
            VALUES($characterKey, $kind, $name, $requiredLevel, $availability, $proficiency, $domain, $isFresh, $lastSeenAt)
            ON CONFLICT(character_key, ability_kind, name) DO UPDATE SET
                required_level = excluded.required_level,
                availability = excluded.availability,
                proficiency = excluded.proficiency,
                domain = excluded.domain,
                is_fresh = excluded.is_fresh,
                last_seen_at = excluded.last_seen_at;
            """;
        command.Parameters.AddWithValue("$characterKey", characterKey);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$requiredLevel", requiredLevel);
        command.Parameters.AddWithValue("$availability", availability);
        command.Parameters.AddWithValue("$proficiency", DbValue(proficiency));
        command.Parameters.AddWithValue("$domain", domain);
        command.Parameters.AddWithValue("$isFresh", isFresh ? 1 : 0);
        command.Parameters.AddWithValue("$lastSeenAt", observedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistEquipmentAsync(
        SqliteConnection connection,
        StateSnapshot state,
        IReadOnlyList<EquipmentSlotState> slots,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        foreach (EquipmentSlotState slot in slots)
        {
            await UpsertEquipmentAsync(connection, CharacterKey(state), slot.Slot, slot.Item, observedAt, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static Task PersistEquipmentChangeAsync(
        SqliteConnection connection,
        StateSnapshot state,
        EquipmentChanged equipment,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) =>
        UpsertEquipmentAsync(connection, CharacterKey(state), equipment.Slot, equipment.Item, observedAt, cancellationToken);

    private static async Task UpsertEquipmentAsync(
        SqliteConnection connection,
        string characterKey,
        string slot,
        string? item,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO equipment(character_key, slot, item, last_seen_at)
            VALUES($characterKey, $slot, $item, $lastSeenAt)
            ON CONFLICT(character_key, slot) DO UPDATE SET
                item = excluded.item,
                last_seen_at = excluded.last_seen_at;
            """;
        command.Parameters.AddWithValue("$characterKey", characterKey);
        command.Parameters.AddWithValue("$slot", slot);
        command.Parameters.AddWithValue("$item", DbValue(item));
        command.Parameters.AddWithValue("$lastSeenAt", observedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistExecutedCommandAsync(
        SqliteConnection connection,
        ActionExecuted action,
        DateTimeOffset executedAt,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO action_history(executed_at, action_id, command, source)
            VALUES($executedAt, $actionId, $command, $source);
            """;
        command.Parameters.AddWithValue("$executedAt", executedAt.ToString("O"));
        command.Parameters.AddWithValue("$actionId", action.ActionId.ToString("D"));
        command.Parameters.AddWithValue("$command", action.Command);
        command.Parameters.AddWithValue("$source", action.Provenance?.Origin.ToString() ?? action.Source.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistItemAcquisitionAsync(
        SqliteConnection connection,
        ItemAcquired acquired,
        string? roomId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        string itemName = NormalizeSemanticText(acquired.ItemName) ?? acquired.ItemName.Trim();
        if (itemName.Length == 0) return;

        using (SqliteCommand item = connection.CreateCommand())
        {
            item.CommandText = """
                INSERT INTO item_knowledge(
                    item_name, flags_json, weight, wear_locations_json, level, material,
                    item_type, weapon_type, weapon_flags_json, damage_type, damage_dice,
                    damage_average, extra_fields_json, raw_text, first_seen_at, last_seen_at, observation_count)
                VALUES($name, '[]', NULL, '[]', NULL, NULL, NULL, NULL, '[]', NULL, NULL, NULL, '{}',
                       $rawText, $observedAt, $observedAt, 1)
                ON CONFLICT(item_name) DO UPDATE SET
                    last_seen_at = excluded.last_seen_at,
                    observation_count = item_knowledge.observation_count + 1;
                """;
            item.Parameters.AddWithValue("$name", itemName);
            item.Parameters.AddWithValue("$rawText", acquired.RawText);
            item.Parameters.AddWithValue("$observedAt", observedAt.ToString("O"));
            await item.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(roomId)) return;
        string? mobName = await ResolveAcquisitionMobAsync(
            connection, acquired, roomId, observedAt, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(mobName)) return;

        using SqliteCommand source = connection.CreateCommand();
        source.CommandText = """
            INSERT INTO mob_item_sources(
                item_name, mob_name, room_id, source_kind, first_seen_at, last_seen_at, observation_count)
            VALUES($itemName, $mobName, $roomId, $sourceKind, $observedAt, $observedAt, 1)
            ON CONFLICT(item_name, mob_name, room_id, source_kind) DO UPDATE SET
                last_seen_at = excluded.last_seen_at,
                observation_count = mob_item_sources.observation_count + 1;
            """;
        source.Parameters.AddWithValue("$itemName", itemName);
        source.Parameters.AddWithValue("$mobName", mobName);
        source.Parameters.AddWithValue("$roomId", roomId);
        source.Parameters.AddWithValue("$sourceKind", acquired.SourceKind.ToString());
        source.Parameters.AddWithValue("$observedAt", observedAt.ToString("O"));
        await source.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ResolveAcquisitionMobAsync(
        SqliteConnection connection,
        ItemAcquired acquired,
        string roomId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        string? candidate = acquired.SourceKind switch
        {
            ItemAcquisitionSourceKind.Corpse => ExtractMobNameFromCorpse(acquired.SourceDescription),
            ItemAcquisitionSourceKind.MobDrop => NormalizeSemanticText(acquired.SourceDescription),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(candidate) && acquired.SourceKind == ItemAcquisitionSourceKind.Corpse)
        {
            using SqliteCommand recentKill = connection.CreateCommand();
            recentKill.CommandText = """
                SELECT target
                FROM combat_events
                WHERE room_id = $roomId
                  AND event_type = $eventType
                  AND target IS NOT NULL
                  AND observed_at >= $cutoff
                ORDER BY observed_at DESC
                LIMIT 1;
                """;
            recentKill.Parameters.AddWithValue("$roomId", roomId);
            recentKill.Parameters.AddWithValue("$eventType", nameof(EnemyKilled));
            recentKill.Parameters.AddWithValue("$cutoff", observedAt.Subtract(TimeSpan.FromMinutes(15)).ToString("O"));
            object? result = await recentKill.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            candidate = result as string;
        }

        if (string.IsNullOrWhiteSpace(candidate)) return null;

        using SqliteCommand observedMob = connection.CreateCommand();
        observedMob.CommandText = """
            SELECT COALESCE(canonical_name, description)
            FROM room_entities
            WHERE room_id = $roomId
              AND kind = $occupantKind
              AND (COALESCE(canonical_name, description) = $candidate COLLATE NOCASE
                   OR description LIKE $contains COLLATE NOCASE)
            ORDER BY last_seen_at DESC
            LIMIT 1;
            """;
        observedMob.Parameters.AddWithValue("$roomId", roomId);
        observedMob.Parameters.AddWithValue("$occupantKind", RoomEntityKind.Occupant.ToString());
        observedMob.Parameters.AddWithValue("$candidate", candidate.Trim());
        observedMob.Parameters.AddWithValue("$contains", $"%{candidate.Trim()}%");
        object? matched = await observedMob.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (matched is string knownMob && !string.IsNullOrWhiteSpace(knownMob)) return knownMob;

        return acquired.SourceKind == ItemAcquisitionSourceKind.Corpse ? candidate.Trim() : null;
    }

    private static string? ExtractMobNameFromCorpse(string? sourceDescription)
    {
        string? source = NormalizeSemanticText(sourceDescription);
        if (string.IsNullOrWhiteSpace(source)) return null;

        int corpseOf = source.IndexOf("corpse of ", StringComparison.OrdinalIgnoreCase);
        if (corpseOf >= 0)
        {
            string candidate = source[(corpseOf + "corpse of ".Length)..].Trim();
            return candidate.Length == 0 ? null : candidate;
        }

        const string possessiveCorpse = "'s corpse";
        if (source.EndsWith(possessiveCorpse, StringComparison.OrdinalIgnoreCase))
        {
            string candidate = source[..^possessiveCorpse.Length].Trim();
            return candidate.Length == 0 ? null : candidate;
        }

        return null;
    }

    private static async Task PersistItemKnowledgeAsync(
        SqliteConnection connection,
        ItemIdentification item,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO item_knowledge(
                item_name, flags_json, weight, wear_locations_json, level, material,
                item_type, weapon_type, weapon_flags_json, damage_type, damage_dice,
                damage_average, extra_fields_json, raw_text, first_seen_at, last_seen_at,
                observation_count)
            VALUES(
                $name, $flags, $weight, $wear, $level, $material,
                $itemType, $weaponType, $weaponFlags, $damageType, $damageDice,
                $damageAverage, $extra, $rawText, $observedAt, $observedAt, 1)
            ON CONFLICT(item_name) DO UPDATE SET
                flags_json = excluded.flags_json,
                weight = excluded.weight,
                wear_locations_json = excluded.wear_locations_json,
                level = excluded.level,
                material = excluded.material,
                item_type = excluded.item_type,
                weapon_type = excluded.weapon_type,
                weapon_flags_json = excluded.weapon_flags_json,
                damage_type = excluded.damage_type,
                damage_dice = excluded.damage_dice,
                damage_average = excluded.damage_average,
                extra_fields_json = excluded.extra_fields_json,
                raw_text = excluded.raw_text,
                last_seen_at = excluded.last_seen_at,
                observation_count = item_knowledge.observation_count + 1;
            """;
        command.Parameters.AddWithValue("$name", item.Name);
        command.Parameters.AddWithValue("$flags", JsonSerializer.Serialize(item.Flags, JsonOptions));
        command.Parameters.AddWithValue("$weight", DbValue(item.Weight));
        command.Parameters.AddWithValue("$wear", JsonSerializer.Serialize(item.WearLocations, JsonOptions));
        command.Parameters.AddWithValue("$level", DbValue(item.Level));
        command.Parameters.AddWithValue("$material", DbValue(item.Material));
        command.Parameters.AddWithValue("$itemType", DbValue(item.ItemType));
        command.Parameters.AddWithValue("$weaponType", DbValue(item.WeaponType));
        command.Parameters.AddWithValue("$weaponFlags", JsonSerializer.Serialize(item.WeaponFlags, JsonOptions));
        command.Parameters.AddWithValue("$damageType", DbValue(item.DamageType));
        command.Parameters.AddWithValue("$damageDice", DbValue(item.DamageDice));
        command.Parameters.AddWithValue("$damageAverage", DbValue(item.DamageAverage));
        command.Parameters.AddWithValue("$extra", JsonSerializer.Serialize(item.ExtraFields, JsonOptions));
        command.Parameters.AddWithValue("$rawText", item.RawText);
        command.Parameters.AddWithValue("$observedAt", observedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistAbilityHelpAsync(
        SqliteConnection connection,
        AbilityHelpDocument help,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ability_help(
                ability_name, ability_kind, activation_lag, activation_mana_cost,
                syntax, description, fields_json, raw_text, first_seen_at, last_seen_at,
                observation_count)
            VALUES(
                $name, $kind, $lag, $manaCost,
                $syntax, $description, $fields, $rawText, $observedAt, $observedAt, 1)
            ON CONFLICT(ability_name, ability_kind) DO UPDATE SET
                activation_lag = excluded.activation_lag,
                activation_mana_cost = excluded.activation_mana_cost,
                syntax = excluded.syntax,
                description = excluded.description,
                fields_json = excluded.fields_json,
                raw_text = excluded.raw_text,
                last_seen_at = excluded.last_seen_at,
                observation_count = ability_help.observation_count + 1;
            """;
        command.Parameters.AddWithValue("$name", help.Name);
        command.Parameters.AddWithValue("$kind", help.Kind.ToString());
        command.Parameters.AddWithValue("$lag", DbValue(help.ActivationLagRounds));
        command.Parameters.AddWithValue("$manaCost", DbValue(help.ActivationManaCost));
        command.Parameters.AddWithValue("$syntax", DbValue(help.Syntax));
        command.Parameters.AddWithValue("$description", help.Description);
        command.Parameters.AddWithValue("$fields", JsonSerializer.Serialize(help.Fields, JsonOptions));
        command.Parameters.AddWithValue("$rawText", help.RawText);
        command.Parameters.AddWithValue("$observedAt", observedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }


    private static async Task PersistExplicitAreaAsync(
        SqliteConnection connection,
        string roomId,
        string area,
        CancellationToken cancellationToken)
    {
        string normalized = area.Trim();
        if (normalized.Length == 0) return;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO room_metadata(room_id, area)
            VALUES($roomId, $area)
            ON CONFLICT(room_id) DO UPDATE SET area = excluded.area;
            """;
        command.Parameters.AddWithValue("$roomId", roomId);
        command.Parameters.AddWithValue("$area", normalized);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistRoomAsync(
        SqliteConnection connection,
        RoomObservationObserved room,
        StateSnapshot state,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        _previousRoomId = _currentRoomId;
        bool enteredRoom = !string.Equals(_currentRoomId, room.RoomId, StringComparison.Ordinal);
        _currentRoomId = room.RoomId;

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO rooms(room_id, name, description_fingerprint, description, terrain, light, first_seen_at, last_seen_at, visit_count)
                VALUES($roomId, $name, $fingerprint, $description, $terrain, $light, $observedAt, $observedAt, 1)
                ON CONFLICT(room_id) DO UPDATE SET
                    name = excluded.name,
                    description_fingerprint = excluded.description_fingerprint,
                    description = excluded.description,
                    terrain = COALESCE(excluded.terrain, rooms.terrain),
                    light = COALESCE(excluded.light, rooms.light),
                    last_seen_at = excluded.last_seen_at,
                    visit_count = rooms.visit_count + $visitIncrement;
                """;
            command.Parameters.AddWithValue("$roomId", room.RoomId);
            command.Parameters.AddWithValue("$name", room.RoomName);
            command.Parameters.AddWithValue("$fingerprint", room.DescriptionFingerprint);
            command.Parameters.AddWithValue("$description", room.Description);
            command.Parameters.AddWithValue("$terrain", DbValue(state.Room.Terrain));
            command.Parameters.AddWithValue("$light", DbValue(state.Room.Light));
            command.Parameters.AddWithValue("$observedAt", observedAt.ToString("O"));
            command.Parameters.AddWithValue("$visitIncrement", enteredRoom ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (RoomExitObservation exit in room.ExitDetails)
        {
            await UpsertExitAsync(connection, room.RoomId, exit, null, observedAt, cancellationToken).ConfigureAwait(false);
        }

        foreach (RoomContentObservation entity in room.Contents)
        {
            string key = BuildEntityKey(entity);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO room_entities(
                    room_id, entity_key, kind, description, canonical_name, traits,
                    target_keywords_json, first_seen_at, last_seen_at, observation_count)
                VALUES($roomId, $entityKey, $kind, $description, $canonicalName, $traits,
                    $keywords, $observedAt, $observedAt, 1)
                ON CONFLICT(room_id, entity_key) DO UPDATE SET
                    description = excluded.description,
                    canonical_name = COALESCE(excluded.canonical_name, room_entities.canonical_name),
                    traits = excluded.traits,
                    target_keywords_json = excluded.target_keywords_json,
                    last_seen_at = excluded.last_seen_at,
                    observation_count = room_entities.observation_count + 1;
                """;
            command.Parameters.AddWithValue("$roomId", room.RoomId);
            command.Parameters.AddWithValue("$entityKey", key);
            command.Parameters.AddWithValue("$kind", entity.Kind.ToString());
            command.Parameters.AddWithValue("$description", entity.Description);
            command.Parameters.AddWithValue("$canonicalName", DbValue(entity.CanonicalName));
            command.Parameters.AddWithValue("$traits", (long)entity.Traits);
            command.Parameters.AddWithValue("$keywords", JsonSerializer.Serialize(entity.TargetKeywords ?? Array.Empty<string>(), JsonOptions));
            command.Parameters.AddWithValue("$observedAt", observedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(_previousRoomId) &&
            !string.Equals(_previousRoomId, room.RoomId, StringComparison.Ordinal))
        {
            PendingTraversal? traversal = TakeTraversalFromRoom(_previousRoomId!, observedAt);
            if (traversal is not null)
            {
                RoomExitObservation inferred = new(
                    traversal.Direction,
                    true,
                    ExitDoorState.Unknown,
                    ExitTraversability.Traversable);
                await UpsertExitAsync(
                    connection,
                    _previousRoomId!,
                    inferred,
                    room.RoomId,
                    observedAt,
                    cancellationToken).ConfigureAwait(false);
                await PersistRoomTransitionAsync(
                    connection,
                    _previousRoomId!,
                    room.RoomId,
                    "exit",
                    traversal.Direction,
                    observedAt,
                    cancellationToken).ConfigureAwait(false);
                RebaseQueuedTraversalBatch(_previousRoomId!, room.RoomId);
            }
            else
            {
                // Recall, teleport, summon, death/respawn, scripted transport, and unknown
                // relocations are observations of movement, not traversable map edges.
                await PersistRoomTransitionAsync(
                    connection,
                    _previousRoomId!,
                    room.RoomId,
                    "relocation",
                    null,
                    observedAt,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        RoomKnowledge? current = await ReadRoomAsync(connection, room.RoomId, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _currentRoomKnowledge, current);
    }

    private static async Task PersistRoomTransitionAsync(
        SqliteConnection connection,
        string fromRoomId,
        string toRoomId,
        string transitionKind,
        string? direction,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO room_transitions(from_room_id, to_room_id, transition_kind, direction, observed_at)
            VALUES($fromRoomId, $toRoomId, $transitionKind, $direction, $observedAt);
            """;
        command.Parameters.AddWithValue("$fromRoomId", fromRoomId);
        command.Parameters.AddWithValue("$toRoomId", toRoomId);
        command.Parameters.AddWithValue("$transitionKind", transitionKind);
        command.Parameters.AddWithValue("$direction", DbValue(direction));
        command.Parameters.AddWithValue("$observedAt", observedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task PersistExitStateAsync(
        SqliteConnection connection,
        string roomId,
        RoomExitStateChanged exit,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) =>
        UpsertExitAsync(
            connection,
            roomId,
            new RoomExitObservation(exit.Direction, true, exit.DoorState, exit.Traversability, exit.BlockReason),
            null,
            observedAt,
            cancellationToken);

    private static async Task UpsertExitAsync(
        SqliteConnection connection,
        string roomId,
        RoomExitObservation exit,
        string? toRoomId,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO room_exits(from_room_id, direction, to_room_id, door_state, traversability, block_reason, last_seen_at)
            VALUES($fromRoomId, $direction, $toRoomId, $doorState, $traversability, $blockReason, $lastSeenAt)
            ON CONFLICT(from_room_id, direction) DO UPDATE SET
                to_room_id = COALESCE(excluded.to_room_id, room_exits.to_room_id),
                door_state = excluded.door_state,
                traversability = excluded.traversability,
                block_reason = excluded.block_reason,
                last_seen_at = excluded.last_seen_at;
            """;
        command.Parameters.AddWithValue("$fromRoomId", roomId);
        command.Parameters.AddWithValue("$direction", exit.Direction);
        command.Parameters.AddWithValue("$toRoomId", DbValue(toRoomId));
        command.Parameters.AddWithValue("$doorState", exit.DoorState.ToString());
        command.Parameters.AddWithValue("$traversability", exit.Traversability.ToString());
        command.Parameters.AddWithValue("$blockReason", DbValue(exit.BlockReason));
        command.Parameters.AddWithValue("$lastSeenAt", observedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistCombatEventAsync(
        SqliteConnection connection,
        EventEnvelope envelope,
        StateSnapshot state,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        string? target = envelope.Payload switch
        {
            CombatTargetConditionObserved observed => observed.TargetName,
            EnemyKilled killed => killed.TargetName,
            CombatDamageObserved damage => damage.Damage.Source == CombatActor.Player ? damage.Damage.TargetName : damage.Damage.SourceName,
            CombatAttackObserved attack => attack.Attack.Source == CombatActor.Player ? attack.Attack.TargetName : attack.Attack.SourceName,
            _ => state.Combat.TargetName ?? state.Combat.TargetId
        };

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO combat_events(observed_at, event_type, target, room_id, state_version, payload_json)
            VALUES($observedAt, $eventType, $target, $roomId, $stateVersion, $payload);
            """;
        command.Parameters.AddWithValue("$observedAt", envelope.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$eventType", envelope.Payload.GetType().Name);
        command.Parameters.AddWithValue("$target", DbValue(target));
        command.Parameters.AddWithValue("$roomId", DbValue(state.Room.Id));
        command.Parameters.AddWithValue("$stateVersion", state.Version);
        command.Parameters.AddWithValue("$payload", payloadJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistCommunicationAsync(
        SqliteConnection connection,
        CommunicationObserved communication,
        DateTimeOffset observedAt,
        string? roomId,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO communications(observed_at, channel, speaker, message, room_id)
            VALUES($observedAt, $channel, $speaker, $message, $roomId);
            """;
        command.Parameters.AddWithValue("$observedAt", observedAt.ToString("O"));
        command.Parameters.AddWithValue("$channel", communication.Channel);
        command.Parameters.AddWithValue("$speaker", DbValue(communication.Speaker));
        command.Parameters.AddWithValue("$message", communication.Message);
        command.Parameters.AddWithValue("$roomId", DbValue(roomId));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PersistJevDecisionAsync(
        SqliteConnection connection,
        JevDecisionTrace decision,
        DateTimeOffset observedAt,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR REPLACE INTO jev_decisions(
                decision_id, observed_at, state_version, domain, selected_action,
                confidence, authority, outcome, model, payload_json)
            VALUES($decisionId, $observedAt, $stateVersion, $domain, $selectedAction,
                $confidence, $authority, $outcome, $model, $payload);
            """;
        command.Parameters.AddWithValue("$decisionId", decision.DecisionId.ToString("D"));
        command.Parameters.AddWithValue("$observedAt", observedAt.ToString("O"));
        command.Parameters.AddWithValue("$stateVersion", decision.StateVersion);
        command.Parameters.AddWithValue("$domain", decision.Domain.ToString());
        command.Parameters.AddWithValue("$selectedAction", decision.Selected.Action);
        command.Parameters.AddWithValue("$confidence", decision.Confidence);
        command.Parameters.AddWithValue("$authority", decision.Authority.ToString());
        command.Parameters.AddWithValue("$outcome", decision.Outcome.ToString());
        command.Parameters.AddWithValue("$model", decision.Model);
        command.Parameters.AddWithValue("$payload", payloadJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshSummaryAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM mud_events),
                (SELECT COUNT(*) FROM sessions),
                (SELECT COUNT(*) FROM rooms),
                (SELECT COUNT(*) FROM room_entities),
                (SELECT COUNT(*) FROM combat_events),
                (SELECT COUNT(*) FROM communications),
                (SELECT COUNT(*) FROM command_history),
                (SELECT COUNT(*) FROM jev_decisions),
                (SELECT COUNT(*) FROM item_knowledge),
                (SELECT COUNT(*) FROM ability_help);
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        Volatile.Write(ref _summary, new KnowledgeSummary(
            DatabasePath,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            DateTimeOffset.UtcNow));
    }

    private static async Task<ItemKnowledge?> ReadItemKnowledgeAsync(
        SqliteConnection connection,
        string itemName,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_name, flags_json, weight, wear_locations_json, level, material,
                   item_type, weapon_type, weapon_flags_json, damage_type, damage_dice,
                   damage_average, extra_fields_json, observation_count, last_seen_at
            FROM item_knowledge
            WHERE item_name = $name COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", itemName);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ItemKnowledge(
            reader.GetString(0),
            DeserializeStrings(reader.GetString(1)),
            reader.IsDBNull(2) ? null : Convert.ToDecimal(reader.GetDouble(2)),
            DeserializeStrings(reader.GetString(3)),
            reader.IsDBNull(4) ? null : ToInt32(reader.GetInt64(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            DeserializeStrings(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : Convert.ToDecimal(reader.GetDouble(11)),
            DeserializeStringDictionary(reader.GetString(12)),
            ToInt32(reader.GetInt64(13)),
            ParseDate(reader.IsDBNull(14) ? null : reader.GetString(14)));
    }

    private static async Task<AbilityHelpKnowledge?> ReadAbilityHelpKnowledgeAsync(
        SqliteConnection connection,
        string abilityName,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT ability_name, ability_kind, activation_lag, activation_mana_cost,
                   syntax, description, fields_json, observation_count, last_seen_at
            FROM ability_help
            WHERE ability_name = $name COLLATE NOCASE
            ORDER BY last_seen_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", abilityName);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        AbilityHelpKind kind = Enum.TryParse(reader.GetString(1), ignoreCase: true, out AbilityHelpKind parsedKind)
            ? parsedKind
            : AbilityHelpKind.Unknown;
        return new AbilityHelpKnowledge(
            reader.GetString(0),
            kind,
            reader.IsDBNull(2) ? null : Convert.ToDecimal(reader.GetDouble(2)),
            reader.IsDBNull(3) ? null : ToInt32(reader.GetInt64(3)),
            NormalizeSemanticText(reader.IsDBNull(4) ? null : reader.GetString(4)),
            NormalizeSemanticText(reader.GetString(5)) ?? string.Empty,
            NormalizeFields(DeserializeStringDictionary(reader.GetString(6))),
            ToInt32(reader.GetInt64(7)),
            ParseDate(reader.IsDBNull(8) ? null : reader.GetString(8)));
    }

    private static IReadOnlyDictionary<string, string> DeserializeStringDictionary(string json)
    {
        try
        {
            Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);
            return parsed is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyList<string> DeserializeStrings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static async Task<RoomKnowledge?> ReadRoomAsync(
        SqliteConnection connection,
        string roomId,
        CancellationToken cancellationToken)
    {
        string? name;
        string? description;
        string? terrain;
        string? light;
        int visitCount;
        DateTimeOffset? firstSeen;
        DateTimeOffset? lastSeen;

        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT name, description, terrain, light, visit_count, first_seen_at, last_seen_at
                FROM rooms WHERE room_id = $roomId;
                """;
            command.Parameters.AddWithValue("$roomId", roomId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            name = reader.IsDBNull(0) ? null : reader.GetString(0);
            description = reader.IsDBNull(1) ? null : reader.GetString(1);
            terrain = reader.IsDBNull(2) ? null : reader.GetString(2);
            light = reader.IsDBNull(3) ? null : reader.GetString(3);
            visitCount = ToInt32(reader.GetInt64(4));
            firstSeen = ParseDate(reader.IsDBNull(5) ? null : reader.GetString(5));
            lastSeen = ParseDate(reader.IsDBNull(6) ? null : reader.GetString(6));
        }

        List<KnowledgeExit> exits = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT e.direction, e.to_room_id, r.name, e.door_state, e.traversability, e.block_reason, e.last_seen_at
                FROM room_exits e
                LEFT JOIN rooms r ON r.room_id = e.to_room_id
                WHERE e.from_room_id = $roomId
                ORDER BY e.direction;
                """;
            command.Parameters.AddWithValue("$roomId", roomId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                exits.Add(new KnowledgeExit(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    ParseDate(reader.IsDBNull(6) ? null : reader.GetString(6))));
            }
        }

        List<KnowledgeEntity> entities = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT kind, description, canonical_name, observation_count, last_seen_at
                FROM room_entities
                WHERE room_id = $roomId
                ORDER BY observation_count DESC, last_seen_at DESC
                LIMIT 12;
                """;
            command.Parameters.AddWithValue("$roomId", roomId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entities.Add(new KnowledgeEntity(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    ToInt32(reader.GetInt64(3)),
                    ParseDate(reader.IsDBNull(4) ? null : reader.GetString(4))));
            }
        }

        return new RoomKnowledge(roomId, name, description, terrain, light, visitCount, firstSeen, lastSeen, exits, entities);
    }

    private static async Task<CombatKnowledge?> ReadCombatKnowledgeAsync(
        SqliteConnection connection,
        string target,
        CancellationToken cancellationToken)
    {
        int count;
        int kills;
        DateTimeOffset? lastSeen;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*),
                       SUM(CASE WHEN event_type = 'EnemyKilled' THEN 1 ELSE 0 END),
                       MAX(observed_at)
                FROM combat_events
                WHERE target = $target COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$target", target);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            count = ToInt32(reader.GetInt64(0));
            kills = reader.IsDBNull(1) ? 0 : ToInt32(reader.GetInt64(1));
            lastSeen = ParseDate(reader.IsDBNull(2) ? null : reader.GetString(2));
        }

        if (count == 0)
        {
            return null;
        }

        List<CombatMemoryEvent> recent = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT observed_at, event_type, room_id, payload_json FROM combat_events
                WHERE target = $target COLLATE NOCASE
                ORDER BY id DESC LIMIT 8;
                """;
            command.Parameters.AddWithValue("$target", target);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string payloadJson = reader.GetString(3);
                recent.Add(new CombatMemoryEvent(
                    ParseDate(reader.GetString(0)),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    ParseJson(payloadJson)));
            }
        }

        return new CombatKnowledge(target, count, kills, lastSeen, recent);
    }

    private static async Task<IReadOnlyList<ExecutedCommandKnowledge>> ReadRecentExecutedCommandsAsync(
        SqliteConnection connection,
        int limit,
        CancellationToken cancellationToken)
    {
        List<ExecutedCommandKnowledge> commands = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT command, source, executed_at
            FROM action_history
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            commands.Add(new ExecutedCommandKnowledge(
                reader.GetString(0),
                reader.IsDBNull(1) ? DecisionSource.Human.ToString() : reader.GetString(1),
                ParseDate(reader.IsDBNull(2) ? null : reader.GetString(2))));
        }
        commands.Reverse();
        return commands;
    }


    private static async Task<IReadOnlyList<CommunicationKnowledge>> ReadRecentCommunicationsAsync(
        SqliteConnection connection,
        string? roomId,
        int limit,
        CancellationToken cancellationToken)
    {
        List<CommunicationKnowledge> communications = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = string.IsNullOrWhiteSpace(roomId)
            ? "SELECT channel, speaker, message, observed_at FROM communications ORDER BY id DESC LIMIT $limit;"
            : "SELECT channel, speaker, message, observed_at FROM communications WHERE room_id = $roomId ORDER BY id DESC LIMIT $limit;";
        if (!string.IsNullOrWhiteSpace(roomId))
        {
            command.Parameters.AddWithValue("$roomId", roomId!);
        }
        command.Parameters.AddWithValue("$limit", limit);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            communications.Add(new CommunicationKnowledge(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                Truncate(NormalizeSemanticText(reader.GetString(2)), 512) ?? string.Empty,
                ParseDate(reader.IsDBNull(3) ? null : reader.GetString(3))));
        }
        communications.Reverse();
        return communications;
    }

    private static async Task<IReadOnlyList<string>> ReadRecentCommandsAsync(
        SqliteConnection connection,
        int limit,
        bool sentToMudOnly,
        CancellationToken cancellationToken)
    {
        List<string> commands = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sentToMudOnly
            ? "SELECT command FROM command_history WHERE sent_to_mud = 1 ORDER BY id DESC LIMIT $limit;"
            : "SELECT command FROM command_history ORDER BY id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            commands.Add(reader.GetString(0));
        }
        commands.Reverse();
        return commands;
    }


    private static RoomKnowledge BoundRoomForJev(RoomKnowledge room) => room with
    {
        Description = Truncate(room.Description, 1600),
        FrequentEntities = room.FrequentEntities.Take(8).ToArray()
    };

    private static string? NormalizeSemanticText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return System.Text.RegularExpressions.Regex.Replace(value.Trim(), @"\s+", " ");
    }

    private static IReadOnlyDictionary<string, string> NormalizeFields(IReadOnlyDictionary<string, string> fields) =>
        fields.ToDictionary(
            pair => pair.Key,
            pair => NormalizeSemanticText(pair.Value) ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }
        return value[..maxLength] + "…";
    }

    private static int ToInt32(long value) =>
        value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;

    private static JsonElement ParseJson(string value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { raw = Truncate(value, 1024) }, JsonOptions);
        }
    }

    private static string SerializeEventPayload(IMudEvent payload)
    {
        try
        {
            return JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return JsonSerializer.Serialize(new
            {
                serializationError = exception.Message,
                eventType = payload.GetType().FullName,
                display = payload.ToString()
            }, JsonOptions);
        }
    }

    private static string CharacterKey(StateSnapshot state) =>
        string.IsNullOrWhiteSpace(state.Character.Profile.Name)
            ? $"{state.Session.Host ?? "unknown"}:unknown"
            : $"{state.Session.Host ?? "unknown"}:{state.Character.Profile.Name}";

    private static string BuildEntityKey(RoomContentObservation entity)
    {
        string name = entity.CanonicalName ?? entity.Description;
        return $"{entity.Kind}:{name.Trim().ToLowerInvariant()}";
    }

    private const char CodexEntityKeySeparator = '\u001f';

    private static string BuildCodexEntityKey(string entityName, string scopeKind, string scopeValue) =>
        string.Join(CodexEntityKeySeparator, entityName.Trim(), scopeKind.Trim().ToLowerInvariant(), scopeValue.Trim());

    private static void DecodeCodexEntityKey(
        string key,
        out string entityName,
        out string? scopeKind,
        out string? scopeValue)
    {
        string[] parts = key.Split(CodexEntityKeySeparator, 3);
        if (parts.Length == 3 && (parts[1] == "area" || parts[1] == "room"))
        {
            entityName = parts[0];
            scopeKind = parts[1];
            scopeValue = parts[2];
            return;
        }

        entityName = key;
        scopeKind = null;
        scopeValue = null;
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : null;
}
