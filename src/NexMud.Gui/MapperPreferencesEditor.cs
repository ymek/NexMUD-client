using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NexMud.Client.Runtime;
using NexMud.Client.Settings;

namespace NexMud.Gui;

/// <summary>
/// Map-owned preferences. These settings intentionally live with the Mapper instead of the
/// global Settings workspace because they control map acquisition, routing and auto-move.
/// </summary>
internal sealed class MapperPreferencesEditor : UserControl
{
    private readonly NexMudRuntime _runtime;
    private readonly Func<Task> _close;
    private readonly CheckBox _enabled = new() { Content = "Enable mapper" };
    private readonly CheckBox _autoMap = new() { Content = "Record rooms automatically" };
    private readonly CheckBox _persist = new() { Content = "Persist map knowledge" };
    private readonly NumericUpDown _routeDepth = Number(10, 2000);
    private readonly CheckBox _avoidBlocked = new() { Content = "Avoid blocked exits" };
    private readonly CheckBox _allowUnknown = new() { Content = "Allow unknown traversability" };
    private readonly CheckBox _avoidClosedDoors = new() { Content = "Avoid closed doors" };
    private readonly CheckBox _preferKnown = new() { Content = "Prefer known traversable exits" };
    private readonly CheckBox _autoMove = new() { Content = "Enable auto-move" };
    private readonly CheckBox _stopOnCombat = new() { Content = "Stop auto-move on combat" };
    private readonly CheckBox _resumeAfterCombat = new() { Content = "Resume after combat" };
    private readonly CheckBox _replan = new() { Content = "Replan on route deviation" };
    private readonly NumericUpDown _stepDelay = Number(0, 10_000);
    private readonly NumericUpDown _stepTimeout = Number(500, 120_000);
    private readonly NumericUpDown _maxReplans = Number(0, 50);
    private readonly CheckBox _autoOpenDoors = new() { Content = "Auto-open doors" };
    private readonly TextBox _doorCommand = UiTheme.FieldBox();
    private readonly NumericUpDown _visualDepth = Number(1, 50);
    private readonly NumericUpDown _visualRooms = Number(10, 1000);
    private readonly ChipEditor _areas = new("Area");
    private readonly ChipEditor _terrains = new("Terrain");
    private readonly ChipEditor _mobs = new("Mob");
    private readonly TextBlock _message = new() { Foreground = UiTheme.Muted, FontSize = NexTypography.Metadata };

    public MapperPreferencesEditor(NexMudRuntime runtime, Func<Task> close)
    {
        _runtime = runtime;
        _close = close;
        FontFamily = UiTheme.Sans;
        FontSize = NexTypography.Body;
        Load(runtime.Settings.Mapper ?? new MapperPreferences());
        Content = Build();
    }

    private Control Build()
    {
        StackPanel root = new() { Margin = new Thickness(18), Spacing = 12 };
        root.Children.Add(new TextBlock
        {
            Text = "MAP PREFERENCES",
            Foreground = UiTheme.Accent,
            FontWeight = FontWeight.Bold,
            FontSize = NexTypography.SectionTitle
        });
        root.Children.Add(new TextBlock
        {
            Text = "Map acquisition, routing, avoidance, auto-move and display policy.",
            Foreground = UiTheme.Muted,
            TextWrapping = TextWrapping.Wrap
        });

        root.Children.Add(Section("Recording", _enabled, _autoMap, _persist));
        root.Children.Add(Section("Route Planning",
            Row("Maximum route depth", _routeDepth), _avoidBlocked, _allowUnknown, _avoidClosedDoors, _preferKnown));
        root.Children.Add(Section("Avoidance",
            Labelled("Areas", _areas), Labelled("Terrains", _terrains), Labelled("MOB names", _mobs)));
        root.Children.Add(Section("Auto-move",
            _autoMove, _stopOnCombat, _resumeAfterCombat, _replan,
            Row("Step delay (ms)", _stepDelay), Row("Step timeout (ms)", _stepTimeout),
            Row("Maximum replans", _maxReplans), _autoOpenDoors,
            Row("Door command template", _doorCommand)));
        root.Children.Add(Section("Display",
            Row("Visual depth", _visualDepth), Row("Maximum visible rooms", _visualRooms)));

        root.Children.Add(_message);
        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        Button save = UiTheme.PrimaryButton("Save");
        save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        Button cancel = UiTheme.QuietButton("Cancel");
        cancel.Click += async (_, _) => await _close().ConfigureAwait(true);
        actions.Children.Add(save);
        actions.Children.Add(cancel);
        root.Children.Add(actions);
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private void Load(MapperPreferences value)
    {
        _enabled.IsChecked = value.Enabled;
        _autoMap.IsChecked = value.AutoMap;
        _persist.IsChecked = value.PersistKnowledge;
        _routeDepth.Value = value.MaximumRouteDepth;
        _avoidBlocked.IsChecked = value.AvoidBlockedExits;
        _allowUnknown.IsChecked = value.AllowUnknownTraversability;
        _avoidClosedDoors.IsChecked = value.AvoidClosedDoors;
        _preferKnown.IsChecked = value.PreferKnownTraversableExits;
        _autoMove.IsChecked = value.AutoMoveEnabled;
        _stopOnCombat.IsChecked = value.StopAutoMoveOnCombat;
        _resumeAfterCombat.IsChecked = value.ResumeAutoMoveAfterCombat;
        _replan.IsChecked = value.ReplanAutoMoveOnDeviation;
        _stepDelay.Value = value.AutoMoveStepDelayMilliseconds;
        _stepTimeout.Value = value.AutoMoveStepTimeoutMilliseconds;
        _maxReplans.Value = value.AutoMoveMaximumReplans;
        _autoOpenDoors.IsChecked = value.AutoOpenDoors;
        _doorCommand.Text = value.DoorOpenCommandTemplate;
        _visualDepth.Value = value.VisualMapDepth;
        _visualRooms.Value = value.VisualMapMaximumRooms;
        _areas.SetValues(value.AvoidAreas);
        _terrains.SetValues(value.AvoidTerrains);
        _mobs.SetValues(value.AvoidMobNames);
    }

    private async Task SaveAsync()
    {
        try
        {
            string template = (_doorCommand.Text ?? string.Empty).Trim();
            if ((_autoOpenDoors.IsChecked ?? false) && !template.Contains("{direction}", StringComparison.OrdinalIgnoreCase))
            {
                _message.Text = "Door command template must contain {direction}.";
                _message.Foreground = UiTheme.Danger;
                return;
            }

            MapperPreferences value = new(
                Enabled: _enabled.IsChecked ?? false,
                AutoMap: _autoMap.IsChecked ?? false,
                PersistKnowledge: _persist.IsChecked ?? false,
                MaximumRouteDepth: (int)(_routeDepth.Value ?? 250),
                AvoidBlockedExits: _avoidBlocked.IsChecked ?? false,
                AllowUnknownTraversability: _allowUnknown.IsChecked ?? false,
                AutoMoveEnabled: _autoMove.IsChecked ?? false,
                StopAutoMoveOnCombat: _stopOnCombat.IsChecked ?? false,
                ResumeAutoMoveAfterCombat: _resumeAfterCombat.IsChecked ?? false,
                ReplanAutoMoveOnDeviation: _replan.IsChecked ?? false,
                AutoMoveStepDelayMilliseconds: (int)(_stepDelay.Value ?? 120),
                AutoMoveStepTimeoutMilliseconds: (int)(_stepTimeout.Value ?? 8000),
                AutoMoveMaximumReplans: (int)(_maxReplans.Value ?? 3),
                VisualMapDepth: (int)(_visualDepth.Value ?? 8),
                VisualMapMaximumRooms: (int)(_visualRooms.Value ?? 120),
                AvoidAreas: _areas.Values,
                AvoidTerrains: _terrains.Values,
                AvoidMobNames: _mobs.Values,
                AvoidClosedDoors: _avoidClosedDoors.IsChecked ?? false,
                PreferKnownTraversableExits: _preferKnown.IsChecked ?? true,
                AutoOpenDoors: _autoOpenDoors.IsChecked ?? false,
                DoorOpenCommandTemplate: string.IsNullOrWhiteSpace(template) ? "open {direction}" : template);

            await _runtime.SaveMapperPreferencesAsync(value, _runtime.CancellationToken).ConfigureAwait(true);
            await _close().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _message.Text = exception.Message;
            _message.Foreground = UiTheme.Danger;
        }
    }

    private static NumericUpDown Number(decimal min, decimal max) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = 1,
        Width = 110,
        HorizontalAlignment = HorizontalAlignment.Left
    };

    private static Border Section(string title, params Control[] controls)
    {
        StackPanel body = new() { Spacing = 7 };
        body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Foreground = UiTheme.Text });
        foreach (Control control in controls) body.Children.Add(control);
        return new Border
        {
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Child = body
        };
    }

    private static Control Row(string label, Control control)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("180,*"), ColumnSpacing = 8 };
        row.Children.Add(new TextBlock { Text = label, Foreground = UiTheme.Muted, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static Control Labelled(string label, Control control)
    {
        StackPanel panel = new() { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, Foreground = UiTheme.Muted, FontSize = NexTypography.Metadata });
        panel.Children.Add(control);
        return panel;
    }

    private sealed class ChipEditor : UserControl
    {
        private readonly List<string> _values = [];
        private readonly WrapPanel _chips = new();
        private readonly TextBox _input = UiTheme.FieldBox();
        private readonly string _placeholder;

        public ChipEditor(string placeholder)
        {
            _placeholder = placeholder;
            _input.PlaceholderText = $"Add {placeholder.ToLowerInvariant()}";
            Button add = UiTheme.QuietButton("Add");
            add.Click += (_, _) => AddValue();
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
            row.Children.Add(_input);
            Grid.SetColumn(add, 1);
            row.Children.Add(add);
            StackPanel root = new();
            root.Children.Add(_chips);
            root.Children.Add(row);
            Content = root;
        }

        public IReadOnlyList<string> Values => _values.ToArray();

        public void SetValues(IEnumerable<string>? values)
        {
            _values.Clear();
            foreach (string value in values ?? Array.Empty<string>())
            {
                string trimmed = value.Trim();
                if (trimmed.Length > 0 && !_values.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) _values.Add(trimmed);
            }
            Render();
        }

        private void AddValue()
        {
            string value = (_input.Text ?? string.Empty).Trim();
            if (value.Length == 0 || _values.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
            _values.Add(value);
            _input.Text = string.Empty;
            Render();
        }

        private void Render()
        {
            _chips.Children.Clear();
            foreach (string value in _values.ToArray())
            {
                Button chip = UiTheme.QuietButton($"{value}  ×");
                chip.MinHeight = 24;
                chip.Padding = new Thickness(7, 2);
                chip.Click += (_, _) =>
                {
                    _values.Remove(value);
                    Render();
                };
                _chips.Children.Add(chip);
            }
            if (_values.Count == 0)
            {
                _chips.Children.Add(new TextBlock
                {
                    Text = $"No {_placeholder.ToLowerInvariant()} avoidance configured.",
                    Foreground = UiTheme.Faint,
                    FontSize = NexTypography.Metadata
                });
            }
        }
    }
}
