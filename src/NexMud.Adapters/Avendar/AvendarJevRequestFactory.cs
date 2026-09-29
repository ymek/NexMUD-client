using NexMud.Contracts.Jev;
using NexMud.Contracts.State;

namespace NexMud.Adapters.Avendar;

public sealed record AvendarAutonomyConstraints(
    IReadOnlySet<string> SuppressedActions,
    string? ImmediateBacktrackDirection,
    IReadOnlyList<string> RecentRoomIds,
    IReadOnlySet<string> ProtectedTargets)
{
    public static AvendarAutonomyConstraints Empty { get; } =
        new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            null,
            Array.Empty<string>(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    public bool IsSuppressed(string actionId) => SuppressedActions.Contains(actionId);
}


/// <summary>
/// Builds compact, deterministic Jev requests from modeled Avendar state.
/// Jev receives explicit facts and only actions which can be revalidated immediately before dispatch.
/// </summary>
public static class AvendarJevRequestFactory
{
    public static bool TryCreateCombatRequest(StateSnapshot state, out JevChoiceRequest? request) =>
        TryCreateCombatRequest(state, null, AvendarAutonomyConstraints.Empty, out request);

    public static bool TryCreateCombatRequest(
        StateSnapshot state,
        object? persistentContext,
        out JevChoiceRequest? request) =>
        TryCreateCombatRequest(state, persistentContext, AvendarAutonomyConstraints.Empty, out request);

    public static bool TryCreateCombatRequest(
        StateSnapshot state,
        object? persistentContext,
        AvendarAutonomyConstraints constraints,
        out JevChoiceRequest? request)
    {
        ArgumentNullException.ThrowIfNull(state);
        request = null;

        if (state.Session.InputMode != SessionInputMode.Normal)
        {
            return false;
        }

        Dictionary<string, string?> criteria = new(StringComparer.OrdinalIgnoreCase)
        {
            ["continue"] = state.Combat.Active
                ? "Issue no additional command and allow Avendar's automatic combat round to continue."
                : "Do not initiate combat with anyone currently observed in the room."
        };

        if (state.Combat.Active)
        {
            bool hasTarget = !string.IsNullOrWhiteSpace(state.Combat.TargetName ?? state.Combat.TargetId);
            foreach (SkillActionDefinition definition in AvendarActionCapabilities.KnownActions.Values)
            {
                if (!IsCombatRelevant(definition.SkillName))
                {
                    continue;
                }

                ActionEligibilityResult eligibility = AvendarActionCapabilities.Evaluate(
                    definition,
                    state,
                    hasTarget,
                    equippedWeaponIsBlunt: EquippedWeaponIsBlunt(state));
                if (eligibility.Eligibility == ActionEligibility.Eligible &&
                    !constraints.IsSuppressed(definition.DecisionAction))
                {
                    criteria[definition.DecisionAction] = definition.ExpectedEffect;
                }
            }
        }
        else
        {
            // Target acquisition is never offered while the character is depleted or unable to
            // stand. Recovery owns that state transition before Combat can start another fight.
            if (NeedsRecoveryForTravel(state) || !IsStanding(state.Character.Position))
            {
                return false;
            }

            foreach (RoomContentObservation occupant in state.Room.Occupants.Take(8))
            {
                string? target = EntityTarget(occupant);
                if (string.IsNullOrWhiteSpace(target) || IsProtectedTarget(occupant, constraints.ProtectedTargets))
                {
                    continue;
                }

                string actionId = $"engage:{Uri.EscapeDataString(target)}";
                if (constraints.IsSuppressed(actionId))
                {
                    continue;
                }

                criteria[actionId] =
                    $"Initiate combat with {occupant.Description}. Use only when this is an appropriate grind or PK target.";
            }
        }

        if (criteria.Count < 2)
        {
            return false;
        }

        JevAuthority authority = AuthorityFor(state, JevDomain.Combat);
        request = new JevChoiceRequest(
            state.Version,
            JevDomain.Combat,
            state.Combat.Active
                ? "Select the best immediate legal combat action. Choose continue when an extra command is not worth its lag, cost, or risk."
                : "Decide whether to initiate a fight with an observed occupant. Prefer continue when target suitability is uncertain.",
            BuildProjection(state, persistentContext, constraints),
            criteria,
            authority,
            DecisionSource.Jev,
            [
                JevAuxiliaryQuestion.Score(
                    "danger",
                    "Rate the player's immediate combat danger using this ordered scale.",
                    "stable: current pressure is manageable",
                    "pressured: meaningful disadvantage or incoming pressure",
                    "danger: survival is at material risk without adjustment",
                    "critical: immediate defensive or escape priority"),
                JevAuxiliaryQuestion.Noul(
                    "disengage",
                    "Should survival take priority over maintaining offensive pressure right now?",
                    "Yes when disengaging, recovering, or reducing exposure should dominate the next decision.",
                    "No when continuing normal offensive pressure is reasonable.")
            ]);
        return true;
    }

    public static bool TryCreateRecoveryRequest(
        StateSnapshot state,
        object? persistentContext,
        out JevChoiceRequest? request) =>
        TryCreateRecoveryRequest(state, persistentContext, AvendarAutonomyConstraints.Empty, out request);

    public static bool TryCreateRecoveryRequest(
        StateSnapshot state,
        object? persistentContext,
        AvendarAutonomyConstraints constraints,
        out JevChoiceRequest? request)
    {
        ArgumentNullException.ThrowIfNull(state);
        request = null;

        if (state.Session.InputMode != SessionInputMode.Normal || state.Combat.Active)
        {
            return false;
        }

        bool needsResources = NeedsRecoveryForTravel(state);
        bool standing = IsStanding(state.Character.Position);
        bool canStand = CanStandFromPosition(state.Character.Position);
        if (!needsResources && !canStand)
        {
            return false;
        }

        Dictionary<string, string?> criteria = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hold"] = "Issue no command and remain in the current recovery state."
        };

        if (canStand && ResourcesReadyForTravel(state) && !constraints.IsSuppressed("stand"))
        {
            criteria["stand"] = "Stand up because resources are sufficiently recovered to resume movement.";
        }

        SkillActionDefinition? recover = AvendarActionCapabilities.KnownActions.TryGetValue("recover", out SkillActionDefinition? found)
            ? found
            : null;
        if (recover is not null && IsBelowMaximum(state.Character.HitPoints))
        {
            ActionEligibilityResult eligibility = AvendarActionCapabilities.Evaluate(recover, state, hasTarget: false);
            if (eligibility.Eligibility == ActionEligibility.Eligible &&
                !constraints.IsSuppressed(recover.DecisionAction))
            {
                criteria[recover.DecisionAction] = recover.ExpectedEffect;
            }
        }

        if (standing && needsResources && !constraints.IsSuppressed("rest"))
        {
            criteria["rest"] = "Rest to improve passive hit point, mana, and movement recovery before continuing to travel.";
        }

        if (criteria.Count < 2)
        {
            return false;
        }

        request = new JevChoiceRequest(
            state.Version,
            JevDomain.Recovery,
            "Choose the next recovery action. Preserve survival margin before navigation resumes; avoid unnecessary downtime when resources are ready.",
            BuildProjection(state, persistentContext, constraints),
            criteria,
            AuthorityFor(state, JevDomain.Recovery),
            DecisionSource.Jev,
            [
                JevAuxiliaryQuestion.Score(
                    "readiness",
                    "Rate readiness to resume travel and combat.",
                    "depleted: recovery should continue",
                    "recovering: usable but below a comfortable margin",
                    "ready: sufficient resources for normal travel",
                    "full: effectively topped off"),
                JevAuxiliaryQuestion.Noul(
                    "resume_travel",
                    "Should the character resume travel now?",
                    "Yes when HP, MA, MV, position, and known conditions provide a reasonable safety margin.",
                    "No when additional recovery materially improves survival margin.")
            ]);
        return true;
    }

    public static bool TryCreateNavigationRequest(
        StateSnapshot state,
        object? persistentContext,
        out JevChoiceRequest? request) =>
        TryCreateNavigationRequest(state, persistentContext, AvendarAutonomyConstraints.Empty, out request);

    public static bool TryCreateNavigationRequest(
        StateSnapshot state,
        object? persistentContext,
        AvendarAutonomyConstraints constraints,
        out JevChoiceRequest? request)
    {
        ArgumentNullException.ThrowIfNull(state);
        request = null;

        if (state.Session.InputMode != SessionInputMode.Normal ||
            state.Combat.Active ||
            !IsStanding(state.Character.Position) ||
            NeedsRecoveryForTravel(state) ||
            !state.Room.Exits.IsKnown)
        {
            return false;
        }

        IReadOnlyList<RoomExitObservation> exits = state.Room.Exits.Details ?? state.Room.Exits.Directions
            .Select(direction => new RoomExitObservation(direction, true, ExitDoorState.Unknown, ExitTraversability.Traversable))
            .ToArray();

        Dictionary<string, NavigationCandidate> candidates = exits
            .Where(exit => exit.Exists && exit.Traversability != ExitTraversability.Blocked)
            .GroupBy(exit => exit.Direction, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(exit =>
            {
                string direction = NormalizeDirection(exit.Direction);
                string actionId = $"move:{direction}";
                string? destinationId = KnownDestinationId(state, direction);
                string? destinationName = KnownDestinationName(state, direction);
                int? recentIndex = destinationId is null
                    ? null
                    : IndexOfRoom(constraints.RecentRoomIds, destinationId);
                return new NavigationCandidate(actionId, direction, destinationId, destinationName, recentIndex);
            })
            .Where(candidate => !constraints.IsSuppressed(candidate.ActionId))
            .ToDictionary(candidate => candidate.ActionId, StringComparer.OrdinalIgnoreCase);

        if (candidates.Count == 0)
        {
            return false;
        }

        // Immediate reversal is a deterministic fallback, not a normal exploration choice.
        // Keep it only when it is the sole traversable route. This prevents E/W/E/W loops
        // without trapping the character in a genuine dead end.
        if (candidates.Count > 1 && !string.IsNullOrWhiteSpace(constraints.ImmediateBacktrackDirection))
        {
            candidates.Remove($"move:{NormalizeDirection(constraints.ImmediateBacktrackDirection!)}");
        }

        // If at least one route is unknown or outside the short-term tabu set, keep Jev focused
        // on those productive choices instead of bouncing through recently visited rooms.
        NavigationCandidate[] novel = candidates.Values.Where(candidate => candidate.RecentIndex is null).ToArray();
        if (novel.Length > 0)
        {
            candidates = novel.ToDictionary(candidate => candidate.ActionId, StringComparer.OrdinalIgnoreCase);
        }
        else if (candidates.Count > 1)
        {
            int oldestIndex = candidates.Values.Min(candidate => candidate.RecentIndex ?? int.MaxValue);
            candidates = candidates.Values
                .Where(candidate => candidate.RecentIndex == oldestIndex)
                .ToDictionary(candidate => candidate.ActionId, StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, string?> criteria = new(StringComparer.OrdinalIgnoreCase)
        {
            ["stay"] = "Remain in the current room when moving would be strategically worse or insufficiently informed."
        };

        foreach (NavigationCandidate candidate in candidates.Values.OrderBy(candidate => candidate.Direction, StringComparer.OrdinalIgnoreCase))
        {
            criteria[candidate.ActionId] = candidate.DestinationName is null
                ? $"Move {candidate.Direction} through an available exit to an unmapped destination."
                : $"Move {candidate.Direction} toward {candidate.DestinationName}.";
        }

        if (criteria.Count < 2)
        {
            return false;
        }

        request = new JevChoiceRequest(
            state.Version,
            JevDomain.Navigation,
            "Choose the next room movement for productive exploration or grinding. Use mapped history when useful, avoid blocked routes, and choose stay when movement is not justified.",
            BuildProjection(state, persistentContext, constraints),
            criteria,
            AuthorityFor(state, JevDomain.Navigation),
            DecisionSource.Jev,
            [
                JevAuxiliaryQuestion.Score(
                    "route_value",
                    "Rate the expected value of moving now.",
                    "poor: stay or gather more information",
                    "neutral: little known advantage",
                    "useful: likely progress or exploration value",
                    "strong: clearly preferred route for the current grind/exploration loop"),
                JevAuxiliaryQuestion.Noul(
                    "safe_to_move",
                    "Is it reasonable to leave the current room now?",
                    "Yes when recovery margin and route state support movement.",
                    "No when recovery, combat, or route uncertainty should keep the character here.")
            ]);
        return true;
    }

    public static bool TryMaterializeAction(
        StateSnapshot state,
        JevDomain domain,
        string actionId,
        out string? command,
        out string? rejectionReason) =>
        domain switch
        {
            JevDomain.Combat => TryMaterializeCombatAction(state, actionId, out command, out rejectionReason),
            JevDomain.Navigation => TryMaterializeNavigationAction(state, actionId, out command, out rejectionReason),
            JevDomain.Recovery => TryMaterializeRecoveryAction(state, actionId, out command, out rejectionReason),
            _ => UnsupportedDomain(domain, out command, out rejectionReason)
        };

    public static bool TryMaterializeCombatAction(
        StateSnapshot state,
        string actionId,
        out string? command,
        out string? rejectionReason)
    {
        ArgumentNullException.ThrowIfNull(state);
        command = null;
        rejectionReason = null;

        if (IsNoOpAction(actionId))
        {
            return true;
        }

        if (actionId.StartsWith("engage:", StringComparison.OrdinalIgnoreCase))
        {
            if (state.Combat.Active)
            {
                rejectionReason = "Combat has already started; idle target acquisition is stale.";
                return false;
            }

            string target = Uri.UnescapeDataString(actionId["engage:".Length..]);
            bool present = state.Room.Occupants.Any(occupant =>
                string.Equals(EntityTarget(occupant), target, StringComparison.OrdinalIgnoreCase));
            if (!present)
            {
                rejectionReason = $"Target '{target}' is no longer observed in the room.";
                return false;
            }

            command = $"kill {target}";
            return true;
        }

        if (!state.Combat.Active)
        {
            rejectionReason = "Combat is no longer active.";
            return false;
        }

        SkillActionDefinition? definition = AvendarActionCapabilities.KnownActions.Values.FirstOrDefault(
            candidate => candidate.DecisionAction.Equals(actionId, StringComparison.OrdinalIgnoreCase));
        if (definition is null || !IsCombatRelevant(definition.SkillName))
        {
            rejectionReason = $"Unknown combat action '{actionId}'.";
            return false;
        }

        string? targetName = state.Combat.TargetName ?? state.Combat.TargetId;
        bool hasTarget = !string.IsNullOrWhiteSpace(targetName);
        ActionEligibilityResult eligibility = AvendarActionCapabilities.Evaluate(
            definition,
            state,
            hasTarget,
            equippedWeaponIsBlunt: EquippedWeaponIsBlunt(state));
        if (eligibility.Eligibility != ActionEligibility.Eligible)
        {
            rejectionReason = eligibility.Reasons.Count == 0
                ? $"{definition.SkillName} is no longer eligible."
                : string.Join(" ", eligibility.Reasons);
            return false;
        }

        command = definition.CommandTemplate;
        if (definition.TargetRequirement == SkillTargetRequirement.Required)
        {
            if (string.IsNullOrWhiteSpace(targetName))
            {
                rejectionReason = "The selected action requires a target, but no target is currently known.";
                command = null;
                return false;
            }
            command = command.Replace("<target>", targetName, StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    public static bool TryMaterializeNavigationAction(
        StateSnapshot state,
        string actionId,
        out string? command,
        out string? rejectionReason)
    {
        command = null;
        rejectionReason = null;
        if (IsNoOpAction(actionId))
        {
            return true;
        }
        if (!actionId.StartsWith("move:", StringComparison.OrdinalIgnoreCase))
        {
            rejectionReason = $"Unknown navigation action '{actionId}'.";
            return false;
        }
        if (state.Combat.Active || NeedsRecoveryForTravel(state) || !IsStanding(state.Character.Position))
        {
            rejectionReason = "Navigation is no longer appropriate because combat, recovery, or position state changed.";
            return false;
        }

        string direction = NormalizeDirection(actionId["move:".Length..]);
        IReadOnlyList<RoomExitObservation> exits = state.Room.Exits.Details ?? state.Room.Exits.Directions
            .Select(value => new RoomExitObservation(value, true, ExitDoorState.Unknown, ExitTraversability.Traversable))
            .ToArray();
        RoomExitObservation? exit = exits.FirstOrDefault(value =>
            NormalizeDirection(value.Direction).Equals(direction, StringComparison.OrdinalIgnoreCase));
        if (exit is null || !exit.Exists || exit.Traversability == ExitTraversability.Blocked)
        {
            rejectionReason = $"Exit '{direction}' is no longer traversable.";
            return false;
        }

        command = direction;
        return true;
    }

    public static bool TryMaterializeRecoveryAction(
        StateSnapshot state,
        string actionId,
        out string? command,
        out string? rejectionReason)
    {
        command = null;
        rejectionReason = null;
        if (IsNoOpAction(actionId))
        {
            return true;
        }
        if (state.Combat.Active)
        {
            rejectionReason = "Recovery action is stale because combat is active.";
            return false;
        }

        if (actionId.Equals("stand", StringComparison.OrdinalIgnoreCase))
        {
            if (IsStanding(state.Character.Position))
            {
                rejectionReason = "Character is already standing.";
                return false;
            }
            if (!CanStandFromPosition(state.Character.Position))
            {
                rejectionReason = "Character posture is not known to require a stand command.";
                return false;
            }
            command = "stand";
            return true;
        }
        if (actionId.Equals("rest", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsStanding(state.Character.Position) || !NeedsRecoveryForTravel(state))
            {
                rejectionReason = "Rest is no longer appropriate.";
                return false;
            }
            command = "rest";
            return true;
        }
        if (actionId.Equals("recover", StringComparison.OrdinalIgnoreCase) &&
            AvendarActionCapabilities.KnownActions.TryGetValue("recover", out SkillActionDefinition? recover))
        {
            ActionEligibilityResult eligibility = AvendarActionCapabilities.Evaluate(recover, state, hasTarget: false);
            if (eligibility.Eligibility != ActionEligibility.Eligible || !IsBelowMaximum(state.Character.HitPoints))
            {
                rejectionReason = eligibility.Reasons.Count == 0 ? "Recover is no longer needed." : string.Join(" ", eligibility.Reasons);
                return false;
            }
            command = recover.CommandTemplate;
            return true;
        }

        rejectionReason = $"Unknown recovery action '{actionId}'.";
        return false;
    }

    private sealed record NavigationCandidate(
        string ActionId,
        string Direction,
        string? DestinationId,
        string? DestinationName,
        int? RecentIndex);

    public static bool NeedsRecoveryForTravel(StateSnapshot state) =>
        Percent(state.Character.HitPoints) is double hp && hp < 0.85 ||
        Percent(state.Character.Mana) is double mana && mana < 0.50 ||
        Percent(state.Character.Movement) is double move && move < 0.50;

    public static bool IsNoOpAction(string actionId) =>
        actionId.Equals("continue", StringComparison.OrdinalIgnoreCase) ||
        actionId.Equals("stay", StringComparison.OrdinalIgnoreCase) ||
        actionId.Equals("hold", StringComparison.OrdinalIgnoreCase);

    private static bool ResourcesReadyForTravel(StateSnapshot state) =>
        (Percent(state.Character.HitPoints) ?? 1) >= 0.90 &&
        (Percent(state.Character.Mana) ?? 1) >= 0.65 &&
        (Percent(state.Character.Movement) ?? 1) >= 0.65;

    private static object BuildProjection(
        StateSnapshot state,
        object? persistentContext,
        AvendarAutonomyConstraints constraints) => new
    {
        character = new
        {
            hp = VitalProjection(state.Character.HitPoints),
            mana = VitalProjection(state.Character.Mana),
            move = VitalProjection(state.Character.Movement),
            position = state.Character.Position,
            conditions = state.Character.Conditions,
            effects = state.Character.Effects,
            level = state.Character.Profile.Level
        },
        combat = new
        {
            active = state.Combat.Active,
            target = state.Combat.TargetName ?? state.Combat.TargetId,
            targetCondition = state.Combat.TargetCondition,
            lastOutgoing = state.Combat.LastPlayerDamage,
            lastIncoming = state.Combat.LastOpponentDamage,
            lastAvoidedAttack = state.Combat.LastAvoidedAttack
        },
        room = new
        {
            id = state.Room.Id,
            name = state.Room.Name,
            terrain = state.Room.Terrain,
            light = state.Room.Light,
            exits = state.Room.Exits.Directions,
            occupants = state.Room.Occupants.Select(occupant => new
            {
                occupant.Description,
                occupant.CanonicalName,
                target = EntityTarget(occupant)
            }).ToArray()
        },
        knowledge = persistentContext,
        autonomy = new
        {
            recentRooms = constraints.RecentRoomIds,
            immediateBacktrackDirection = constraints.ImmediateBacktrackDirection,
            suppressedActions = constraints.SuppressedActions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            protectedTargets = constraints.ProtectedTargets.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
        },
        abilities = state.Character.Skills
            .Where(skill => skill.Availability == SkillAvailability.Available)
            .Select(skill => new
            {
                skill.Name,
                skill.Domain,
                skill.ProficiencyPercent,
                skill.IsFresh
            })
            .ToArray()
    };

    private static object VitalProjection(VitalState vital) => new
    {
        current = vital.Current,
        maximum = vital.Maximum,
        percent = Percent(vital)
    };

    private static double? Percent(VitalState vital) =>
        vital.Current is int current && vital.Maximum is int maximum && maximum > 0
            ? Math.Round((double)current / maximum, 3)
            : null;

    private static bool IsBelowMaximum(VitalState vital) =>
        vital.Current is int current && vital.Maximum is int maximum && current < maximum;

    private static bool IsStanding(string? position) =>
        string.Equals(position?.Trim(), "standing", StringComparison.OrdinalIgnoreCase);

    private static bool CanStandFromPosition(string? position)
    {
        string normalized = position?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized is "resting" or "sitting" or "sleeping";
    }

    private static string NormalizeDirection(string direction) => direction.Trim().ToLowerInvariant();

    private static int? IndexOfRoom(IReadOnlyList<string> rooms, string roomId)
    {
        for (int index = 0; index < rooms.Count; index++)
        {
            if (string.Equals(rooms[index], roomId, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return null;
    }

    private static string? KnownDestinationId(StateSnapshot state, string direction)
    {
        if (string.IsNullOrWhiteSpace(state.Room.Id))
        {
            return null;
        }
        RoomEdgeState? edge = state.World.Edges.LastOrDefault(edge =>
            edge.FromRoomId.Equals(state.Room.Id, StringComparison.Ordinal) &&
            edge.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase));
        return edge?.ToRoomId;
    }

    private static string? KnownDestinationName(StateSnapshot state, string direction)
    {
        if (string.IsNullOrWhiteSpace(state.Room.Id))
        {
            return null;
        }
        RoomEdgeState? edge = state.World.Edges.LastOrDefault(edge =>
            edge.FromRoomId.Equals(state.Room.Id, StringComparison.Ordinal) &&
            edge.Direction.Equals(direction, StringComparison.OrdinalIgnoreCase));
        if (edge is null || !state.World.Rooms.TryGetValue(edge.ToRoomId, out RoomNodeState? room))
        {
            return null;
        }
        return room.Name;
    }

    private static bool IsProtectedTarget(RoomContentObservation occupant, IReadOnlySet<string> protectedTargets)
    {
        if (protectedTargets.Count == 0)
        {
            return false;
        }

        string canonical = occupant.CanonicalName?.Trim() ?? string.Empty;
        string description = occupant.Description.Trim();
        return protectedTargets.Any(name =>
            canonical.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            description.Contains(name, StringComparison.OrdinalIgnoreCase) ||
            (occupant.TargetKeywords?.Any(keyword => keyword.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? false));
    }

    private static string? EntityTarget(RoomContentObservation entity) =>
        entity.TargetKeywords?.FirstOrDefault(keyword => !string.IsNullOrWhiteSpace(keyword)) ??
        entity.CanonicalName?.Trim();

    private static JevAuthority AuthorityFor(StateSnapshot state, JevDomain domain) =>
        state.JevAuthority.Domains.TryGetValue(domain, out JevAuthority configured)
            ? configured
            : JevAuthority.Off;

    private static bool? EquippedWeaponIsBlunt(StateSnapshot state)
    {
        string? mainHand = state.Character.Equipment.MainHand;
        if (string.IsNullOrWhiteSpace(mainHand) ||
            !state.Character.Equipment.IdentifiedItems.TryGetValue(mainHand, out ItemIdentification? item))
        {
            return null;
        }

        string? damageType = item.DamageType?.Trim().ToLowerInvariant();
        return damageType switch
        {
            "bash" or "blunt" => true,
            "slash" or "pierce" or "stab" => false,
            _ => null
        };
    }

    private static bool IsCombatRelevant(string skillName) =>
        skillName.Equals("dirt kicking", StringComparison.OrdinalIgnoreCase) ||
        skillName.Equals("recover", StringComparison.OrdinalIgnoreCase) ||
        skillName.Equals("backstab", StringComparison.OrdinalIgnoreCase);

    private static bool UnsupportedDomain(JevDomain domain, out string? command, out string? rejectionReason)
    {
        command = null;
        rejectionReason = $"Jev domain '{domain}' does not yet have an executable action projector.";
        return false;
    }
}
