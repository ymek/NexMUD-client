using System.Text.RegularExpressions;
using JevMud.Contracts.Events;
using JevMud.Contracts.Gameplay;
using JevMud.Contracts.State;

namespace JevMud.Adapters.Avendar;

public sealed partial class AvendarSemanticParser
{
    private static readonly string AbsolutePattern = string.Join(
        "|",
        AvendarDamageLexicon.AbsoluteTiers.Keys
            .OrderByDescending(value => value.Length)
            .Select(Regex.Escape));

    private static readonly string RelativePattern = string.Join(
        "|",
        AvendarDamageLexicon.RelativeTermsLongestFirst.Select(Regex.Escape));

    private static readonly Regex PlayerDamageRegex = new(
        $@"^Your (?:(?<absolute>{AbsolutePattern}) )?(?<type>\S+) (?<relative>{RelativePattern}) (?<target>.+)\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OpponentDamageRegex = new(
        $@"^(?<source>.+?)'s (?:(?<absolute>{AbsolutePattern}) )?(?<type>\S+) (?<relative>{RelativePattern}) you\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<IMudEvent> ParseLine(string line, long sourceSequence = 0)
    {
        string text = line.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        List<IMudEvent> events = [];

        if (text.Equals("Buffer cleared.", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new GameCommandQueueCleared(sourceSequence));
            return events;
        }

        if (text.Equals("You are hungry.", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new CharacterStatusObserved("hungry", true, sourceSequence));
            return events;
        }

        if (text.Equals("You are thirsty.", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new CharacterStatusObserved("thirsty", true, sourceSequence));
            return events;
        }

        if (CombatMovementRestrictionRegex().IsMatch(text))
        {
            events.Add(new MovementObserved(new MovementObservation(
                MovementCause.Unknown,
                MovementResult.CombatRestricted,
                null,
                null,
                text,
                sourceSequence)));
            return events;
        }

        if (TeleportCueRegex().IsMatch(text))
        {
            events.Add(new MovementObserved(new MovementObservation(
                MovementCause.Teleport,
                MovementResult.Unknown,
                null,
                null,
                text,
                sourceSequence)));
            return events;
        }

        Match follow = FollowMovementRegex().Match(text);
        if (follow.Success)
        {
            events.Add(Movement(MovementCause.Follow, null, text, sourceSequence));
            return events;
        }

        if (FleeMovementRegex().IsMatch(text))
        {
            events.Add(Movement(MovementCause.Flee, null, text, sourceSequence));
            return events;
        }

        Match crawl = CrawlMovementRegex().Match(text);
        if (crawl.Success)
        {
            events.Add(Movement(
                MovementCause.Crawl,
                crawl.Groups["direction"].Value.ToLowerInvariant(),
                text,
                sourceSequence));
            return events;
        }

        if (PortalMovementRegex().IsMatch(text))
        {
            events.Add(Movement(MovementCause.Portal, null, text, sourceSequence));
            return events;
        }

        if (SummonMovementRegex().IsMatch(text))
        {
            events.Add(Movement(MovementCause.Summon, null, text, sourceSequence));
            return events;
        }

        if (ForcedMovementRegex().IsMatch(text))
        {
            events.Add(Movement(MovementCause.Forced, null, text, sourceSequence));
            return events;
        }

        if (text == "[Hit Return to continue]")
        {
            events.Add(new PagerPromptObserved());
            return events;
        }

        if (text.Equals("You are already standing.", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("You stand up.", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new CharacterPositionObserved("standing"));
            return events;
        }

        if (text.Equals("Alas, you cannot go that way.", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new NavigationFailed(null, text));
            events.Add(new MovementObserved(new MovementObservation(
                MovementCause.Unknown,
                MovementResult.Blocked,
                null,
                null,
                text,
                sourceSequence)));
            return events;
        }

        Match opened = DoorOpenedRegex().Match(text);
        if (opened.Success)
        {
            events.Add(new RoomExitStateChanged(
                opened.Groups["direction"].Value.ToLowerInvariant(),
                ExitDoorState.Open,
                ExitTraversability.Traversable));
            return events;
        }

        if (ClosedDoorRegex().IsMatch(text) || LockedDoorRegex().IsMatch(text))
        {
            events.Add(new NavigationFailed(null, text));
            return events;
        }

        Match exploration = ExplorationRegex().Match(text);
        if (exploration.Success)
        {
            int points = int.Parse(exploration.Groups["points"].Value);
            int xp = int.Parse(exploration.Groups["xp"].Value);
            events.Add(new ExplorationGained(points, xp));
            events.Add(new ExperienceGained(xp));
            return events;
        }

        Match explorationOnly = ExplorationOnlyRegex().Match(text);
        if (explorationOnly.Success)
        {
            events.Add(new ExplorationGained(int.Parse(explorationOnly.Groups["points"].Value), 0));
            return events;
        }

        Match improved = SkillImprovedRegex().Match(text);
        if (improved.Success)
        {
            int xp = int.Parse(improved.Groups["xp"].Value);
            events.Add(new SkillImproved(improved.Groups["skill"].Value.Trim(), xp));
            events.Add(new ExperienceGained(xp));
            return events;
        }

        Match practiced = SkillPracticeRegex().Match(text);
        if (practiced.Success)
        {
            events.Add(new SkillPracticeSucceeded(practiced.Groups["skill"].Value.Trim()));
            return events;
        }

        Match xpMatch = ExperienceRegex().Match(text);
        if (xpMatch.Success)
        {
            events.Add(new ExperienceGained(int.Parse(xpMatch.Groups["amount"].Value)));
            return events;
        }

        Match currency = CurrencyRegex().Match(text);
        if (currency.Success)
        {
            events.Add(new CurrencyGained(
                currency.Groups["currency"].Value.ToLowerInvariant(),
                int.Parse(currency.Groups["amount"].Value)));
            return events;
        }

        Match splitCurrency = SplitCurrencyRegex().Match(text);
        if (splitCurrency.Success)
        {
            events.Add(new CurrencyGained(
                splitCurrency.Groups["currency"].Value.ToLowerInvariant(),
                int.Parse(splitCurrency.Groups["amount"].Value)));
            return events;
        }

        Match acquired = ItemAcquiredRegex().Match(text);
        if (acquired.Success)
        {
            string source = acquired.Groups["source"].Value.Trim();
            events.Add(new ItemAcquired(
                acquired.Groups["item"].Value.Trim(),
                source,
                source.Contains("corpse", StringComparison.OrdinalIgnoreCase)
                    ? ItemAcquisitionSourceKind.Corpse
                    : ItemAcquisitionSourceKind.Unknown,
                text));
            return events;
        }

        Match dropped = MobDroppedItemRegex().Match(text);
        if (dropped.Success)
        {
            events.Add(new ItemAcquired(
                dropped.Groups["item"].Value.Trim(),
                dropped.Groups["source"].Value.Trim(),
                ItemAcquisitionSourceKind.MobDrop,
                text));
            return events;
        }

        Match promotion = PromotionRegex().Match(text);
        if (promotion.Success)
        {
            events.Add(new CharacterPromoted(
                int.Parse(promotion.Groups["hp"].Value),
                int.Parse(promotion.Groups["mana"].Value),
                int.Parse(promotion.Groups["move"].Value)));
            return events;
        }

        Match dualWield = DualWieldRegex().Match(text);
        if (dualWield.Success)
        {
            events.Add(new EquipmentChanged("dual wielded", dualWield.Groups["item"].Value));
            return events;
        }

        Match held = HoldRegex().Match(text);
        if (held.Success)
        {
            events.Add(new EquipmentChanged("held", held.Groups["item"].Value));
            return events;
        }

        Match wield = WieldRegex().Match(text);
        if (wield.Success)
        {
            events.Add(new EquipmentChanged("wielded", wield.Groups["item"].Value));
            return events;
        }

        Match stopUsing = StopUsingRegex().Match(text);
        if (stopUsing.Success)
        {
            events.Add(new EquipmentChanged("wielded", null));
            return events;
        }

        Match wear = WearRegex().Match(text);
        if (wear.Success)
        {
            events.Add(new EquipmentChanged(
                NormalizeEquipmentSlot(wear.Groups["slot"].Value),
                wear.Groups["item"].Value));
            return events;
        }

        if (text.Equals("You sure are BLEEDING!", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new CharacterConditionChanged("bleeding", true));
            return events;
        }

        if (text.Equals("You are full.", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new CharacterConditionChanged("full", true));
            return events;
        }

        if (text.Equals("Your thirst is quenched.", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new CharacterConditionChanged("thirst quenched", true));
            return events;
        }

        Match playerDamage = PlayerDamageRegex.Match(text);
        if (playerDamage.Success)
        {
            events.Add(new CombatDamageObserved(CreateDamage(
                CombatActor.Player,
                "player",
                playerDamage.Groups["target"].Value,
                playerDamage)));
            return events;
        }

        Match opponentDamage = OpponentDamageRegex.Match(text);
        if (opponentDamage.Success)
        {
            string source = opponentDamage.Groups["source"].Value;
            events.Add(new CombatDamageObserved(CreateDamage(
                CombatActor.Opponent,
                source,
                "player",
                opponentDamage)));
            return events;
        }

        Match playerMiss = PlayerMissRegex().Match(text);
        if (playerMiss.Success)
        {
            events.Add(new CombatAttackObserved(new CombatAttackObservation(
                CombatActor.Player,
                "player",
                playerMiss.Groups["target"].Value,
                playerMiss.Groups["type"].Value.ToLowerInvariant(),
                AttackOutcome.Miss)));
            return events;
        }

        Match opponentMiss = OpponentMissRegex().Match(text);
        if (opponentMiss.Success)
        {
            events.Add(new CombatAttackObserved(new CombatAttackObservation(
                CombatActor.Opponent,
                opponentMiss.Groups["source"].Value,
                "player",
                opponentMiss.Groups["type"].Value.ToLowerInvariant(),
                AttackOutcome.Miss)));
            return events;
        }

        Match dodge = PlayerDodgedRegex().Match(text);
        if (dodge.Success)
        {
            events.Add(new CombatAttackObserved(new CombatAttackObservation(
                CombatActor.Player,
                "player",
                dodge.Groups["target"].Value,
                null,
                AttackOutcome.Dodged)));
            return events;
        }

        Match departed = OccupantDepartureRegex().Match(text);
        if (departed.Success)
        {
            events.Add(new RoomOccupantDeparted(
                departed.Groups["target"].Value,
                departed.Groups["direction"].Value.ToLowerInvariant()));
            return events;
        }

        Match dead = DeathRegex().Match(text);
        if (dead.Success)
        {
            events.Add(new EnemyKilled(dead.Groups["target"].Value));
            return events;
        }

        Match corpseDestroyed = CorpseDestroyedRegex().Match(text);
        if (corpseDestroyed.Success)
        {
            events.Add(new CorpseDestroyed(corpseDestroyed.Groups["target"].Value.Trim()));
            return events;
        }

        Match sacrifice = CorpseSacrificeRegex().Match(text);
        if (sacrifice.Success && sacrifice.Groups["item"].Value.Contains("corpse", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new CorpseSacrificed(
                sacrifice.Groups["item"].Value.Trim(),
                sacrifice.Groups["deity"].Value.Trim(),
                sourceSequence));
            return events;
        }

        Match harvested = CorpseHarvestRegex().Match(text);
        if (harvested.Success)
        {
            events.Add(new CorpseHarvested(
                harvested.Groups["item"].Value.Trim(),
                harvested.Groups["corpse"].Value.Trim(),
                sourceSequence));
            return events;
        }

        Match conditionWithPercent = CombatConditionPercentRegex().Match(text);
        if (conditionWithPercent.Success)
        {
            int minimum = int.Parse(conditionWithPercent.Groups["min"].Value);
            int maximum = conditionWithPercent.Groups["max"].Success
                ? int.Parse(conditionWithPercent.Groups["max"].Value)
                : minimum;
            string descriptor = conditionWithPercent.Groups["condition"].Value.Trim();
            events.Add(new CombatTargetConditionObserved(
                conditionWithPercent.Groups["target"].Value.Trim(),
                descriptor)
            {
                Range = new ConditionRange(minimum, maximum, descriptor),
                SourceSequence = sourceSequence
            });
            return events;
        }

        Match condition = CombatConditionRegex().Match(text);
        if (condition.Success)
        {
            string descriptor = condition.Groups["condition"].Value;
            events.Add(new CombatTargetConditionObserved(
                condition.Groups["target"].Value,
                descriptor)
            {
                Range = KnownConditionRange(descriptor),
                SourceSequence = sourceSequence
            });
        }

        return events;
    }

    private static MovementObserved Movement(
        MovementCause cause,
        string? direction,
        string detail,
        long sourceSequence) =>
        new(new MovementObservation(
            cause,
            MovementResult.SucceededUnknownRoom,
            direction,
            null,
            detail,
            sourceSequence));

    private static ConditionRange? KnownConditionRange(string descriptor) =>
        descriptor.Trim().ToLowerInvariant() switch
        {
            "in excellent condition" => new ConditionRange(100, 100, descriptor),
            "a few scratches" => new ConditionRange(90, 100, descriptor),
            "pretty hurt" => new ConditionRange(15, 30, descriptor),
            "some small wounds and bruises" => new ConditionRange(75, 90, descriptor),
            "quite a few wounds" => new ConditionRange(50, 75, descriptor),
            "some big nasty wounds and scratches" => new ConditionRange(30, 50, descriptor),
            "in awful condition" => new ConditionRange(0, 15, descriptor),
            _ => null
        };

    private static DamageObservation CreateDamage(
        CombatActor source,
        string sourceName,
        string targetName,
        Match match)
    {
        string? absolute = match.Groups["absolute"].Success
            ? match.Groups["absolute"].Value.ToLowerInvariant()
            : null;
        int? absoluteTier = absolute is not null && AvendarDamageLexicon.AbsoluteTiers.TryGetValue(absolute, out int tier)
            ? tier
            : null;
        string relative = match.Groups["relative"].Value.ToLowerInvariant();

        return new DamageObservation(
            source,
            sourceName,
            targetName,
            match.Groups["type"].Value.ToLowerInvariant(),
            absolute,
            absoluteTier,
            relative,
            AvendarDamageLexicon.RelativeTiers[relative]);
    }

    private static string NormalizeEquipmentSlot(string slot)
    {
        string normalized = Regex.Replace(slot.Trim().ToLowerInvariant(), @"\s+", " ");
        return normalized switch
        {
            "left finger" or "right finger" or "finger" => "worn on finger",
            "neck" => "worn around neck",
            "torso" => "worn on torso",
            "head" => "worn on head",
            "legs" => "worn on legs",
            "feet" => "worn on feet",
            "hands" => "worn on hands",
            "arms" => "worn on arms",
            "shield" => "worn as shield",
            "body" => "worn about body",
            "waist" => "worn about waist",
            "left wrist" or "right wrist" or "wrist" => "worn around wrist",
            _ => normalized
        };
    }

    [GeneratedRegex(@"^You receive (?<amount>\d+) experience points?\.$")]
    private static partial Regex ExperienceRegex();

    [GeneratedRegex(@"^You gained (?<points>\d+) exploration points? and (?<xp>\d+) experience points?!$")]
    private static partial Regex ExplorationRegex();

    [GeneratedRegex(@"^You gained (?<points>\d+) exploration points?!$", RegexOptions.IgnoreCase)]
    private static partial Regex ExplorationOnlyRegex();

    [GeneratedRegex(@"^You get (?<amount>\d+) (?<currency>copper|silver|gold|platinum) coins? from .+\.$", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyRegex();

    [GeneratedRegex(@"^You split some coins\.\s+Your share is (?<amount>\d+) (?<currency>copper|silver|gold|platinum)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitCurrencyRegex();

    [GeneratedRegex(@"^You get (?<item>.+?) from (?<source>.+?)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex ItemAcquiredRegex();

    [GeneratedRegex(@"^(?<source>.+?) drops (?<item>.+?)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex MobDroppedItemRegex();

    [GeneratedRegex(@"^You are promoted!!\s+You gain (?<hp>\d+) hit points?, (?<mana>\d+) mana, and (?<move>\d+) movement\.$")]
    private static partial Regex PromotionRegex();

    [GeneratedRegex(@"^You have become better at (?<skill>.+?), and receive (?<xp>\d+) experience!$", RegexOptions.IgnoreCase)]
    private static partial Regex SkillImprovedRegex();

    [GeneratedRegex(@"^You are now learned at (?<skill>.+?)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex SkillPracticeRegex();

    [GeneratedRegex(@"^You dual wield (?<item>.+?)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex DualWieldRegex();

    [GeneratedRegex(@"^You hold (?<item>.+?) in your hand\.$", RegexOptions.IgnoreCase)]
    private static partial Regex HoldRegex();

    [GeneratedRegex(@"^You wield (?<item>.+?)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex WieldRegex();

    [GeneratedRegex(@"^You stop using (?<item>.+?)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex StopUsingRegex();

    [GeneratedRegex(@"^You wear (?<item>.+?) (?:on|about|around) your (?<slot>.+?)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex WearRegex();

    [GeneratedRegex(@"^You open the .+? door to the (?<direction>north|east|south|west|up|down)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex DoorOpenedRegex();

    [GeneratedRegex(@"^The .+? door is closed\.$", RegexOptions.IgnoreCase)]
    private static partial Regex ClosedDoorRegex();

    [GeneratedRegex(@"^(?:The .+? door is locked|It is locked)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex LockedDoorRegex();

    [GeneratedRegex(@"^Your (?<type>\S+) misses (?<target>.+)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerMissRegex();

    [GeneratedRegex(@"^(?<source>.+?)'s (?<type>\S+) misses you\.$", RegexOptions.IgnoreCase)]
    private static partial Regex OpponentMissRegex();

    [GeneratedRegex(@"^(?<target>.+?) barely dodges your attack\.$", RegexOptions.IgnoreCase)]
    private static partial Regex PlayerDodgedRegex();

    [GeneratedRegex(@"^(?<target>.+?) leaves (?<direction>north|east|south|west|up|down)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex OccupantDepartureRegex();

    [GeneratedRegex(@"^(?<target>.+?) (?:is DEAD!!|is DESTROYED!)$", RegexOptions.IgnoreCase)]
    private static partial Regex DeathRegex();

    [GeneratedRegex(@"^You (?:quickly )?destroy the corpse of (?<target>.+?)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex CorpseDestroyedRegex();

    [GeneratedRegex(@"^No way!\s+You are still fighting!$", RegexOptions.IgnoreCase)]
    private static partial Regex CombatMovementRestrictionRegex();

    [GeneratedRegex(@"^You feel a slight tingling\.$", RegexOptions.IgnoreCase)]
    private static partial Regex TeleportCueRegex();

    [GeneratedRegex(@"^You follow (?<target>.+?)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex FollowMovementRegex();

    [GeneratedRegex(@"^You flee from combat!$", RegexOptions.IgnoreCase)]
    private static partial Regex FleeMovementRegex();

    [GeneratedRegex(@"^You crawl (?<direction>north|east|south|west|up|down)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex CrawlMovementRegex();

    [GeneratedRegex(@"^You enter (?:the |a )?.*portal\.$", RegexOptions.IgnoreCase)]
    private static partial Regex PortalMovementRegex();

    [GeneratedRegex(@"^.+ has summoned you!$", RegexOptions.IgnoreCase)]
    private static partial Regex SummonMovementRegex();

    [GeneratedRegex(@"^(?:You are sucked through .+|You ride the powerful suction.+|The powerful suction .+ you .+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ForcedMovementRegex();

    [GeneratedRegex(@"^You offer (?<item>.+?) to (?<deity>[^.]+)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex CorpseSacrificeRegex();

    [GeneratedRegex(@"^You .+?(?<item>heart|eye|hand|finger|tongue|head|brains|hair|entrails).+?corpse of (?<corpse>.+?)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex CorpseHarvestRegex();

    [GeneratedRegex(
        @"^(?<target>.+?) (?<condition>is in excellent condition|has a few scratches|has some small wounds and bruises|has quite a few wounds|has some big nasty wounds and scratches|looks pretty hurt|is in awful condition)\.?\s*\[(?<min>\d+)%(?:-(?<max>\d+)%)?\]\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CombatConditionPercentRegex();

    [GeneratedRegex(@"^(?<target>.+?) (?:(?:looks (?<condition>pretty hurt))|(?:has (?<condition>a few scratches|some small wounds and bruises|quite a few wounds|some big nasty wounds and scratches))|(?:is (?<condition>in excellent condition|in awful condition)))\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex CombatConditionRegex();
}
