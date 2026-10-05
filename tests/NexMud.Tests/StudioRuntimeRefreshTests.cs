using NexMud.Gui.AutomationStudio;
using Assert = NexMud.Tests.Program.Assert;

namespace NexMud.Tests;

internal static class StudioRuntimeRefreshTests
{
    public static Task CoalescesUntilRefreshCompletes()
    {
        RuntimeUiRefreshGate gate = new();

        Assert.True(gate.TryQueue(out long generation));
        Assert.False(gate.TryQueue(out _));
        Assert.False(gate.TryQueue(out _));

        Assert.True(gate.Complete(generation));
        Assert.False(gate.TryQueue(out _));
        Assert.True(gate.Complete(generation));
        Assert.False(gate.Complete(generation));
        Assert.True(gate.TryQueue(out _));
        return Task.CompletedTask;
    }

    public static Task ProfileInvalidationIgnoresStaleRefreshCompletion()
    {
        RuntimeUiRefreshGate gate = new();
        Assert.True(gate.TryQueue(out long oldGeneration));

        gate.Invalidate();

        Assert.True(gate.TryQueue(out long currentGeneration));
        Assert.True(currentGeneration > oldGeneration);
        Assert.False(gate.Complete(oldGeneration));
        Assert.False(gate.TryQueue(out _));
        Assert.True(gate.Complete(currentGeneration));
        Assert.False(gate.Complete(currentGeneration));
        return Task.CompletedTask;
    }
}
