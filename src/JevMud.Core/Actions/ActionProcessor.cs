using System.Threading.Channels;
using JevMud.Contracts.Actions;
using JevMud.Contracts.Events;
using JevMud.Contracts.Jev;
using JevMud.Core.Events;
using JevMud.Core.Jev;
using JevMud.Core.State;

namespace JevMud.Core.Actions;

public sealed class ActionProcessor
{
    private readonly Channel<MudActionEnvelope> _actions;
    private readonly ICommandSender _sender;
    private readonly StateReducer _state;
    private readonly JevAuthorityService _authority;
    private readonly IEventSink _events;

    public ActionProcessor(
        ICommandSender sender,
        StateReducer state,
        JevAuthorityService authority,
        IEventSink events)
    {
        _sender = sender;
        _state = state;
        _authority = authority;
        _events = events;
        _actions = Channel.CreateBounded<MudActionEnvelope>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public ValueTask QueueAsync(MudActionEnvelope action, CancellationToken cancellationToken = default) =>
        _actions.Writer.WriteAsync(action, cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (MudActionEnvelope envelope in _actions.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await ProcessAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessAsync(MudActionEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.Action is not SendCommandAction command)
        {
            await RejectAsync(envelope, "Unsupported action type.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (envelope.Source is DecisionSource.Jev or DecisionSource.Hybrid)
        {
            if (!_authority.Enabled)
            {
                await RejectAsync(envelope, "Jev is disabled by the master control.", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (envelope.Domain is null)
            {
                await RejectAsync(envelope, "Jev actions require a domain.", cancellationToken).ConfigureAwait(false);
                return;
            }

            JevAuthority authority = _authority.Get(envelope.Domain.Value);
            if (authority is JevAuthority.Off or JevAuthority.Observe or JevAuthority.Suggest)
            {
                await RejectAsync(envelope, $"{envelope.Domain} authority is {authority}.", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            if (authority == JevAuthority.Approve && !envelope.UserApproved)
            {
                await RejectAsync(envelope, "User approval is required.", cancellationToken).ConfigureAwait(false);
                return;
            }

            long currentVersion = _state.Current.Version;
            if (envelope.StateVersion != currentVersion)
            {
                await RejectAsync(
                    envelope,
                    $"Decision state v{envelope.StateVersion} is stale; current state is v{currentVersion}.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await _events.PublishAsync(
                new ActionValidated(envelope.ActionId, envelope.StateVersion, currentVersion),
                "actions",
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            string observableCommand = command.Sensitive ? "<redacted>" : command.Command;
            CommandProvenance provenance = envelope.Provenance ?? InferProvenance(envelope.Source);
            await _events.PublishAsync(
                new ActionDispatching(envelope.ActionId, observableCommand, command.Sensitive, envelope.Source, provenance),
                "actions",
                cancellationToken).ConfigureAwait(false);

            await _sender.SendCommandAsync(command.Command, cancellationToken).ConfigureAwait(false);
            await _events.PublishAsync(
                new ActionExecuted(envelope.ActionId, observableCommand, command.Sensitive, envelope.Source, provenance),
                "actions",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RejectAsync(envelope, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }


    private static CommandProvenance InferProvenance(DecisionSource source) => source switch
    {
        DecisionSource.Human => new CommandProvenance(CommandOrigin.User, "user", "User"),
        DecisionSource.Jev or DecisionSource.Hybrid => new CommandProvenance(CommandOrigin.Jev, "jev", "Jev"),
        _ => new CommandProvenance(CommandOrigin.System, "rules", "Rules")
    };

    private Task RejectAsync(
        MudActionEnvelope envelope,
        string reason,
        CancellationToken cancellationToken) =>
        _events.PublishAsync(new ActionRejected(envelope.ActionId, reason), "actions", cancellationToken).AsTask();
}
