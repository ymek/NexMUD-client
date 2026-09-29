using Avalonia;

namespace JevMud.Gui;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                CrashDiagnostics.Record($"Unhandled AppDomain exception (terminating={eventArgs.IsTerminating})", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            CrashDiagnostics.Record("Unobserved task exception", eventArgs.Exception);
            eventArgs.SetObserved();
        };

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            CrashDiagnostics.Record("Fatal application exception", exception);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect();
}

internal sealed record GuiStartupOptions(
    string? Host,
    int? Port,
    bool UseTls,
    string? JevModel,
    bool AutoConnect)
{
    public static GuiStartupOptions Parse(string[] args)
    {
        string? host = null;
        int? port = null;
        bool tls = false;
        bool autoConnect = true;
        string? model = null;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--host":
                    host = RequireValue(args, ref index, "--host");
                    break;
                case "--port":
                    string portValue = RequireValue(args, ref index, "--port");
                    if (!int.TryParse(portValue, out int parsedPort) || parsedPort is < 1 or > 65535)
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
                case "--no-connect":
                    autoConnect = false;
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

        return new GuiStartupOptions(host, port, tls, model, autoConnect);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }
        return args[index];
    }
}
