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
}
