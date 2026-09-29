using System.Collections.ObjectModel;
using NexMud.Contracts.State;

namespace NexMud.Adapters.Avendar;

public enum ActionEligibility
{
    Eligible,
    Ineligible,
    Unknown
}

public enum SkillTargetRequirement
{
    None,
    Required
}

public sealed record SkillActionDefinition(
    string SkillName,
    string DecisionAction,
    string CommandTemplate,
    decimal ActivationLagRounds,
    int ManaCost,
    SkillTargetRequirement TargetRequirement,
    string ExpectedEffect);

public sealed record ActionEligibilityResult(
    string SkillName,
    ActionEligibility Eligibility,
    IReadOnlyList<string> Reasons);

public static class AvendarActionCapabilities
{
    public static IReadOnlyDictionary<string, SkillActionDefinition> KnownActions { get; } =
        new ReadOnlyDictionary<string, SkillActionDefinition>(
            new Dictionary<string, SkillActionDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["dirt kicking"] = new(
                    "dirt kicking",
                    "dirt_kicking",
                    "dirt <target>",
                    1.5m,
                    5,
                    SkillTargetRequirement.Required,
                    "Temporarily blinds the target."),
                ["recover"] = new(
                    "recover",
                    "recover",
                    "recover",
                    1.0m,
                    0,
                    SkillTargetRequirement.None,
                    "Restores some lost hit points."),
                ["backstab"] = new(
                    "backstab",
                    "backstab",
                    "backstab <target>",
                    2.0m,
                    8,
                    SkillTargetRequirement.Required,
                    "Initiates combat with a high-damage strike against an unsuspecting target.")
            });

    public static ActionEligibilityResult Evaluate(
        SkillActionDefinition definition,
        StateSnapshot state,
        bool hasTarget,
        bool? equippedWeaponIsBlunt = null)
    {
        SkillState? skill = state.Character.Skills.FirstOrDefault(
            value => value.Name.Equals(definition.SkillName, StringComparison.OrdinalIgnoreCase));

        if (skill is null)
        {
            return Result(definition.SkillName, ActionEligibility.Unknown, "Skill state has not been observed.");
        }

        if (skill.Availability == SkillAvailability.Unavailable)
        {
            return Result(definition.SkillName, ActionEligibility.Ineligible, "Skill is not currently available.");
        }

        if (definition.ManaCost > 0)
        {
            if (state.Character.Mana.Current is null)
            {
                return Result(definition.SkillName, ActionEligibility.Unknown, "Current mana is not known.");
            }

            if (state.Character.Mana.Current.Value < definition.ManaCost)
            {
                return Result(definition.SkillName, ActionEligibility.Ineligible, "Insufficient mana.");
            }
        }

        if (definition.TargetRequirement == SkillTargetRequirement.Required && !hasTarget)
        {
            return Result(definition.SkillName, ActionEligibility.Ineligible, "A target is required.");
        }

        if (definition.SkillName.Equals("dirt kicking", StringComparison.OrdinalIgnoreCase))
        {
            string? terrain = state.Room.Terrain?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(terrain))
            {
                return Result(definition.SkillName, ActionEligibility.Unknown, "Current terrain is not known.");
            }

            if (terrain is "water" or "underwater" or "air")
            {
                return Result(
                    definition.SkillName,
                    ActionEligibility.Ineligible,
                    $"Dirt kicking cannot be used in terrain '{terrain}'.");
            }
        }

        if (definition.SkillName.Equals("backstab", StringComparison.OrdinalIgnoreCase))
        {
            if (equippedWeaponIsBlunt is true)
            {
                return Result(definition.SkillName, ActionEligibility.Ineligible, "Backstab requires a non-blunt weapon.");
            }

            if (equippedWeaponIsBlunt is null)
            {
                return Result(definition.SkillName, ActionEligibility.Unknown, "Equipped weapon type is not known.");
            }
        }

        return Result(definition.SkillName, ActionEligibility.Eligible);
    }

    private static ActionEligibilityResult Result(
        string skillName,
        ActionEligibility eligibility,
        params string[] reasons) =>
        new(
            skillName,
            eligibility,
            new ReadOnlyCollection<string>(reasons));
}
