namespace NexMud.Contracts.Gameplay;

/// <summary>
/// Produces immutable gameplay observations at the canonical replay/live boundary.
/// Consumers do not distinguish live transport from replay after this contract.
/// </summary>
public interface IGameObservationSource
{
    IAsyncEnumerable<GameObservation> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed class ReplayGameObservationSource : IGameObservationSource
{
    private readonly IReadOnlyList<GameObservation> _observations;

    public ReplayGameObservationSource(IEnumerable<GameObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        _observations = observations.ToArray();
    }

    public async IAsyncEnumerable<GameObservation> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (GameObservation observation in _observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return observation;
            await Task.Yield();
        }
    }
}
