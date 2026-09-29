using JevMud.Contracts.State;

namespace JevMud.Adapters.Avendar;

internal static class AvendarAbilityClassifier
{
    private static readonly IReadOnlyDictionary<string, AbilityDomain> KnownDomains =
        new Dictionary<string, AbilityDomain>(StringComparer.OrdinalIgnoreCase)
        {
            ["dagger"] = AbilityDomain.Combat,
            ["sword"] = AbilityDomain.Combat,
            ["whip"] = AbilityDomain.Combat,
            ["hand to hand"] = AbilityDomain.Combat,
            ["backstab"] = AbilityDomain.Combat,
            ["dirt kicking"] = AbilityDomain.Combat,
            ["recover"] = AbilityDomain.Recovery,
            ["peek"] = AbilityDomain.Utility,
            ["steal"] = AbilityDomain.Utility
        };

    public static AbilityDomain Classify(string name) =>
        KnownDomains.TryGetValue(name, out AbilityDomain domain)
            ? domain
            : AbilityDomain.Unknown;
}
