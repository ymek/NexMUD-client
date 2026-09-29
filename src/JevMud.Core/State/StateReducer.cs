using System.Collections.ObjectModel;
using System.Threading.Channels;
using JevMud.Contracts.Events;
using JevMud.Contracts.State;

namespace JevMud.Core.State;

public sealed class StateReducer
{
    private readonly ChannelReader<EventEnvelope> _events;
    private readonly object _stateLock = new();
    private readonly object _subscriberLock = new();
    private readonly object _sequenceLock = new();
    private readonly List<Channel<StateSnapshot>> _subscribers = [];
    private TaskCompletionSource<long> _sequenceAdvanced = NewSequenceSignal();
    private StateSnapshot _current = StateSnapshot.Initial;
    private long _lastProcessedSequence;
    private bool _completed;

    public StateReducer(ChannelReader<EventEnvelope> events)
    {
        _events = events;
    }

    public long LastProcessedSequence => Interlocked.Read(ref _lastProcessedSequence);

    public async Task WaitUntilProcessedAsync(long sequence, CancellationToken cancellationToken = default)
    {
        if (sequence <= 0 || LastProcessedSequence >= sequence) return;

        while (LastProcessedSequence < sequence)
        {
            Task<long> wait;
            lock (_sequenceLock)
            {
                if (LastProcessedSequence >= sequence) return;
                wait = _sequenceAdvanced.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public StateSnapshot Current
    {
        get
        {
            lock (_stateLock)
            {
                return _current;
            }
        }
    }

    public ChannelReader<StateSnapshot> Subscribe(int capacity = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Channel<StateSnapshot> channel = Channel.CreateBounded<StateSnapshot>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });

        lock (_subscriberLock)
        {
            if (_completed)
            {
                channel.Writer.TryComplete();
                return channel.Reader;
            }

            _subscribers.Add(channel);
        }

        return channel.Reader;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (EventEnvelope envelope in _events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                StateSnapshot next;
                bool changed;
                lock (_stateLock)
                {
                    StateSnapshot previous = _current;
                    next = Reduce(previous, envelope);
                    changed = !ReferenceEquals(previous, next);
                    if (changed)
                    {
                        _current = next;
                    }
                }

                Interlocked.Exchange(ref _lastProcessedSequence, envelope.Sequence);
                SignalSequenceAdvanced(envelope.Sequence);

                if (changed)
                {
                    PublishSnapshot(next);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            SignalSequenceAdvanced(LastProcessedSequence);
            CompleteSubscribers();
        }
    }

    private static TaskCompletionSource<long> NewSequenceSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void SignalSequenceAdvanced(long sequence)
    {
        TaskCompletionSource<long> signal;
        lock (_sequenceLock)
        {
            signal = _sequenceAdvanced;
            _sequenceAdvanced = NewSequenceSignal();
        }
        signal.TrySetResult(sequence);
    }

    public static StateSnapshot Reduce(StateSnapshot current, EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(envelope);

        StateSnapshot next = envelope.Payload switch
        {
            ConnectionStateChanged change => ReduceConnection(current, change),
            SessionInputModeChanged mode => ReduceInputMode(current, mode),
            ResponseCaptureChanged capture => ReduceResponseCapture(current, capture),
            TransportError error => ReduceTransportError(current, error),
            CharacterVitalsChanged vitals => ReduceVitals(current, vitals),
            CharacterPositionObserved position => ReducePosition(current, position),
            CharacterPromptObserved prompt => ReducePrompt(current, prompt),
            CharacterScoreObserved score => ReduceScore(current, score),
            SkillsSnapshotObserved skills => ReduceSkills(current, skills, envelope.Timestamp),
            SpellsSnapshotObserved spells => ReduceSpells(current, spells, envelope.Timestamp),
            SkillImproved improved => ReduceSkillStale(current, improved.SkillName),
            SkillPracticeSucceeded practiced => ReduceSkillStale(current, practiced.SkillName),
            EquipmentChanged equipment => ReduceEquipment(current, equipment),
            EquipmentSnapshotObserved equipment => ReduceEquipmentSnapshot(current, equipment),
            InventorySnapshotObserved inventory => ReduceInventorySnapshot(current, inventory),
            ItemIdentified item => ReduceItemIdentification(current, item.Item),
            CharacterConditionChanged condition => ReduceCharacterCondition(current, condition),
            RoomChanged room => ReduceRoom(current, room),
            RoomObservationObserved room => ReduceRoomObservation(current, room, envelope.Timestamp),
            RoomContentsObserved contents => ReduceLegacyRoomContents(current, contents),
            RoomOccupantDeparted departed => ReduceRoomOccupantDeparted(current, departed),
            NavigationAttempted navigation => ReduceNavigationAttempted(current, navigation),
            NavigationResponseCompleted response => ReduceNavigationResponseCompleted(current, response),
            NavigationFailed failed => ReduceNavigationFailed(current, failed),
            RoomExitStateChanged exit => ReduceRoomExitStateChanged(current, exit),
            CombatStateChanged combat => ReduceCombatState(current, combat),
            CombatDamageObserved damage => ReduceDamage(current, damage.Damage),
            CombatAttackObserved attack => ReduceAttack(current, attack.Attack),
            CombatTargetConditionObserved condition => ReduceCondition(current, condition),
            EnemyKilled killed => ReduceKilled(current, killed),
            CorpseDestroyed destroyed => ReduceCorpseDestroyed(current, destroyed),
            EffectStateChanged effect => ReduceEffect(current, effect),
            ExperienceGained experience => ReduceExperienceGained(current, experience),
            ExplorationGained exploration => ReduceExplorationGained(current, exploration),
            CurrencyGained currency => ReduceCurrencyGained(current, currency),
            CharacterPromoted promotion => ReducePromotion(current, promotion),
            JevAuthorityProfileChanged authority => current with { JevAuthority = authority.Snapshot },
            _ => current
        };

        if (ReferenceEquals(next, current))
        {
            return current;
        }

        return next with
        {
            Version = current.Version + 1,
            Timestamp = envelope.Timestamp
        };
    }

    private static StateSnapshot ReduceConnection(StateSnapshot current, ConnectionStateChanged change)
    {
        SessionState next = new(
            change.Status,
            change.Host ?? current.Session.Host,
            change.Port ?? current.Session.Port,
            change.Status == ConnectionStatus.Disconnected ? change.Reason : null,
            change.Status == ConnectionStatus.Disconnected ? SessionInputMode.Unknown : current.Session.InputMode);

        return next == current.Session ? current : current with { Session = next };
    }

    private static StateSnapshot ReduceInputMode(StateSnapshot current, SessionInputModeChanged mode)
    {
        if (current.Session.InputMode == mode.Mode)
        {
            return current;
        }

        return current with { Session = current.Session with { InputMode = mode.Mode } };
    }

    private static StateSnapshot ReduceResponseCapture(StateSnapshot current, ResponseCaptureChanged capture)
    {
        if (current.Session.ActiveCapture == capture.Capture)
        {
            return current;
        }

        return current with { Session = current.Session with { ActiveCapture = capture.Capture } };
    }

    private static StateSnapshot ReduceTransportError(StateSnapshot current, TransportError error)
    {
        if (string.Equals(current.Session.LastError, error.Message, StringComparison.Ordinal))
        {
            return current;
        }

        return current with { Session = current.Session with { LastError = error.Message } };
    }

    private static StateSnapshot ReduceVitals(StateSnapshot current, CharacterVitalsChanged vitals)
    {
        CharacterState next = current.Character with
        {
            HitPoints = MergeVital(current.Character.HitPoints, vitals.HitPoints, vitals.MaxHitPoints),
            Mana = MergeVital(current.Character.Mana, vitals.Mana, vitals.MaxMana),
            Movement = MergeVital(current.Character.Movement, vitals.Movement, vitals.MaxMovement)
        };

        return CharacterEquivalent(next, current.Character) ? current : current with { Character = next };
    }

    private static StateSnapshot ReducePosition(StateSnapshot current, CharacterPositionObserved observed)
    {
        if (string.Equals(current.Character.Position, observed.Position, StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        return current with
        {
            Character = current.Character with { Position = observed.Position }
        };
    }

    private static StateSnapshot ReducePrompt(StateSnapshot current, CharacterPromptObserved prompt)
    {
        CharacterState character = current.Character with
        {
            HitPoints = new VitalState(prompt.HitPoints, prompt.MaxHitPoints),
            Mana = new VitalState(prompt.Mana, prompt.MaxMana),
            Movement = new VitalState(prompt.Movement, prompt.MaxMovement),
            Experience = prompt.Experience,
            ExperienceToLevel = prompt.ExperienceToLevel,
            Position = prompt.Position
        };

        bool namedRoomChanged = !string.Equals(prompt.RoomName, current.Room.Name, StringComparison.Ordinal);
        bool movementAwaitingObservation = current.World.PendingDirection is not null;
        bool invalidateContents = namedRoomChanged || movementAwaitingObservation;

        RoomState room = current.Room with
        {
            Name = prompt.RoomName,
            Exits = new ExitState(
                prompt.Exits.IsKnown,
                Copy(prompt.Exits.Directions),
                current.Room.Exits.Details),
            Terrain = prompt.Terrain,
            Light = prompt.Light,
            ContentsCompleteness = invalidateContents
                ? ObservationCompleteness.Unknown
                : current.Room.ContentsCompleteness,
            Contents = invalidateContents ? EmptyRoomContents() : current.Room.Contents,
            RecentObservations = invalidateContents ? Array.Empty<string>() : current.Room.RecentObservations
        };

        if (CharacterEquivalent(character, current.Character) && RoomEquivalent(room, current.Room))
        {
            return current;
        }

        return current with { Character = character, Room = room };
    }

    private static StateSnapshot ReduceScore(StateSnapshot current, CharacterScoreObserved score)
    {
        CharacterProfileState profile = MergeProfile(current.Character.Profile, score.Profile);
        CharacterCombatStats combatStats = MergeCombatStats(current.Character.CombatStats, score.CombatStats);
        InventorySummary inventory = MergeInventory(current.Character.Inventory, score.Inventory);
        IReadOnlyDictionary<string, AttributeScore> attributes =
            score.Attributes.Count > 0 ? Copy(score.Attributes) : current.Character.Attributes;
        IReadOnlyList<string> effects =
            score.Effects is null ? current.Character.Effects : Copy(score.Effects);
        IReadOnlyList<string> conditions = score.Conditions is null
            ? current.Character.Conditions
            : MergeStrings(current.Character.Conditions, score.Conditions);

        CharacterState next = current.Character with
        {
            Profile = profile,
            Attributes = attributes,
            HitPoints = MergeVital(current.Character.HitPoints, score.HitPoints.Current, score.HitPoints.Maximum),
            Mana = MergeVital(current.Character.Mana, score.Mana.Current, score.Mana.Maximum),
            Movement = MergeVital(current.Character.Movement, score.Movement.Current, score.Movement.Maximum),
            Experience = score.Experience ?? current.Character.Experience,
            ExperienceToLevel = score.ExperienceToLevel ?? current.Character.ExperienceToLevel,
            Exploration = score.Exploration ?? current.Character.Exploration,
            CombatStats = combatStats,
            Inventory = inventory,
            Effects = effects,
            Conditions = conditions
        };

        return CharacterEquivalent(current.Character, next)
            ? current
            : current with { Character = next };
    }

    private static StateSnapshot ReduceSkills(
        StateSnapshot current,
        SkillsSnapshotObserved observed,
        DateTimeOffset observedAt)
    {
        IReadOnlyList<SkillState> skills = observed.Completeness == ObservationCompleteness.Complete
            ? new ReadOnlyCollection<SkillState>(
                observed.Skills.Select(skill => skill with { IsFresh = true }).ToArray())
            : MergePartialSkills(current.Character.Skills, observed.Skills);
        CharacterState next = current.Character with
        {
            Skills = skills,
            SkillsCompleteness = observed.Completeness,
            SkillsObservedAt = observedAt
        };
        return CharacterEquivalent(current.Character, next) ? current : current with { Character = next };
    }

    private static StateSnapshot ReduceSpells(
        StateSnapshot current,
        SpellsSnapshotObserved observed,
        DateTimeOffset observedAt)
    {
        IReadOnlyList<SpellState> spells = observed.Completeness == ObservationCompleteness.Complete
            ? new ReadOnlyCollection<SpellState>(
                observed.Spells.Select(spell => spell with { IsFresh = true }).ToArray())
            : MergePartialSpells(current.Character.Spells, observed.Spells);
        CharacterState next = current.Character with
        {
            Spells = spells,
            SpellsCompleteness = observed.Completeness,
            SpellsObservedAt = observedAt
        };
        return CharacterEquivalent(current.Character, next) ? current : current with { Character = next };
    }

    private static IReadOnlyList<SkillState> MergePartialSkills(
        IReadOnlyList<SkillState> current,
        IReadOnlyList<SkillState> observed)
    {
        Dictionary<(string Name, int Level), SkillState> merged = current
            .ToDictionary(skill => (skill.Name.ToUpperInvariant(), skill.RequiredLevel));
        foreach (SkillState skill in observed)
        {
            merged[(skill.Name.ToUpperInvariant(), skill.RequiredLevel)] = skill with { IsFresh = true };
        }
        return new ReadOnlyCollection<SkillState>(merged.Values
            .OrderBy(skill => skill.RequiredLevel)
            .ThenBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static IReadOnlyList<SpellState> MergePartialSpells(
        IReadOnlyList<SpellState> current,
        IReadOnlyList<SpellState> observed)
    {
        Dictionary<(string Name, int Level), SpellState> merged = current
            .ToDictionary(spell => (spell.Name.ToUpperInvariant(), spell.RequiredLevel));
        foreach (SpellState spell in observed)
        {
            merged[(spell.Name.ToUpperInvariant(), spell.RequiredLevel)] = spell with { IsFresh = true };
        }
        return new ReadOnlyCollection<SpellState>(merged.Values
            .OrderBy(spell => spell.RequiredLevel)
            .ThenBy(spell => spell.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    private static StateSnapshot ReduceSkillStale(StateSnapshot current, string skillName)
    {
        SkillState[] updated = current.Character.Skills.ToArray();
        int index = Array.FindIndex(updated, skill => skill.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || !updated[index].IsFresh)
        {
            return current;
        }

        updated[index] = updated[index] with { IsFresh = false };
        return current with
        {
            Character = current.Character with
            {
                Skills = new ReadOnlyCollection<SkillState>(updated)
            }
        };
    }

    private static StateSnapshot ReduceEquipment(StateSnapshot current, EquipmentChanged changed)
    {
        string slot = NormalizeEquipmentSlot(changed.Slot);
        EquipmentState equipment = current.Character.Equipment;
        string? mainHand = equipment.MainHand;
        Dictionary<string, string> worn = new(equipment.Worn, StringComparer.OrdinalIgnoreCase);
        List<EquipmentSlotState> slots = equipment.Slots.ToList();

        if (slot.Equals("wielded", StringComparison.OrdinalIgnoreCase))
        {
            bool slotChanged = SetSlot(slots, slot, changed.Item);
            if (string.Equals(mainHand, changed.Item, StringComparison.OrdinalIgnoreCase) && !slotChanged)
            {
                return current;
            }
            mainHand = changed.Item;
        }
        else
        {
            if (changed.Item is null)
            {
                worn.Remove(slot);
            }
            else
            {
                worn[slot] = changed.Item;
            }
            SetSlot(slots, slot, changed.Item);
        }

        EquipmentState next = new(mainHand, new ReadOnlyDictionary<string, string>(worn))
        {
            Slots = new ReadOnlyCollection<EquipmentSlotState>(slots),
            Completeness = equipment.Completeness,
            IdentifiedItems = equipment.IdentifiedItems
        };
        if (EquipmentEqual(equipment, next))
        {
            return current;
        }

        return current with
        {
            Character = current.Character with { Equipment = next }
        };
    }

    private static StateSnapshot ReduceEquipmentSnapshot(StateSnapshot current, EquipmentSnapshotObserved observed)
    {
        IReadOnlyList<EquipmentSlotState> slots = observed.Slots
            .Select(slot => slot with { Slot = NormalizeEquipmentSlot(slot.Slot) })
            .ToArray();
        string? mainHand = slots.FirstOrDefault(slot =>
            slot.Slot.Equals("wielded", StringComparison.OrdinalIgnoreCase))?.Item;

        Dictionary<string, string> worn = new(StringComparer.OrdinalIgnoreCase);
        foreach (EquipmentSlotState slot in slots.Where(slot => !slot.IsEmpty &&
                     !slot.Slot.Equals("wielded", StringComparison.OrdinalIgnoreCase)))
        {
            string key = slot.Ordinal > 1 ? $"{slot.Slot} #{slot.Ordinal}" : slot.Slot;
            worn[key] = slot.Item!;
        }

        EquipmentState next = new(mainHand, new ReadOnlyDictionary<string, string>(worn))
        {
            Slots = slots,
            Completeness = observed.Completeness,
            IdentifiedItems = current.Character.Equipment.IdentifiedItems
        };
        if (EquipmentEqual(current.Character.Equipment, next))
        {
            return current;
        }

        return current with
        {
            Character = current.Character with { Equipment = next }
        };
    }

    private static StateSnapshot ReduceInventorySnapshot(StateSnapshot current, InventorySnapshotObserved observed)
    {
        string[] items = observed.Items
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .ToArray();

        if (current.Character.InventoryCompleteness == observed.Completeness &&
            current.Character.Inventory.Items == items.Length &&
            current.Character.CarriedItems.SequenceEqual(items, StringComparer.OrdinalIgnoreCase))
        {
            return current;
        }

        InventorySummary inventory = current.Character.Inventory with { Items = items.Length };
        return current with
        {
            Character = current.Character with
            {
                Inventory = inventory,
                CarriedItems = new ReadOnlyCollection<string>(items),
                InventoryCompleteness = observed.Completeness
            }
        };
    }

    private static StateSnapshot ReduceItemIdentification(StateSnapshot current, ItemIdentification item)
    {
        EquipmentState equipment = current.Character.Equipment;
        bool relevant = equipment.Slots.Any(slot =>
            !slot.IsEmpty && string.Equals(slot.Item, item.Name, StringComparison.OrdinalIgnoreCase));
        if (!relevant)
        {
            return current;
        }

        Dictionary<string, ItemIdentification> identified = new(equipment.IdentifiedItems, StringComparer.OrdinalIgnoreCase)
        {
            [item.Name] = item
        };
        EquipmentState next = equipment with
        {
            IdentifiedItems = new ReadOnlyDictionary<string, ItemIdentification>(identified)
        };
        return current with { Character = current.Character with { Equipment = next } };
    }

    private static bool SetSlot(List<EquipmentSlotState> slots, string slot, string? item)
    {
        List<int> matching = [];
        for (int index = 0; index < slots.Count; index++)
        {
            if (slots[index].Slot.Equals(slot, StringComparison.OrdinalIgnoreCase))
            {
                matching.Add(index);
            }
        }

        int target = item is null
            ? matching.FirstOrDefault(index => !slots[index].IsEmpty, -1)
            : matching.FirstOrDefault(index => slots[index].IsEmpty, -1);
        if (target < 0 && matching.Count > 0)
        {
            target = matching[0];
        }

        if (target >= 0)
        {
            if (string.Equals(slots[target].Item, item, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            slots[target] = slots[target] with { Item = item };
            return true;
        }

        int ordinal = matching.Count + 1;
        slots.Add(new EquipmentSlotState(slot, ordinal, item));
        return true;
    }

    private static string NormalizeEquipmentSlot(string slot) =>
        string.Join(" ", slot.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static StateSnapshot ReduceCharacterCondition(StateSnapshot current, CharacterConditionChanged changed)
    {
        IReadOnlyList<string> conditions = ApplyNamedState(current.Character.Conditions, changed.Condition, changed.Active);
        if (SequenceEqual(current.Character.Conditions, conditions))
        {
            return current;
        }

        return current with { Character = current.Character with { Conditions = conditions } };
    }

    private static StateSnapshot ReduceRoom(StateSnapshot current, RoomChanged room)
    {
        bool sameIdentity = string.Equals(room.Id, current.Room.Id, StringComparison.Ordinal);
        RoomState next = current.Room with
        {
            Id = room.Id,
            Name = room.Name,
            Exits = new ExitState(true, Copy(room.Exits), current.Room.Exits.Details),
            ContentsCompleteness = sameIdentity ? current.Room.ContentsCompleteness : ObservationCompleteness.Unknown,
            Contents = sameIdentity ? current.Room.Contents : EmptyRoomContents(),
            RecentObservations = sameIdentity ? current.Room.RecentObservations : Array.Empty<string>()
        };

        return RoomEquivalent(current.Room, next) ? current : current with { Room = next };
    }

    private static StateSnapshot ReduceRoomObservation(
        StateSnapshot current,
        RoomObservationObserved observed,
        DateTimeOffset observedAt)
    {
        IReadOnlyList<RoomExitObservation> exitDetails = Copy(observed.ExitDetails);
        IReadOnlyList<string> observedDirections = observed.ExitDetails
            .Where(exit => exit.Exists)
            .Select(exit => exit.Direction)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ExitState exits = new(
            IsKnown: true,
            Directions: observedDirections.Count > 0 ? Copy(observedDirections) : Copy(current.Room.Exits.Directions),
            Details: exitDetails);
        string resolvedRoomId = ResolveRoomId(current, observed);

        RoomState room = new(
            resolvedRoomId,
            observed.RoomName,
            observed.DescriptionFingerprint,
            observed.Description,
            exits,
            current.Room.Terrain,
            current.Room.Light,
            observed.ContentsCompleteness,
            Copy(observed.Contents))
        {
            RecentObservations = Copy(observed.RecentObservations)
        };

        WorldMapState world = UpdateWorldMap(current, observed, resolvedRoomId, observedAt);
        bool roomChanged = !RoomEquivalent(current.Room, room);
        bool worldChanged = !WorldEquivalent(current.World, world);

        if (!roomChanged && !worldChanged)
        {
            return current;
        }

        return current with { Room = room, World = world };
    }

    private static string ResolveRoomId(StateSnapshot current, RoomObservationObserved observed)
    {
        if (current.Room.Id is not null &&
            current.World.PendingDirection is null &&
            string.Equals(current.Room.DescriptionFingerprint, observed.DescriptionFingerprint, StringComparison.Ordinal))
        {
            return current.Room.Id;
        }

        if (current.Room.Id is not null && current.World.PendingDirection is not null)
        {
            RoomEdgeState? knownEdge = current.World.Edges.FirstOrDefault(edge =>
                edge.FromRoomId == current.Room.Id &&
                edge.Direction.Equals(current.World.PendingDirection, StringComparison.OrdinalIgnoreCase));
            if (knownEdge is not null &&
                current.World.Rooms.TryGetValue(knownEdge.ToRoomId, out RoomNodeState? knownTarget) &&
                string.Equals(knownTarget.DescriptionFingerprint, observed.DescriptionFingerprint, StringComparison.Ordinal))
            {
                return knownTarget.Id;
            }
        }

        RoomNodeState[] fingerprintMatches = current.World.Rooms.Values
            .Where(room =>
                string.Equals(room.Name, observed.RoomName, StringComparison.Ordinal) &&
                string.Equals(room.DescriptionFingerprint, observed.DescriptionFingerprint, StringComparison.Ordinal))
            .ToArray();
        if (fingerprintMatches.Length == 1)
        {
            return fingerprintMatches[0].Id;
        }

        if (!current.World.Rooms.ContainsKey(observed.RoomId))
        {
            return observed.RoomId;
        }

        int suffix = 2;
        string candidate;
        do
        {
            candidate = $"{observed.RoomId}~{suffix++}";
        }
        while (current.World.Rooms.ContainsKey(candidate));

        return candidate;
    }

    private static WorldMapState UpdateWorldMap(
        StateSnapshot current,
        RoomObservationObserved observed,
        string resolvedRoomId,
        DateTimeOffset observedAt)
    {
        string? previousRoomId = current.Room.Id;
        string? pendingDirection = current.World.PendingDirection;
        Dictionary<string, RoomNodeState> rooms = new(current.World.Rooms, StringComparer.Ordinal);
        RoomNodeState candidate = new(
            resolvedRoomId,
            observed.RoomName,
            observed.DescriptionFingerprint,
            observed.Description,
            Copy(observed.ExitDetails),
            observedAt);

        if (!rooms.TryGetValue(resolvedRoomId, out RoomNodeState? existing) || !RoomNodeEquivalent(existing, candidate))
        {
            rooms[resolvedRoomId] = candidate;
        }

        List<RoomEdgeState> edges = current.World.Edges.ToList();
        if (previousRoomId is not null &&
            !string.Equals(previousRoomId, resolvedRoomId, StringComparison.Ordinal) &&
            pendingDirection is not null &&
            !edges.Any(edge =>
                edge.FromRoomId == previousRoomId &&
                edge.ToRoomId == resolvedRoomId &&
                edge.Direction.Equals(pendingDirection, StringComparison.OrdinalIgnoreCase)))
        {
            edges.Add(new RoomEdgeState(
                previousRoomId,
                pendingDirection,
                resolvedRoomId,
                observedAt));
        }

        IReadOnlyList<string> remainingPending = current.World.PendingDirections.Count <= 1
            ? new ReadOnlyCollection<string>(Array.Empty<string>())
            : new ReadOnlyCollection<string>(current.World.PendingDirections.Skip(1).ToArray());

        return new WorldMapState(
            new ReadOnlyDictionary<string, RoomNodeState>(rooms),
            new ReadOnlyCollection<RoomEdgeState>(edges.ToArray()),
            remainingPending);
    }

    private static StateSnapshot ReduceLegacyRoomContents(StateSnapshot current, RoomContentsObserved observed)
    {
        if (!string.Equals(current.Room.Name, observed.RoomName, StringComparison.Ordinal))
        {
            return current;
        }

        List<RoomContentObservation> contents = [];
        contents.AddRange(observed.Occupants.Select(item => item with { Kind = RoomEntityKind.Occupant }));
        contents.AddRange(observed.Objects.Select(item => item with { Kind = RoomEntityKind.Object }));
        contents.AddRange(observed.UnknownContents.Select(item => item with { Kind = RoomEntityKind.Unknown }));

        RoomState next = current.Room with
        {
            ContentsCompleteness = ObservationCompleteness.Complete,
            Contents = new ReadOnlyCollection<RoomContentObservation>(contents.ToArray())
        };

        return RoomEquivalent(current.Room, next) ? current : current with { Room = next };
    }

    private static StateSnapshot ReduceRoomOccupantDeparted(StateSnapshot current, RoomOccupantDeparted departed)
    {
        IReadOnlyList<RoomContentObservation> contents = RemoveFirstMatchingOccupant(current.Room.Contents, departed.TargetName);
        if (ReferenceEquals(contents, current.Room.Contents))
        {
            return current;
        }

        return current with { Room = current.Room with { Contents = contents, ContentsCompleteness = ObservationCompleteness.Partial } };
    }

    private static StateSnapshot ReduceNavigationAttempted(StateSnapshot current, NavigationAttempted navigation)
    {
        string[] pending = [.. current.World.PendingDirections, navigation.Direction];
        return current with
        {
            World = current.World with
            {
                PendingDirections = new ReadOnlyCollection<string>(pending)
            }
        };
    }

    private static StateSnapshot ReduceNavigationResponseCompleted(StateSnapshot current, NavigationResponseCompleted response)
    {
        if (response.RoomObserved || current.World.PendingDirections.Count == 0) return current;

        IReadOnlyList<string> remaining = RemovePendingDirection(current.World.PendingDirections, response.Direction);
        if (SequenceEqual(current.World.PendingDirections, remaining)) return current;
        return current with { World = current.World with { PendingDirections = remaining } };
    }

    private static StateSnapshot ReduceNavigationFailed(StateSnapshot current, NavigationFailed failed)
    {
        string? direction = failed.Direction ?? current.World.PendingDirection;
        IReadOnlyList<string> remaining = RemovePendingDirection(current.World.PendingDirections, direction);
        WorldMapState world = SequenceEqual(current.World.PendingDirections, remaining)
            ? current.World
            : current.World with { PendingDirections = remaining };

        ExitDoorState doorState = failed.Reason.Contains("locked", StringComparison.OrdinalIgnoreCase)
            ? ExitDoorState.Locked
            : failed.Reason.Contains("closed", StringComparison.OrdinalIgnoreCase)
                ? ExitDoorState.Closed
                : ExitDoorState.Unknown;
        RoomState room = direction is null
            ? current.Room
            : ApplyExitState(
                current.Room,
                direction,
                doorState,
                ExitTraversability.Blocked,
                failed.Reason);

        if (ReferenceEquals(world, current.World) && ReferenceEquals(room, current.Room))
        {
            return current;
        }

        return current with { World = world, Room = room };
    }

    private static StateSnapshot ReduceRoomExitStateChanged(StateSnapshot current, RoomExitStateChanged changed)
    {
        RoomState room = ApplyExitState(
            current.Room,
            changed.Direction,
            changed.DoorState,
            changed.Traversability,
            changed.BlockReason);
        return ReferenceEquals(room, current.Room) ? current : current with { Room = room };
    }

    private static RoomState ApplyExitState(
        RoomState current,
        string direction,
        ExitDoorState doorState,
        ExitTraversability traversability,
        string? blockReason)
    {
        List<RoomExitObservation> details = current.Exits.Details?.ToList() ?? [];
        int index = details.FindIndex(exit => exit.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase));
        RoomExitObservation next = new(direction, true, doorState, traversability, blockReason);

        if (index >= 0)
        {
            RoomExitObservation existing = details[index];
            next = new RoomExitObservation(
                existing.Direction,
                true,
                doorState == ExitDoorState.Unknown ? existing.DoorState : doorState,
                traversability,
                blockReason);
            if (existing == next)
            {
                return current;
            }
            details[index] = next;
        }
        else
        {
            details.Add(next);
        }

        List<string> traversable = current.Exits.Directions.ToList();
        bool contains = traversable.Any(value => value.Equals(direction, StringComparison.OrdinalIgnoreCase));
        if (traversability == ExitTraversability.Traversable && !contains)
        {
            traversable.Add(direction);
        }
        else if (traversability == ExitTraversability.Blocked && contains)
        {
            traversable.RemoveAll(value => value.Equals(direction, StringComparison.OrdinalIgnoreCase));
        }

        return current with
        {
            Exits = new ExitState(
                true,
                new ReadOnlyCollection<string>(traversable.ToArray()),
                new ReadOnlyCollection<RoomExitObservation>(details.ToArray()))
        };
    }

    private static StateSnapshot ReduceCombatState(StateSnapshot current, CombatStateChanged combat)
    {
        bool targetChanged = !string.Equals(current.Combat.TargetId, combat.TargetId, StringComparison.Ordinal);
        CombatState next = current.Combat with
        {
            Active = combat.Active,
            TargetId = combat.Active ? combat.TargetId : null,
            TargetName = combat.Active && !targetChanged ? current.Combat.TargetName : null,
            TargetCondition = combat.Active && !targetChanged ? current.Combat.TargetCondition : null,
            LastPlayerDamage = combat.Active && !targetChanged ? current.Combat.LastPlayerDamage : null,
            LastOpponentDamage = combat.Active && !targetChanged ? current.Combat.LastOpponentDamage : null,
            LastAvoidedAttack = combat.Active && !targetChanged ? current.Combat.LastAvoidedAttack : null
        };

        return next == current.Combat ? current : current with { Combat = next };
    }

    private static StateSnapshot ReduceDamage(StateSnapshot current, DamageObservation damage)
    {
        string targetName = damage.Source == CombatActor.Player ? damage.TargetName : damage.SourceName;
        CombatState next = current.Combat with
        {
            Active = true,
            TargetName = targetName,
            LastPlayerDamage = damage.Source == CombatActor.Player ? damage : current.Combat.LastPlayerDamage,
            LastOpponentDamage = damage.Source == CombatActor.Opponent ? damage : current.Combat.LastOpponentDamage
        };

        return next == current.Combat ? current : current with { Combat = next };
    }

    private static StateSnapshot ReduceAttack(StateSnapshot current, CombatAttackObservation attack)
    {
        string targetName = attack.Source == CombatActor.Player ? attack.TargetName : attack.SourceName;
        CombatState next = current.Combat with
        {
            Active = true,
            TargetName = targetName,
            LastAvoidedAttack = attack
        };
        return next == current.Combat ? current : current with { Combat = next };
    }

    private static StateSnapshot ReduceCondition(StateSnapshot current, CombatTargetConditionObserved condition)
    {
        CombatState next = current.Combat with
        {
            Active = true,
            TargetName = condition.TargetName,
            TargetCondition = condition.Condition
        };

        return next == current.Combat ? current : current with { Combat = next };
    }

    private static StateSnapshot ReduceKilled(StateSnapshot current, EnemyKilled killed)
    {
        IReadOnlyList<RoomContentObservation> contents = RemoveFirstMatchingOccupant(current.Room.Contents, killed.TargetName);
        RoomState room = ReferenceEquals(contents, current.Room.Contents)
            ? current.Room
            : current.Room with { Contents = contents, ContentsCompleteness = ObservationCompleteness.Partial };

        bool combatMatches = current.Combat.TargetName is null ||
            string.Equals(current.Combat.TargetName, killed.TargetName, StringComparison.OrdinalIgnoreCase);

        CombatState combat = current.Combat;
        if (combatMatches &&
            (current.Combat.Active || current.Combat.TargetName is not null || current.Combat.TargetCondition is not null))
        {
            combat = current.Combat with
            {
                Active = false,
                TargetId = null,
                TargetName = null,
                TargetCondition = null
            };
        }

        if (ReferenceEquals(room, current.Room) && combat == current.Combat)
        {
            return current;
        }

        return current with { Room = room, Combat = combat };
    }

    private static StateSnapshot ReduceCorpseDestroyed(StateSnapshot current, CorpseDestroyed destroyed)
    {
        IReadOnlyList<RoomContentObservation> contents = RemoveFirstMatchingCorpse(current.Room.Contents, destroyed.TargetName);
        if (ReferenceEquals(contents, current.Room.Contents))
        {
            return current;
        }

        return current with
        {
            Room = current.Room with
            {
                Contents = contents,
                ContentsCompleteness = ObservationCompleteness.Partial
            }
        };
    }

    private static StateSnapshot ReduceEffect(StateSnapshot current, EffectStateChanged effect)
    {
        IReadOnlyList<string> effects = ApplyNamedState(current.Character.Effects, effect.Effect, effect.Active);
        if (SequenceEqual(current.Character.Effects, effects))
        {
            return current;
        }

        return current with { Character = current.Character with { Effects = effects } };
    }

    private static StateSnapshot ReduceExperienceGained(StateSnapshot current, ExperienceGained gained)
    {
        if (current.Character.Experience is null)
        {
            return current;
        }

        long nextExperience = checked(current.Character.Experience.Value + gained.Amount);
        return current with { Character = current.Character with { Experience = nextExperience } };
    }

    private static StateSnapshot ReduceExplorationGained(StateSnapshot current, ExplorationGained gained)
    {
        if (current.Character.Exploration is null)
        {
            return current;
        }

        int nextExploration = checked(current.Character.Exploration.Value + gained.Amount);
        return current with { Character = current.Character with { Exploration = nextExploration } };
    }

    private static StateSnapshot ReduceCurrencyGained(StateSnapshot current, CurrencyGained gained)
    {
        InventorySummary inventory = current.Character.Inventory;
        InventorySummary next = gained.Currency.ToLowerInvariant() switch
        {
            "copper" when inventory.Copper is not null => inventory with { Copper = checked(inventory.Copper.Value + gained.Amount) },
            "silver" when inventory.Silver is not null => inventory with { Silver = checked(inventory.Silver.Value + gained.Amount) },
            "gold" when inventory.Gold is not null => inventory with { Gold = checked(inventory.Gold.Value + gained.Amount) },
            _ => inventory
        };

        return next == inventory
            ? current
            : current with { Character = current.Character with { Inventory = next } };
    }

    private static StateSnapshot ReducePromotion(StateSnapshot current, CharacterPromoted promotion)
    {
        CharacterProfileState profile = current.Character.Profile.Level is null
            ? current.Character.Profile
            : current.Character.Profile with { Level = checked(current.Character.Profile.Level.Value + 1) };

        CharacterState next = current.Character with
        {
            Profile = profile,
            HitPoints = AddMaximum(current.Character.HitPoints, promotion.HitPointGain),
            Mana = AddMaximum(current.Character.Mana, promotion.ManaGain),
            Movement = AddMaximum(current.Character.Movement, promotion.MovementGain)
        };

        return CharacterEquivalent(current.Character, next) ? current : current with { Character = next };
    }

    private static VitalState AddMaximum(VitalState vital, int amount) =>
        vital.Maximum is null ? vital : vital with { Maximum = checked(vital.Maximum.Value + amount) };

    private static VitalState MergeVital(VitalState current, int? currentValue, int? maximumValue) =>
        new(currentValue ?? current.Current, maximumValue ?? current.Maximum);

    private static CharacterProfileState MergeProfile(CharacterProfileState current, CharacterProfileState observed) =>
        new(
            observed.Name ?? current.Name,
            observed.Title ?? current.Title,
            observed.Lineage ?? current.Lineage,
            observed.ClassName ?? current.ClassName,
            observed.Level ?? current.Level,
            observed.Gender ?? current.Gender,
            observed.Age ?? current.Age,
            observed.AgeDescriptor ?? current.AgeDescriptor,
            observed.Hours ?? current.Hours,
            observed.Resonance ?? current.Resonance,
            observed.Alignment ?? current.Alignment);

    private static CharacterCombatStats MergeCombatStats(CharacterCombatStats current, CharacterCombatStats observed) =>
        new(
            observed.Hitroll ?? current.Hitroll,
            observed.Damroll ?? current.Damroll,
            observed.Saves ?? current.Saves,
            observed.ArmorClass ?? current.ArmorClass,
            observed.ArmorClassDescriptor ?? current.ArmorClassDescriptor);

    private static InventorySummary MergeInventory(InventorySummary current, InventorySummary observed) =>
        new(
            observed.Items ?? current.Items,
            observed.MaxItems ?? current.MaxItems,
            observed.Weight ?? current.Weight,
            observed.MaxWeight ?? current.MaxWeight,
            observed.Copper ?? current.Copper,
            observed.Silver ?? current.Silver,
            observed.Gold ?? current.Gold);

    private void PublishSnapshot(StateSnapshot snapshot)
    {
        lock (_subscriberLock)
        {
            foreach (Channel<StateSnapshot> subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(snapshot);
            }
        }
    }

    private void CompleteSubscribers()
    {
        lock (_subscriberLock)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            foreach (Channel<StateSnapshot> subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }
            _subscribers.Clear();
        }
    }

    private static IReadOnlyList<string> RemovePendingDirection(
        IReadOnlyList<string> pending,
        string? direction)
    {
        if (pending.Count == 0 || direction is null)
        {
            return pending;
        }

        int index = -1;
        for (int candidate = 0; candidate < pending.Count; candidate++)
        {
            if (pending[candidate].Equals(direction, StringComparison.OrdinalIgnoreCase))
            {
                index = candidate;
                break;
            }
        }

        if (index < 0)
        {
            return pending;
        }

        return new ReadOnlyCollection<string>(
            pending.Where((_, candidate) => candidate != index).ToArray());
    }

    private static IReadOnlyList<string> ApplyNamedState(IReadOnlyList<string> values, string value, bool active)
    {
        HashSet<string> updated = new(values, StringComparer.OrdinalIgnoreCase);
        if (active)
        {
            updated.Add(value);
        }
        else
        {
            updated.Remove(value);
        }

        return new ReadOnlyCollection<string>(updated.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IReadOnlyList<string> MergeStrings(IReadOnlyList<string> current, IReadOnlyList<string> observed)
    {
        HashSet<string> merged = new(current, StringComparer.OrdinalIgnoreCase);
        merged.UnionWith(observed);
        return new ReadOnlyCollection<string>(merged.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool CharacterEquivalent(CharacterState left, CharacterState right) =>
        left.HitPoints == right.HitPoints &&
        left.Mana == right.Mana &&
        left.Movement == right.Movement &&
        left.Experience == right.Experience &&
        left.ExperienceToLevel == right.ExperienceToLevel &&
        left.Exploration == right.Exploration &&
        string.Equals(left.Position, right.Position, StringComparison.Ordinal) &&
        left.Profile == right.Profile &&
        DictionaryEqual(left.Attributes, right.Attributes) &&
        left.CombatStats == right.CombatStats &&
        left.Inventory == right.Inventory &&
        SequenceEqual(left.Effects, right.Effects) &&
        SequenceEqual(left.Conditions, right.Conditions) &&
        SkillsEqual(left.Skills, right.Skills) &&
        SpellsEqual(left.Spells, right.Spells) &&
        left.SkillsCompleteness == right.SkillsCompleteness &&
        left.SpellsCompleteness == right.SpellsCompleteness &&
        left.SkillsObservedAt == right.SkillsObservedAt &&
        left.SpellsObservedAt == right.SpellsObservedAt &&
        EquipmentEqual(left.Equipment, right.Equipment);

    private static bool EquipmentEqual(EquipmentState left, EquipmentState right) =>
        string.Equals(left.MainHand, right.MainHand, StringComparison.OrdinalIgnoreCase) &&
        DictionaryEqual(left.Worn, right.Worn) &&
        left.Completeness == right.Completeness &&
        EquipmentSlotsEqual(left.Slots, right.Slots);

    private static bool EquipmentSlotsEqual(
        IReadOnlyList<EquipmentSlotState> left,
        IReadOnlyList<EquipmentSlotState> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!left[index].Slot.Equals(right[index].Slot, StringComparison.OrdinalIgnoreCase) ||
                left[index].Ordinal != right[index].Ordinal ||
                !string.Equals(left[index].Item, right[index].Item, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static bool RoomEquivalent(RoomState left, RoomState right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        string.Equals(left.DescriptionFingerprint, right.DescriptionFingerprint, StringComparison.Ordinal) &&
        string.Equals(left.Description, right.Description, StringComparison.Ordinal) &&
        ExitEquals(left.Exits, right.Exits) &&
        string.Equals(left.Terrain, right.Terrain, StringComparison.Ordinal) &&
        string.Equals(left.Light, right.Light, StringComparison.Ordinal) &&
        left.ContentsCompleteness == right.ContentsCompleteness &&
        RoomContentsEqual(left.Contents, right.Contents) &&
        SequenceEqual(left.RecentObservations, right.RecentObservations);

    private static bool WorldEquivalent(WorldMapState left, WorldMapState right) =>
        SequenceEqual(left.PendingDirections, right.PendingDirections) &&
        RoomNodeDictionaryEqual(left.Rooms, right.Rooms) &&
        RoomEdgesEqual(left.Edges, right.Edges);

    private static bool RoomNodeDictionaryEqual(
        IReadOnlyDictionary<string, RoomNodeState> left,
        IReadOnlyDictionary<string, RoomNodeState> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, RoomNodeState> pair in left)
        {
            if (!right.TryGetValue(pair.Key, out RoomNodeState? node) || !RoomNodeEquivalent(pair.Value, node))
            {
                return false;
            }
        }
        return true;
    }

    private static bool RoomNodeEquivalent(RoomNodeState left, RoomNodeState right) =>
        left.Id == right.Id &&
        left.Name == right.Name &&
        left.DescriptionFingerprint == right.DescriptionFingerprint &&
        left.Description == right.Description &&
        RoomExitDetailsEqual(left.Exits, right.Exits);

    private static bool RoomEdgesEqual(IReadOnlyList<RoomEdgeState> left, IReadOnlyList<RoomEdgeState> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (left[index].FromRoomId != right[index].FromRoomId ||
                left[index].ToRoomId != right[index].ToRoomId ||
                !left[index].Direction.Equals(right[index].Direction, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ExitEquals(ExitState left, ExitState right) =>
        left.IsKnown == right.IsKnown &&
        SequenceEqual(left.Directions, right.Directions) &&
        RoomExitDetailsEqual(left.Details ?? Array.Empty<RoomExitObservation>(), right.Details ?? Array.Empty<RoomExitObservation>());

    private static bool RoomExitDetailsEqual(
        IReadOnlyList<RoomExitObservation> left,
        IReadOnlyList<RoomExitObservation> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<RoomContentObservation> RemoveFirstMatchingOccupant(
        IReadOnlyList<RoomContentObservation> contents,
        string targetName)
    {
        int index = -1;
        for (int candidate = 0; candidate < contents.Count; candidate++)
        {
            if (contents[candidate].Kind == RoomEntityKind.Occupant && MatchesOccupant(contents[candidate], targetName))
            {
                index = candidate;
                break;
            }
        }

        if (index < 0)
        {
            return contents;
        }

        RoomContentObservation[] updated = new RoomContentObservation[contents.Count - 1];
        int destination = 0;
        for (int source = 0; source < contents.Count; source++)
        {
            if (source == index)
            {
                continue;
            }
            updated[destination++] = contents[source];
        }

        return new ReadOnlyCollection<RoomContentObservation>(updated);
    }

    private static IReadOnlyList<RoomContentObservation> RemoveFirstMatchingCorpse(
        IReadOnlyList<RoomContentObservation> contents,
        string targetName)
    {
        string normalizedTarget = targetName.Trim();
        int index = -1;
        for (int candidate = 0; candidate < contents.Count; candidate++)
        {
            RoomContentObservation content = contents[candidate];
            if (content.Kind != RoomEntityKind.Corpse)
            {
                continue;
            }

            string description = StripDecorators(content.Description);
            if (description.Contains($"corpse of {normalizedTarget}", StringComparison.OrdinalIgnoreCase) ||
                description.Contains($"{normalizedTarget}'s corpse", StringComparison.OrdinalIgnoreCase))
            {
                index = candidate;
                break;
            }
        }

        if (index < 0)
        {
            return contents;
        }

        RoomContentObservation[] updated = new RoomContentObservation[contents.Count - 1];
        int destination = 0;
        for (int source = 0; source < contents.Count; source++)
        {
            if (source == index)
            {
                continue;
            }
            updated[destination++] = contents[source];
        }

        return new ReadOnlyCollection<RoomContentObservation>(updated);
    }

    private static bool MatchesOccupant(RoomContentObservation occupant, string targetName)
    {
        if (!string.IsNullOrWhiteSpace(occupant.CanonicalName) &&
            string.Equals(occupant.CanonicalName, targetName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string description = StripDecorators(occupant.Description);
        return string.Equals(description, targetName, StringComparison.OrdinalIgnoreCase) ||
            description.StartsWith(targetName + " ", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripDecorators(string description)
    {
        string remaining = description.TrimStart();
        while (remaining.StartsWith('('))
        {
            int close = remaining.IndexOf(')');
            if (close < 0)
            {
                break;
            }
            remaining = remaining[(close + 1)..].TrimStart();
        }
        return remaining;
    }

    private static bool RoomContentsEqual(
        IReadOnlyList<RoomContentObservation> left,
        IReadOnlyList<RoomContentObservation> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            RoomContentObservation l = left[index];
            RoomContentObservation r = right[index];
            if (l.Description != r.Description ||
                l.CanonicalName != r.CanonicalName ||
                l.Kind != r.Kind ||
                l.Traits != r.Traits ||
                !NullableSequenceEqual(l.TargetKeywords, r.TargetKeywords) ||
                !NullableSequenceEqual(l.Decorators, r.Decorators))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SkillsEqual(IReadOnlyList<SkillState> left, IReadOnlyList<SkillState> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }
        return true;
    }

    private static bool SpellsEqual(IReadOnlyList<SpellState> left, IReadOnlyList<SpellState> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }
        return true;
    }

    private static bool NullableSequenceEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }
        return SequenceEqual(left, right);
    }

    private static bool SequenceEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count &&
        left.Zip(right).All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));

    private static bool DictionaryEqual<T>(IReadOnlyDictionary<string, T> left, IReadOnlyDictionary<string, T> right)
        where T : notnull
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, T> pair in left)
        {
            if (!right.TryGetValue(pair.Key, out T? value) || !EqualityComparer<T>.Default.Equals(value, pair.Value))
            {
                return false;
            }
        }
        return true;
    }

    private static IReadOnlyList<string> Copy(IReadOnlyList<string> source) =>
        new ReadOnlyCollection<string>(source.ToArray());

    private static IReadOnlyList<RoomExitObservation> Copy(IReadOnlyList<RoomExitObservation> source) =>
        new ReadOnlyCollection<RoomExitObservation>(source.ToArray());

    private static IReadOnlyList<RoomContentObservation> Copy(IReadOnlyList<RoomContentObservation> source) =>
        new ReadOnlyCollection<RoomContentObservation>(source.ToArray());

    private static IReadOnlyList<RoomContentObservation> EmptyRoomContents() =>
        new ReadOnlyCollection<RoomContentObservation>(Array.Empty<RoomContentObservation>());

    private static IReadOnlyDictionary<string, AttributeScore> Copy(
        IReadOnlyDictionary<string, AttributeScore> source) =>
        new ReadOnlyDictionary<string, AttributeScore>(
            source.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
}
