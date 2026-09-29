using JevMud.Contracts.Jev;

namespace JevMud.Jev;

public interface IJevDecisionEngine
{
    Task<JevDecisionTrace> EvaluateChoiceAsync(
        JevChoiceRequest request,
        CancellationToken cancellationToken = default);
}
