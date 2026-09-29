using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using JevMud.Client.Runtime;
using JevMud.Scripting.Runtime;

namespace JevMud.Gui;

/// <summary>
/// Runtime-oriented scripting surface. User script editing remains intentionally separate from
/// runtime/domain services; this view reports loaded modules and the currently available engines.
/// </summary>
internal sealed class ScriptingWorkspace : UserControl
{
    private readonly JevMudRuntime _runtime;
    private readonly StackPanel _managedModules = new() { Spacing = 4 };
    private readonly StackPanel _javascriptModules = new() { Spacing = 4 };
    private readonly TextBlock _status = new();

    public ScriptingWorkspace(JevMudRuntime runtime)
    {
        _runtime = runtime;
        FontFamily = UiTheme.Sans;
        FontSize = NexTypography.Body;
        Content = Build();
        Refresh();
    }

    public void Refresh()
    {
        _status.Text = $"Managed: {_runtime.Scripting.Runtime.RuntimeName} · JavaScript: {_runtime.Scripting.JavaScriptRuntime.RuntimeName}";
        RenderModules(_managedModules, _runtime.Scripting.Runtime.Snapshot());
        RenderModules(_javascriptModules, _runtime.Scripting.JavaScriptRuntime.Snapshot());
    }

    private Control Build()
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Background = UiTheme.Window,
            Margin = new Thickness(14)
        };

        StackPanel heading = new() { Spacing = 5 };
        heading.Children.Add(new TextBlock
        {
            Text = "Scripting",
            Foreground = UiTheme.Text,
            FontSize = NexTypography.Display,
            FontWeight = FontWeight.SemiBold
        });
        heading.Children.Add(new TextBlock
        {
            Text = "TypeScript authoring · JavaScript execution through Jint · capability-gated NexMUD host API",
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Body,
            TextWrapping = TextWrapping.Wrap
        });
        _status.Foreground = UiTheme.Cyan;
        _status.FontSize = NexTypography.Small;
        heading.Children.Add(_status);
        root.Children.Add(heading);

        Grid columns = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(0, 14, 0, 0)
        };
        columns.Children.Add(ModuleColumn("Managed / generated behavior", _managedModules));
        Control javascript = ModuleColumn("Jint script instances", _javascriptModules);
        Grid.SetColumn(javascript, 1);
        columns.Children.Add(javascript);
        Grid.SetRow(columns, 1);
        root.Children.Add(columns);
        return root;
    }

    private static Border ModuleColumn(string title, StackPanel modules)
    {
        StackPanel content = new() { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = UiTheme.Brass,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold
        });
        content.Children.Add(modules);
        return new Border
        {
            Background = UiTheme.Surface,
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12),
            Child = new ScrollViewer
            {
                Content = content,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            }
        };
    }

    private static void RenderModules(StackPanel target, IReadOnlyList<ScriptModuleSnapshot> modules)
    {
        target.Children.Clear();
        if (modules.Count == 0)
        {
            target.Children.Add(new TextBlock
            {
                Text = "No modules currently loaded.",
                Foreground = UiTheme.Faint,
                FontSize = NexTypography.Body
            });
            return;
        }

        foreach (ScriptModuleSnapshot module in modules)
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            StackPanel identity = new() { Spacing = 2 };
            identity.Children.Add(new TextBlock
            {
                Text = module.Name,
                Foreground = UiTheme.Text,
                FontSize = NexTypography.Body,
                FontWeight = FontWeight.SemiBold
            });
            identity.Children.Add(new TextBlock
            {
                Text = module.Id.Value,
                Foreground = UiTheme.Faint,
                FontSize = NexTypography.Small
            });
            row.Children.Add(identity);
            TextBlock state = new()
            {
                Text = module.CancellationRequested ? "STOPPING" : module.Loaded ? "RUNNING" : "STOPPED",
                Foreground = module.CancellationRequested ? UiTheme.Warning : module.Loaded ? UiTheme.Success : UiTheme.Muted,
                FontSize = NexTypography.Small,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(state, 1);
            row.Children.Add(state);
            target.Children.Add(new Border
            {
                Background = UiTheme.Field,
                BorderBrush = UiTheme.Divider,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(8, 6),
                Child = row
            });
        }
    }
}
