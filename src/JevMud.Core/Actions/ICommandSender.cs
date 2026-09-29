namespace JevMud.Core.Actions;

public interface ICommandSender
{
    Task SendCommandAsync(string command, CancellationToken cancellationToken = default);
}
