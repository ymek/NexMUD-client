using NexMud.Contracts.Events;
using NexMud.Contracts.Jev;
using NexMud.Core.Events;

namespace NexMud.Core.Jev;

public sealed class JevAuthorityService
{
    private readonly IEventSink _events;
    private readonly object _lock = new();
    private JevAuthoritySnapshot _current;
    private bool _enabled = true;

    public JevAuthorityService(IEventSink events)
    {
        _events = events;
        _current = CreatePreset(JevPreset.Off);
    }

    public bool Enabled
    {
        get
        {
            lock (_lock)
            {
                return _enabled;
            }
        }
    }

    public JevAuthoritySnapshot Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public JevAuthority Get(JevDomain domain)
    {
        lock (_lock)
        {
            return _current.Domains[domain];
        }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        bool changed;
        lock (_lock)
        {
            changed = _enabled != enabled;
            _enabled = enabled;
        }
        if (changed)
        {
            await _events.PublishAsync(new JevEnabledChanged(enabled), "jev.authority", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task ApplyPresetAsync(JevPreset preset, CancellationToken cancellationToken = default)
    {
        if (preset == JevPreset.Custom)
        {
            throw new ArgumentException("Custom is derived from individual domain changes and cannot be applied directly.", nameof(preset));
        }

        JevAuthoritySnapshot snapshot = CreatePreset(preset);
        lock (_lock)
        {
            _current = snapshot;
        }

        await _events.PublishAsync(new JevAuthorityProfileChanged(snapshot), "jev.authority", cancellationToken)
            .ConfigureAwait(false);
    }


    public async Task ApplyProfileAsync(
        JevAuthoritySnapshot profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        JevDomain[] domains = Enum.GetValues<JevDomain>();
        if (profile.Domains.Count != domains.Length || domains.Any(domain => !profile.Domains.ContainsKey(domain)))
        {
            throw new ArgumentException("Authority profile must define every Jev domain.", nameof(profile));
        }

        JevAuthoritySnapshot snapshot = JevAuthoritySnapshot.Create(
            profile.Preset,
            profile.Domains.ToDictionary(pair => pair.Key, pair => pair.Value));

        lock (_lock)
        {
            _current = snapshot;
        }

        await _events.PublishAsync(new JevAuthorityProfileChanged(snapshot), "jev.authority", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetDomainAsync(
        JevDomain domain,
        JevAuthority authority,
        CancellationToken cancellationToken = default)
    {
        JevAuthoritySnapshot snapshot;
        lock (_lock)
        {
            Dictionary<JevDomain, JevAuthority> domains = new(_current.Domains)
            {
                [domain] = authority
            };
            snapshot = JevAuthoritySnapshot.Create(JevPreset.Custom, domains);
            _current = snapshot;
        }

        await _events.PublishAsync(new JevAuthorityProfileChanged(snapshot), "jev.authority", cancellationToken)
            .ConfigureAwait(false);
    }

    public static JevAuthoritySnapshot CreatePreset(JevPreset preset)
    {
        Dictionary<JevDomain, JevAuthority> values = Enum.GetValues<JevDomain>()
            .ToDictionary(domain => domain, _ => JevAuthority.Off);

        switch (preset)
        {
            case JevPreset.Off:
                break;
            case JevPreset.Copilot:
                values[JevDomain.Combat] = JevAuthority.Suggest;
                values[JevDomain.Navigation] = JevAuthority.Suggest;
                values[JevDomain.Inventory] = JevAuthority.Observe;
                values[JevDomain.Loot] = JevAuthority.Suggest;
                values[JevDomain.Quests] = JevAuthority.Observe;
                values[JevDomain.Training] = JevAuthority.Observe;
                values[JevDomain.Recovery] = JevAuthority.Suggest;
                break;
            case JevPreset.Bot:
                // Only domains with executable, revalidated projectors may be autonomous.
                values[JevDomain.Combat] = JevAuthority.Auto;
                values[JevDomain.Navigation] = JevAuthority.Auto;
                values[JevDomain.Recovery] = JevAuthority.Auto;
                values[JevDomain.Inventory] = JevAuthority.Observe;
                values[JevDomain.Loot] = JevAuthority.Observe;
                values[JevDomain.Quests] = JevAuthority.Observe;
                values[JevDomain.Training] = JevAuthority.Observe;
                values[JevDomain.Social] = JevAuthority.Observe;
                break;
            case JevPreset.Autonomous:
                // "Autonomous" means full autonomy across implemented domains, not permission
                // to fabricate commands for domains which do not yet have an action projector.
                values[JevDomain.Combat] = JevAuthority.Auto;
                values[JevDomain.Navigation] = JevAuthority.Auto;
                values[JevDomain.Recovery] = JevAuthority.Auto;
                values[JevDomain.Inventory] = JevAuthority.Observe;
                values[JevDomain.Loot] = JevAuthority.Observe;
                values[JevDomain.Quests] = JevAuthority.Observe;
                values[JevDomain.Training] = JevAuthority.Observe;
                values[JevDomain.Social] = JevAuthority.Observe;
                break;
            case JevPreset.Custom:
                throw new ArgumentException("Custom is not a preset template.", nameof(preset));
            default:
                throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown preset.");
        }

        return JevAuthoritySnapshot.Create(preset, values);
    }
}
