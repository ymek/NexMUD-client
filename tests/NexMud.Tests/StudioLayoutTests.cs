using Avalonia.Controls;
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

    public static Task MonacoVisibilityTracksEditorMode()
    {
        Border monaco = new();
        Border center = new();

        AutomationStudioWindow.ApplyMonacoVisibility(monaco, center, show: false);
        Assert.False(monaco.IsVisible);
        Assert.Equal(0d, monaco.MaxHeight);
        Assert.True(center.IsVisible);

        AutomationStudioWindow.ApplyMonacoVisibility(monaco, center, show: true);
        Assert.True(monaco.IsVisible);
        Assert.Equal(double.PositiveInfinity, monaco.MaxHeight);
        Assert.False(center.IsVisible);
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
