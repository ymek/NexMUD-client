using NexMud.Gui.AutomationStudio;
using Assert = NexMud.Tests.Program.Assert;

namespace NexMud.Tests;

internal static class StudioLayoutTests
{
    public static Task RuntimeSkipsWorkspaceNavigator()
    {
        Assert.False(StudioActivityModel.UsesWorkspaceNavigator(StudioActivity.Runtime));
        Assert.True(StudioActivityModel.UsesWorkspaceNavigator(StudioActivity.Search));
        Assert.True(StudioActivityModel.UsesWorkspaceNavigator(StudioActivity.Automations));
        return Task.CompletedTask;
    }

    public static Task SearchWorkspaceColumnsParse()
    {
        var columns = AutomationStudioWindow.CreateSearchWorkspaceColumns();

        Assert.Equal(2, columns.Count);
        Assert.True(columns[0].Width.IsStar);
        Assert.True(columns[1].Width.IsStar);
        return Task.CompletedTask;
    }
}
