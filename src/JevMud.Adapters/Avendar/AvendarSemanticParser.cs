using System.Text.RegularExpressions;
using JevMud.Contracts.Events;
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

    public IReadOnlyList<IMudEvent> ParseLine(string line)
    {
        string text = line.Trim();
        if (text.Length == 0)
        {
            return [];
        }

        List<IMudEvent> events = [];

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

        Match condition = CombatConditionRegex().Match(text);
        if (condition.Success)
        {
            events.Add(new CombatTargetConditionObserved(
                condition.Groups["target"].Value,
                condition.Groups["condition"].Value));
        }

        return events;
    }

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

    [GeneratedRegex(@"^You get (?<amount>\d+) (?<currency>copper|silver|gold) coins? from .+\.$", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyRegex();

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

    [GeneratedRegex(@"^(?<target>.+?) is DEAD!!$")]
    private static partial Regex DeathRegex();

    [GeneratedRegex(@"^You (?:quickly )?destroy the corpse of (?<target>.+?)\.$", RegexOptions.IgnoreCase)]
    private static partial Regex CorpseDestroyedRegex();

    [GeneratedRegex(@"^(?<target>.+?) (?:(?:looks (?<condition>pretty hurt))|(?:has (?<condition>some small wounds and bruises|quite a few wounds|some big nasty wounds and scratches))|(?:is (?<condition>in awful condition)))\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex CombatConditionRegex();
}
