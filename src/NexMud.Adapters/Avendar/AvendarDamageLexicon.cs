using System.Collections.ObjectModel;

namespace NexMud.Adapters.Avendar;

public static class AvendarDamageLexicon
{
    public static IReadOnlyDictionary<string, int> AbsoluteTiers { get; } =
        ReadOnly(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["pathetic"] = 1, ["pitiful"] = 1, ["feeble"] = 1, ["weak"] = 1, ["paltry"] = 1,
            ["inadequate"] = 2, ["mediocre"] = 2, ["average"] = 2, ["telling"] = 2,
            ["strong"] = 3, ["potent"] = 3, ["forceful"] = 3, ["powerful"] = 3, ["fierce"] = 3,
            ["vicious"] = 4, ["brutal"] = 4, ["mighty"] = 4, ["fearsome"] = 4, ["ferocious"] = 4,
            ["formidable"] = 5, ["tremendous"] = 5, ["heroic"] = 5, ["titanic"] = 5, ["godlike"] = 5
        });

    public static IReadOnlyDictionary<string, int> RelativeTiers { get; } =
        ReadOnly(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["barely scratches"] = 1, ["scratches"] = 1, ["grazes"] = 1, ["hurts"] = 1, ["hits"] = 1, ["injures"] = 1,
            ["wounds"] = 2, ["damages"] = 2, ["harms"] = 2, ["tears into"] = 2, ["rends"] = 2, ["mauls"] = 2,
            ["savages"] = 3, ["mutilates"] = 3, ["maims"] = 3, ["mangles"] = 3, ["devastates"] = 3, ["dismembers"] = 3,
            ["ravages"] = 4, ["sunders"] = 4, ["kills"] = 4,
            ["slays"] = 5, ["slaughters"] = 5, ["destroys"] = 5, ["butchers"] = 5
        });

    public static IReadOnlyList<string> RelativeTermsLongestFirst { get; } =
        new ReadOnlyCollection<string>(
            RelativeTiers.Keys
                .OrderByDescending(value => value.Length)
                .ToArray());

    private static IReadOnlyDictionary<string, int> ReadOnly(Dictionary<string, int> values) =>
        new ReadOnlyDictionary<string, int>(values);
}
