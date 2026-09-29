using NexMud.Contracts.Transport;
using NexMud.Client.Runtime;
using NexMud.Tui.Views;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace NexMud.Tui;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(argument => argument is "--help" or "-h"))
        {
            Console.WriteLine(StartupOptions.Usage);
            return 0;
        }

        StartupOptions options;
        try
        {
            options = StartupOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(StartupOptions.Usage);
            return 2;
        }

        if (Application.DefaultKeyBindings is not null)
        {
            Application.DefaultKeyBindings[Command.Quit] = Bind.All(Key.Q.WithCtrl);
        }

        await using NexMudRuntime runtime = new();
        runtime.Start();
        await runtime.RestoreSettingsAsync(runtime.CancellationToken).ConfigureAwait(false);

        await runtime.InitializeDecisionEngineAsync(
            Environment.GetEnvironmentVariable("TYPESAFE_API_KEY"),
            options.JevModel,
            runtime.CancellationToken).ConfigureAwait(false);

        using IApplication app = Application.Create().Init();
        using MainWindow window = new(runtime);

        if (options.Host is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await runtime.Transport.ConnectAsync(
                        runtime.CreateConnectionOptions(options.Host, options.Port!.Value, options.UseTls),
                        runtime.CancellationToken).ConfigureAwait(false);
                    await runtime.SaveConnectionSettingsAsync(
                        options.Host,
                        options.Port.Value,
                        options.UseTls,
                        runtime.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runtime.CancellationToken.IsCancellationRequested)
                {
                }
                catch
                {
                    // Connection failures are already published through the event pipeline.
                }
            }, CancellationToken.None);
        }

        app.Run(window);
        return 0;
    }
}

internal sealed record StartupOptions(string? Host, int? Port, bool UseTls, string? JevModel)
{
    public const string Usage = "Usage: nexmud [--host HOST --port PORT [--tls]] [--jev-model MODEL]";

    public static StartupOptions Parse(string[] args)
    {
        string? host = null;
        int? port = null;
        bool tls = false;
        string? model = null;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--host":
                    host = RequireValue(args, ref index, "--host");
                    break;
                case "--port":
                    string portText = RequireValue(args, ref index, "--port");
                    if (!int.TryParse(portText, out int parsedPort) || parsedPort is < 1 or > 65535)
                    {
                        throw new ArgumentException("--port must be between 1 and 65535.");
                    }
                    port = parsedPort;
                    break;
                case "--tls":
                    tls = true;
                    break;
                case "--jev-model":
                    model = RequireValue(args, ref index, "--jev-model");
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        if ((host is null) != (port is null))
        {
            throw new ArgumentException("--host and --port must be provided together.");
        }
        if (tls && host is null)
        {
            throw new ArgumentException("--tls requires --host and --port.");
        }
        if (model is not null && string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("--jev-model requires a non-empty value.");
        }

        return new StartupOptions(host, port, tls, model);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }
        index++;
        return args[index];
    }
}
