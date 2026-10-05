using System.Threading.Channels;

namespace NexMud.Gui.AutomationStudio;

internal static class RuntimeEventBatchReader
{
    public const int MaximumBatchSize = 16;

    public static async ValueTask<T[]> ReadBatchAsync<T>(
        ChannelReader<T> reader,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);

        if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return [];

        List<T> batch = new(maximumCount);
        while (batch.Count < maximumCount && reader.TryRead(out T? item))
            batch.Add(item);
        return batch.ToArray();
    }
}
