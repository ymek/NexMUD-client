using NexMud.Contracts.Jev;

namespace NexMud.Jev;

public interface IJevDecisionEngine
{
    Task<JevDecisionTrace> EvaluateChoiceAsync(
        JevChoiceRequest request,
        CancellationToken cancellationToken = default);
}
