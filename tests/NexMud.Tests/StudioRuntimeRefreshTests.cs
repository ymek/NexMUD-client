using System.Threading.Channels;
using NexMud.Contracts.Events;
using NexMud.Gui.AutomationStudio;
using Assert = NexMud.Tests.Program.Assert;

namespace NexMud.Tests;

internal static class StudioRuntimeRefreshTests
{
    public static async Task ReadsBoundedEventBatchesInOrder()
    {
        Channel<EventEnvelope> channel = Channel.CreateUnbounded<EventEnvelope>();
        int eventCount = RuntimeEventBatchReader.MaximumBatchSize + 1;
        for (long sequence = 1; sequence <= eventCount; sequence++)
            Assert.True(channel.Writer.TryWrite(CreateEnvelope(sequence)));
        channel.Writer.Complete();

        EventEnvelope[] first = await RuntimeEventBatchReader.ReadBatchAsync(
            channel.Reader, RuntimeEventBatchReader.MaximumBatchSize, CancellationToken.None);
        EventEnvelope[] second = await RuntimeEventBatchReader.ReadBatchAsync(
            channel.Reader, RuntimeEventBatchReader.MaximumBatchSize, CancellationToken.None);
        EventEnvelope[] completed = await RuntimeEventBatchReader.ReadBatchAsync(
            channel.Reader, RuntimeEventBatchReader.MaximumBatchSize, CancellationToken.None);

        long[] expected = Enumerable.Range(1, RuntimeEventBatchReader.MaximumBatchSize)
            .Select(value => (long)value)
            .ToArray();
        Assert.Equal(RuntimeEventBatchReader.MaximumBatchSize, first.Length);
        Assert.SequenceEqual(expected, first.Select(envelope => envelope.Sequence));
        Assert.Equal((long)eventCount, second.Single().Sequence);
        Assert.Equal(0, completed.Length);
    }

    public static async Task ReadsEventsArrivingAfterWaitAndHonorsCancellation()
    {
        Channel<EventEnvelope> channel = Channel.CreateUnbounded<EventEnvelope>();
        ValueTask<EventEnvelope[]> pendingRead = RuntimeEventBatchReader.ReadBatchAsync(
            channel.Reader, RuntimeEventBatchReader.MaximumBatchSize, CancellationToken.None);
        Assert.True(channel.Writer.TryWrite(CreateEnvelope(1)));
        Assert.Equal(1L, (await pendingRead).Single().Sequence);

        using CancellationTokenSource cancellation = new();
        ValueTask<EventEnvelope[]> cancelledRead = RuntimeEventBatchReader.ReadBatchAsync(
            channel.Reader, RuntimeEventBatchReader.MaximumBatchSize, cancellation.Token);
        cancellation.Cancel();

        bool wasCancelled = false;
        try
        {
            await cancelledRead;
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
        }
        Assert.True(wasCancelled);
    }

    private static EventEnvelope CreateEnvelope(long sequence) =>
        new(Guid.NewGuid(), sequence, DateTimeOffset.UnixEpoch, "test", new TestRuntimeEvent());

    private sealed record TestRuntimeEvent : IMudEvent;

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
