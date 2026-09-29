using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using NexMud.Client.Runtime;
using NexMud.Contracts.Transport;

namespace NexMud.Gui;

public sealed class App : Application
{
    private NexMudRuntime? _runtime;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _mainWindow;
    private int _shutdownState;

    public App()
    {
        Name = "NexMUD";
    }

    public override void Initialize()
    {
        Name = "NexMUD";
        RequestedThemeVariant = ThemeVariant.Dark;
        Resources["SystemAccentColor"] = UiTheme.AccentColor;
        NexMudTheme.Install(this);
        Styles.Add(new FluentTheme { DensityStyle = DensityStyle.Compact });
        ConfigureNativeApplicationMenu();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException -= HandleUnhandledUiException;
        Dispatcher.UIThread.UnhandledException += HandleUnhandledUiException;
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        GuiStartupOptions options;
        try
        {
            options = GuiStartupOptions.Parse(desktop.Args ?? Array.Empty<string>());
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            desktop.Shutdown(2);
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // Window close / Command-Q first cancel the platform request, perform bounded async
        // cleanup, then explicitly terminate. This prevents the macOS UI thread from blocking.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _desktop = desktop;
        _runtime = new NexMudRuntime();
        _runtime.Start();
        _runtime.RestoreSettingsAsync(_runtime.CancellationToken).GetAwaiter().GetResult();

        _runtime.InitializeDecisionEngineAsync(
            Environment.GetEnvironmentVariable("TYPESAFE_API_KEY"),
            options.JevModel,
            _runtime.CancellationToken).GetAwaiter().GetResult();

        MainWindow window = new(_runtime);
        _mainWindow = window;
        desktop.MainWindow = window;

        // macOS Command-Q is surfaced through ShutdownRequested. Never synchronously wait
        // for sockets, workers, settings, or SQLite on the UI thread.
        desktop.ShutdownRequested += HandleShutdownRequested;
        window.Closing += HandleMainWindowClosing;
        desktop.Exit += (_, _) =>
        {
            window.Closing -= HandleMainWindowClosing;
            desktop.ShutdownRequested -= HandleShutdownRequested;
            _mainWindow = null;
            _desktop = null;
        };

        if (options.AutoConnect)
        {
            string? host = options.Host ?? _runtime.Settings.Host;
            int port = options.Port ?? _runtime.Settings.Port;
            bool tls = options.Host is null ? _runtime.Settings.UseTls : options.UseTls;
            if (!string.IsNullOrWhiteSpace(host) && port is >= 1 and <= 65535)
            {
                _ = ConnectAsync(_runtime, host, port, tls);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void HandleShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (Volatile.Read(ref _shutdownState) == 2)
        {
            return;
        }

        e.Cancel = true;
        _ = BeginShutdownAsync();
    }

    private void HandleMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (Volatile.Read(ref _shutdownState) == 2 || e.CloseReason == WindowCloseReason.OSShutdown)
        {
            return;
        }

        e.Cancel = true;
        _ = BeginShutdownAsync();
    }

    private async Task BeginShutdownAsync()
    {
        if (Interlocked.CompareExchange(ref _shutdownState, 1, 0) != 0)
        {
            return;
        }

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            if (_mainWindow is not null)
            {
                try
                {
                    await _mainWindow.PrepareForShutdownAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    // Workspace persistence must never trap the application during quit.
                }
            }

            NexMudRuntime? runtime = Interlocked.Exchange(ref _runtime, null);
            if (runtime is not null)
            {
                try
                {
                    await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), timeout.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (TimeoutException)
                {
                }
                catch
                {
                    // Shutdown is best-effort after cancellation; the process must still exit.
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _shutdownState, 2);
            IClassicDesktopStyleApplicationLifetime? desktop = _desktop;
            if (desktop is not null)
            {
                Dispatcher.UIThread.Post(() => desktop.Shutdown(0));
            }
        }
    }

    private static async Task ConnectAsync(NexMudRuntime runtime, string host, int port, bool tls)
    {
        try
        {
            await runtime.Transport.ConnectAsync(
                runtime.CreateConnectionOptions(host, port, tls),
                runtime.CancellationToken).ConfigureAwait(false);
            await runtime.SaveConnectionSettingsAsync(host, port, tls, runtime.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runtime.CancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // The transport publishes connection failures through the event stream.
        }
    }

    private static void HandleUnhandledUiException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashDiagnostics.Record("Unhandled Avalonia UI exception", e.Exception);
        e.Handled = true;
    }

    private void ConfigureNativeApplicationMenu()
    {
        NativeMenu menu = new();
        NativeMenuItem about = new("About NexMUD…");
        about.Click += (_, _) => _mainWindow?.ShowAbout();
        menu.Add(about);

        NativeMenuItem settings = new("Settings…");
        settings.Click += async (_, _) =>
        {
            if (_mainWindow is not null)
            {
                await _mainWindow.ShowSettingsAsync();
            }
        };
        menu.Add(settings);
        NativeMenu.SetMenu(this, menu);
    }
}
