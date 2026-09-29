using NexMud.Client.Knowledge;

namespace NexMud.Client.Interaction;

public sealed class KnowledgeCommandHistoryPersistence : ICommandHistoryPersistence
{
    private readonly WorldKnowledgeStore _knowledge;

    public KnowledgeCommandHistoryPersistence(WorldKnowledgeStore knowledge)
    {
        _knowledge = knowledge;
    }

    public Task<IReadOnlyList<string>> LoadAsync(int limit, CancellationToken cancellationToken = default) =>
        _knowledge.GetRecentCommandsAsync(Math.Clamp(limit, 1, 10_000), cancellationToken);

    public Task SaveAsync(string command, bool sentToMud, CancellationToken cancellationToken = default) =>
        _knowledge.RecordCommandAsync(command, sentToMud, cancellationToken);
}
