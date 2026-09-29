using System.Globalization;
using NexMud.Contracts.Events;
using NexMud.Contracts.State;

namespace NexMud.Client.Automation;

/// <summary>
/// Deterministic expression evaluator shared by state rules and workflows. The language is
/// intentionally small and side-effect free so automation remains auditable and safe.
/// </summary>
public static class GameRuleEvaluator
{
    private static readonly string[] Operators = ["<=", ">=", "!=", "==", "<", ">"];

    public static bool Evaluate(
        string expression,
        StateSnapshot state,
        IReadOnlyDictionary<string, string>? variables = null,
        IMudEvent? currentEvent = null)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        return EvaluateOr(TrimOuterParentheses(expression.Trim()), state, variables, currentEvent);
    }

    public static string? ResolveText(
        string name,
        StateSnapshot state,
        IReadOnlyDictionary<string, string>? variables = null,
        IMudEvent? currentEvent = null)
    {
        object? value = Resolve(name, state, variables, currentEvent);
        return value switch
        {
            null => null,
            double number => number.ToString("0.##", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    private static bool EvaluateOr(
        string expression,
        StateSnapshot state,
        IReadOnlyDictionary<string, string>? variables,
        IMudEvent? currentEvent)
    {
        IReadOnlyList<string> parts = SplitTopLevel(expression, "||");
        if (parts.Count > 1) return parts.Any(part => EvaluateAnd(part, state, variables, currentEvent));
        return EvaluateAnd(expression, state, variables, currentEvent);
    }

    private static bool EvaluateAnd(
        string expression,
        StateSnapshot state,
        IReadOnlyDictionary<string, string>? variables,
        IMudEvent? currentEvent)
    {
        IReadOnlyList<string> parts = SplitTopLevel(expression, "&&");
        if (parts.Count > 1) return parts.All(part => EvaluateClause(part, state, variables, currentEvent));
        return EvaluateClause(expression, state, variables, currentEvent);
    }

    private static bool EvaluateClause(
        string rawClause,
        StateSnapshot state,
        IReadOnlyDictionary<string, string>? variables,
        IMudEvent? currentEvent)
    {
        string clause = TrimOuterParentheses(rawClause.Trim());
        if (SplitTopLevel(clause, "||").Count > 1 || SplitTopLevel(clause, "&&").Count > 1)
            return EvaluateOr(clause, state, variables, currentEvent);

        bool negate = clause.StartsWith('!') && !clause.StartsWith("!=", StringComparison.Ordinal);
        if (negate)
        {
            string nested = TrimOuterParentheses(clause[1..].Trim());
            return !EvaluateOr(nested, state, variables, currentEvent);
        }

        string? op = FindTopLevelOperator(clause);
        if (op is null)
        {
            object? value = Resolve(clause, state, variables, currentEvent);
            bool result = value switch
            {
                bool boolean => boolean,
                string text => !string.IsNullOrWhiteSpace(text) && !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase),
                null => false,
                _ => true
            };
            return result;
        }

        int index = FindTopLevelOperatorIndex(clause, op);
        string leftName = clause[..index].Trim();
        string rightLiteral = clause[(index + op.Length)..].Trim();
        object? left = Resolve(leftName, state, variables, currentEvent);
        object? right = ResolveLiteralOrValue(rightLiteral, state, variables, currentEvent);
        return Compare(left, right, op);
    }

    private static object? ResolveLiteralOrValue(
        string text,
        StateSnapshot state,
        IReadOnlyDictionary<string, string>? variables,
        IMudEvent? currentEvent)
    {
        string trimmed = text.Trim();
        if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) || (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
            return trimmed.Length >= 2 ? trimmed[1..^1] : string.Empty;
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase)) return null;
        if (bool.TryParse(trimmed, out bool boolean)) return boolean;
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return number;
        return Resolve(trimmed, state, variables, currentEvent) ?? trimmed;
    }

    private static object? Resolve(
        string name,
        StateSnapshot state,
        IReadOnlyDictionary<string, string>? variables,
        IMudEvent? currentEvent)
    {
        string token = name.Trim();
        if (token.StartsWith("var.", StringComparison.OrdinalIgnoreCase))
        {
            string variable = token[4..];
            return variables is not null && variables.TryGetValue(variable, out string? value) ? value : null;
        }

        if (TryFunction(token, "effect", out string? effect))
            return state.Character.Effects.Any(value => value.Contains(effect!, StringComparison.OrdinalIgnoreCase));
        if (TryFunction(token, "condition", out string? condition))
            return state.Character.Conditions.Any(value => value.Contains(condition!, StringComparison.OrdinalIgnoreCase));
        if (TryFunction(token, "occupant", out string? occupant))
            return state.Room.Occupants.Any(value => MatchesEntity(value, occupant!));
        if (TryFunction(token, "object", out string? obj))
            return state.Room.Objects.Any(value => MatchesEntity(value, obj!));
        if (TryFunction(token, "corpse", out string? corpse))
            return state.Room.Corpses.Any(value => MatchesEntity(value, corpse!));
        if (TryFunction(token, "interactable", out string? interactable))
            return state.Room.Interactables.Any(value => MatchesEntity(value, interactable!));
        if (TryFunction(token, "equipped", out string? equipped))
            return (!string.IsNullOrWhiteSpace(state.Character.Equipment.MainHand) &&
                    state.Character.Equipment.MainHand.Contains(equipped!, StringComparison.OrdinalIgnoreCase)) ||
                   state.Character.Equipment.Slots.Any(slot =>
                       !string.IsNullOrWhiteSpace(slot.Item) && slot.Item.Contains(equipped!, StringComparison.OrdinalIgnoreCase));
        if (TryFunction(token, "equipment", out string? equipmentSlot))
        {
            if (equipmentSlot!.Equals("mainhand", StringComparison.OrdinalIgnoreCase) ||
                equipmentSlot.Equals("main hand", StringComparison.OrdinalIgnoreCase))
                return state.Character.Equipment.MainHand;
            return state.Character.Equipment.Slots
                .FirstOrDefault(slot => slot.Slot.Equals(equipmentSlot, StringComparison.OrdinalIgnoreCase))?.Item;
        }
        if (TryFunction(token, "exit", out string? direction))
            return state.Room.Exits.IsKnown && state.Room.Exits.Directions.Any(value => value.Equals(direction, StringComparison.OrdinalIgnoreCase));
        if (TryFunction(token, "skill", out string? skill))
            return state.Character.Skills.FirstOrDefault(value => value.Name.Equals(skill, StringComparison.OrdinalIgnoreCase))?.ProficiencyPercent;
        if (TryFunction(token, "spell", out string? spell))
            return state.Character.Spells.FirstOrDefault(value => value.Name.Equals(spell, StringComparison.OrdinalIgnoreCase))?.ProficiencyPercent;

        return token.ToLowerInvariant() switch
        {
            "connected" => state.Session.ConnectionStatus == ConnectionStatus.Connected,
            "combat.active" => state.Combat.Active,
            "combat.target" => state.Combat.TargetName ?? state.Combat.TargetId,
            "combat.condition" => state.Combat.TargetCondition,
            "hp" => state.Character.HitPoints.Current,
            "hp.max" => state.Character.HitPoints.Maximum,
            "hp.percent" => Percent(state.Character.HitPoints),
            "mana" => state.Character.Mana.Current,
            "mana.max" => state.Character.Mana.Maximum,
            "mana.percent" => Percent(state.Character.Mana),
            "move" or "movement" => state.Character.Movement.Current,
            "move.max" or "movement.max" => state.Character.Movement.Maximum,
            "move.percent" or "movement.percent" => Percent(state.Character.Movement),
            "position" => state.Character.Position,
            "room.id" => state.Room.Id,
            "room.name" => state.Room.Name,
            "room.terrain" => state.Room.Terrain,
            "room.light" => state.Room.Light,
            "room.occupants" => state.Room.Occupants.Count,
            "room.objects" => state.Room.Objects.Count,
            "room.corpses" => state.Room.Corpses.Count,
            "level" => state.Character.Profile.Level,
            "xp" => state.Character.Experience,
            "xp.tolevel" => state.Character.ExperienceToLevel,
            "inventory.items" => state.Character.Inventory.Items,
            "inventory.weight" => state.Character.Inventory.Weight,
            "inventory.copper" => state.Character.Inventory.Copper,
            "inventory.silver" => state.Character.Inventory.Silver,
            "inventory.gold" => state.Character.Inventory.Gold,
            "event.type" => currentEvent?.GetType().Name,
            _ when token.StartsWith("event.", StringComparison.OrdinalIgnoreCase) => ResolveEventField(token[6..], currentEvent),
            _ => null
        };
    }

    private static object? ResolveEventField(string field, IMudEvent? currentEvent)
    {
        if (currentEvent is null) return null;
        return field.ToLowerInvariant() switch
        {
            "target" => currentEvent switch
            {
                EnemyKilled value => value.TargetName,
                CorpseDestroyed value => value.TargetName,
                CombatTargetConditionObserved value => value.TargetName,
                _ => null
            },
            "item" => currentEvent is ItemAcquired item ? item.ItemName : null,
            "source" => currentEvent is ItemAcquired item ? item.SourceDescription : null,
            "room.id" => currentEvent switch
            {
                RoomObservationObserved room => room.RoomId,
                RoomChanged room => room.Id,
                AutoMoveStateChanged movement => movement.DestinationRoomId,
                _ => null
            },
            "room.name" => currentEvent switch
            {
                RoomObservationObserved room => room.RoomName,
                RoomChanged room => room.Name,
                _ => null
            },
            "direction" => currentEvent switch
            {
                NavigationAttempted value => value.Direction,
                NavigationFailed value => value.Direction,
                RoomExitStateChanged value => value.Direction,
                AutoMoveStateChanged value => value.CurrentDirection,
                _ => null
            },
            "reason" => currentEvent switch
            {
                NavigationFailed value => value.Reason,
                ActionRejected value => value.Reason,
                AutoMoveStateChanged value => value.Reason,
                _ => null
            },
            "channel" => currentEvent is CommunicationObserved communication ? communication.Channel : null,
            "speaker" => currentEvent is CommunicationObserved communication ? communication.Speaker : null,
            "message" => currentEvent is CommunicationObserved communication ? communication.Message : null,
            "amount" => currentEvent switch
            {
                ExperienceGained value => value.Amount,
                CurrencyGained value => value.Amount,
                _ => null
            },
            "currency" => currentEvent is CurrencyGained currency ? currency.Currency : null,
            "status" => currentEvent switch
            {
                AutoMoveStateChanged movement => movement.Status.ToString(),
                ConnectionStateChanged connection => connection.Status.ToString(),
                AutomationWorkflowStateChanged workflow => workflow.Status.ToString(),
                _ => null
            },
            _ => null
        };
    }

    private static bool MatchesEntity(RoomContentObservation observation, string query) =>
        observation.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(observation.CanonicalName) && observation.CanonicalName.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
        (observation.TargetKeywords?.Any(keyword => keyword.Contains(query, StringComparison.OrdinalIgnoreCase)) ?? false);

    private static bool TryFunction(string token, string name, out string? argument)
    {
        argument = null;
        string prefix = name + "(";
        if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !token.EndsWith(')')) return false;
        string raw = token[prefix.Length..^1].Trim();
        if ((raw.StartsWith('"') && raw.EndsWith('"')) || (raw.StartsWith('\'') && raw.EndsWith('\''))) raw = raw[1..^1];
        argument = raw;
        return raw.Length > 0;
    }

    private static double? Percent(VitalState vital) =>
        vital.Current is int current && vital.Maximum is int maximum && maximum > 0
            ? current * 100d / maximum
            : null;

    private static bool Compare(object? left, object? right, string op)
    {
        if (left is null || right is null)
        {
            bool equal = left is null && right is null;
            return op switch { "==" => equal, "!=" => !equal, _ => false };
        }
        if (left is bool leftBool && right is bool rightBool) return Apply(leftBool.CompareTo(rightBool), op);
        if (TryNumber(left, out double leftNumber) && TryNumber(right, out double rightNumber))
            return Apply(leftNumber.CompareTo(rightNumber), op);

        int comparison = string.Compare(
            Convert.ToString(left, CultureInfo.InvariantCulture),
            Convert.ToString(right, CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
        return Apply(comparison, op);
    }

    private static bool TryNumber(object value, out double number)
    {
        try
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            number = 0;
            return false;
        }
    }

    private static bool Apply(int comparison, string op) => op switch
    {
        "==" => comparison == 0,
        "!=" => comparison != 0,
        "<" => comparison < 0,
        ">" => comparison > 0,
        "<=" => comparison <= 0,
        ">=" => comparison >= 0,
        _ => false
    };

    private static string? FindTopLevelOperator(string expression) =>
        Operators.FirstOrDefault(op => FindTopLevelOperatorIndex(expression, op) >= 0);

    private static int FindTopLevelOperatorIndex(string expression, string op)
    {
        int depth = 0;
        char quote = '\0';
        for (int index = 0; index <= expression.Length - op.Length; index++)
        {
            char ch = expression[index];
            if (quote != '\0')
            {
                if (ch == quote && (index == 0 || expression[index - 1] != '\\')) quote = '\0';
                continue;
            }
            if (ch is '\'' or '"') { quote = ch; continue; }
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth = Math.Max(0, depth - 1); continue; }
            if (depth == 0 && expression.AsSpan(index).StartsWith(op, StringComparison.Ordinal)) return index;
        }
        return -1;
    }

    private static IReadOnlyList<string> SplitTopLevel(string expression, string separator)
    {
        List<string> parts = [];
        int depth = 0;
        char quote = '\0';
        int start = 0;
        for (int index = 0; index <= expression.Length - separator.Length; index++)
        {
            char ch = expression[index];
            if (quote != '\0')
            {
                if (ch == quote && (index == 0 || expression[index - 1] != '\\')) quote = '\0';
                continue;
            }
            if (ch is '\'' or '"') { quote = ch; continue; }
            if (ch == '(') { depth++; continue; }
            if (ch == ')') { depth = Math.Max(0, depth - 1); continue; }
            if (depth == 0 && expression.AsSpan(index).StartsWith(separator, StringComparison.Ordinal))
            {
                parts.Add(expression[start..index].Trim());
                start = index + separator.Length;
                index += separator.Length - 1;
            }
        }
        if (parts.Count == 0) return [expression.Trim()];
        parts.Add(expression[start..].Trim());
        return parts;
    }

    private static string TrimOuterParentheses(string expression)
    {
        string value = expression;
        while (value.Length >= 2 && value[0] == '(' && value[^1] == ')' && EnclosesWholeExpression(value))
            value = value[1..^1].Trim();
        return value;
    }

    private static bool EnclosesWholeExpression(string expression)
    {
        int depth = 0;
        char quote = '\0';
        for (int index = 0; index < expression.Length; index++)
        {
            char ch = expression[index];
            if (quote != '\0')
            {
                if (ch == quote && (index == 0 || expression[index - 1] != '\\')) quote = '\0';
                continue;
            }
            if (ch is '\'' or '"') { quote = ch; continue; }
            if (ch == '(') depth++;
            else if (ch == ')' && --depth == 0 && index != expression.Length - 1) return false;
        }
        return depth == 0;
    }
}
