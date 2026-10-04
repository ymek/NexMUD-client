using System.Text.Json;
using NexMud.Gui.AutomationStudio;
using Assert = NexMud.Tests.Program.Assert;

namespace NexMud.Tests;

internal static class StudioSearchMatcherTests
{
    public static Task MatchesCaseAndWholeWordOptions()
    {
        Assert.True(StudioSearchMatcher.Contains("Flee from danger", "flee", matchCase: false, wholeWord: false));
        Assert.False(StudioSearchMatcher.Contains("Flee from danger", "flee", matchCase: true, wholeWord: false));
        Assert.False(StudioSearchMatcher.Contains("needle_box", "needle", matchCase: false, wholeWord: true));
        Assert.True(StudioSearchMatcher.Contains("(needle)", "needle", matchCase: false, wholeWord: true));
        Assert.True(StudioSearchMatcher.Contains("needles then needle", "needle", matchCase: false, wholeWord: true));
        Assert.False(StudioSearchMatcher.Contains(null, "needle", matchCase: false, wholeWord: false));
        Assert.False(StudioSearchMatcher.Contains("anything", string.Empty, matchCase: false, wholeWord: false));
        return Task.CompletedTask;
    }

    public static Task AppliesIncludeAndExcludeTerms()
    {
        string[] included = StudioSearchMatcher.ParseTerms("combat, flee; combat");
        string[] excluded = StudioSearchMatcher.ParseTerms("unsafe\nblocked");

        Assert.Equal(2, included.Length);
        Assert.True(StudioSearchMatcher.MatchesFilters("combat flee routine", included, excluded, false, false));
        Assert.False(StudioSearchMatcher.MatchesFilters("combat routine", included, excluded, false, false));
        Assert.False(StudioSearchMatcher.MatchesFilters("combat flee unsafe routine", included, excluded, false, false));
        return Task.CompletedTask;
    }

    public static Task RanksExactAndTitleMatchesFirst()
    {
        Assert.Equal(0, StudioSearchMatcher.Rank("flee", "flee", false));
        Assert.Equal(1, StudioSearchMatcher.Rank("flee helper", "flee", false));
        Assert.Equal(2, StudioSearchMatcher.Rank("combat flee helper", "flee", false));
        Assert.Equal(3, StudioSearchMatcher.Rank("combat helper", "flee", false));
        return Task.CompletedTask;
    }

    public static Task SavedSearchRoundTripsScopesAndFilters()
    {
        StudioSavedSearch saved = new("Combat", "flee", Automations: false, Workflows: true, Scripts: false,
            Descriptions: true, ScriptContent: false, MatchCase: true, WholeWords: true,
            IncludeTerms: "danger, retreat", ExcludeTerms: "unsafe");
        StudioSavedSearch? restored = JsonSerializer.Deserialize<StudioSavedSearch>(JsonSerializer.Serialize(saved));
        Assert.Equal(saved, restored);
        StudioSavedSearch? legacy = JsonSerializer.Deserialize<StudioSavedSearch>("{\"Name\":\"Legacy\",\"Query\":\"retreat\"}");
        Assert.True(legacy is { Automations: true, Workflows: true, Scripts: true });
        return Task.CompletedTask;
    }
}
