using NexMud.Client.Scripting;
using Assert = NexMud.Tests.Program.Assert;

namespace NexMud.Tests;

internal static class Phase5SourceSearchTests
{
    public static Task HonorsCaseAndWholeWordOptions()
    {
        const string line = "needles needle_box (NEEDLE)";

        Assert.Equal(0, ScriptSourceSearchMatcher.FindFirst(line, "needle", new ScriptSourceSearchOptions()));
        Assert.Equal(-1, ScriptSourceSearchMatcher.FindFirst("Needle", "needle", new ScriptSourceSearchOptions(MatchCase: true)));
        Assert.Equal(20, ScriptSourceSearchMatcher.FindFirst(line, "needle", new ScriptSourceSearchOptions(WholeWord: true)));
        Assert.Equal(-1, ScriptSourceSearchMatcher.FindFirst("needle_box", "needle", new ScriptSourceSearchOptions(WholeWord: true)));

        return Task.CompletedTask;
    }

    public static Task HandlesOverlappingUnicodeAndFilterTerms()
    {
        Assert.Equal(3, ScriptSourceSearchMatcher.FindFirst("xa-a-a", "a-a", new ScriptSourceSearchOptions(WholeWord: true)));
        Assert.Equal(0, ScriptSourceSearchMatcher.FindFirst("élan", "élan", new ScriptSourceSearchOptions(WholeWord: true)));
        Assert.Equal(-1, ScriptSourceSearchMatcher.FindFirst("élan2", "élan", new ScriptSourceSearchOptions(WholeWord: true)));

        string supplementaryLetter = char.ConvertFromUtf32(0x10400);
        string markedLetter = supplementaryLetter + char.ConvertFromUtf32(0x0301);
        string highSurrogate = supplementaryLetter[..1];
        string lowSurrogate = supplementaryLetter[1..];
        ScriptSourceSearchOptions wholeWord = new(WholeWord: true);
        Assert.Equal(-1, ScriptSourceSearchMatcher.FindFirst(supplementaryLetter, highSurrogate, wholeWord));
        Assert.Equal(-1, ScriptSourceSearchMatcher.FindFirst(supplementaryLetter, lowSurrogate, wholeWord));
        Assert.Equal(0, ScriptSourceSearchMatcher.FindFirst(supplementaryLetter, supplementaryLetter, wholeWord));
        Assert.Equal(-1, ScriptSourceSearchMatcher.FindFirst(supplementaryLetter + "needle", "needle", wholeWord));
        Assert.Equal(-1, ScriptSourceSearchMatcher.FindFirst(markedLetter + "needle", "needle", wholeWord));

        ScriptSourceSearchOptions options = new(IncludedTerms: ["combat", "retreat"], ExcludedTerms: ["unsafe"]);
        Assert.True(ScriptSourceSearchMatcher.MatchesFilters("combat retreat routine", options));
        Assert.False(ScriptSourceSearchMatcher.MatchesFilters("combat routine", options));
        Assert.False(ScriptSourceSearchMatcher.MatchesFilters("combat retreat unsafe routine", options));
        return Task.CompletedTask;
    }
}
