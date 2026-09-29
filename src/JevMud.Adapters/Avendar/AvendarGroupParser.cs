using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using JevMud.Contracts.Gameplay;

namespace JevMud.Adapters.Avendar;

public static partial class AvendarGroupParser
{
    public static bool TryParse(
        IReadOnlyList<string> lines,
        long sourceSequence,
        out GroupSnapshot? snapshot)
    {
        snapshot = null;
        string? leader = null;
        List<GroupMemberSnapshot> members = [];

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();
            Match header = HeaderRegex().Match(line.Trim());
            if (header.Success)
            {
                leader = header.Groups["leader"].Value.Trim();
                continue;
            }

            Match member = MemberRegex().Match(line);
            if (!member.Success)
            {
                continue;
            }

            members.Add(new GroupMemberSnapshot(
                member.Groups["name"].Value.Trim(),
                int.Parse(member.Groups["level"].Value),
                member.Groups["class"].Value.Trim(),
                Pair(member, "hp", "maxhp"),
                Pair(member, "mana", "maxmana"),
                Pair(member, "move", "maxmove"),
                sourceSequence));
        }

        if (members.Count == 0)
        {
            return false;
        }

        snapshot = new GroupSnapshot(
            leader,
            new ReadOnlyCollection<GroupMemberSnapshot>(members.ToArray()),
            sourceSequence);
        return true;
    }

    private static ResourceValue Pair(Match match, string current, string maximum) =>
        new(int.Parse(match.Groups[current].Value), int.Parse(match.Groups[maximum].Value));

    [GeneratedRegex(@"^(?<leader>.+?)'s group:$", RegexOptions.IgnoreCase)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(
        @"^\s*\[(?<level>\d+)\s+(?<class>[^\]]+)\]\s+" +
        @"(?<name>.+?)\s+" +
        @"(?<hp>-?\d+)\s*/\s*(?<maxhp>-?\d+)\s+hp\s+" +
        @"(?<mana>-?\d+)\s*/\s*(?<maxmana>-?\d+)\s+mana\s+" +
        @"(?<move>-?\d+)\s*/\s*(?<maxmove>-?\d+)\s+mv\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex MemberRegex();
}
