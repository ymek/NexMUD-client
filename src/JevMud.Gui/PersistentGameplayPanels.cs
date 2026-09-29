using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace JevMud.Gui;

/// <summary>
/// Dense, foveal telemetry strip kept directly between live game output and command entry.
/// It intentionally avoids inferring a level-progress percentage when Avendar has not exposed
/// the current level's lower/upper XP boundaries.
/// </summary>
internal sealed class GameplayHudPanel : UserControl
{
    private readonly TextBlock _level = new();
    private readonly TextBlock _next = new();
    private readonly NexResourceBar _hp = new(NexMudTheme.Hp, 20);
    private readonly NexResourceBar _mana = new(NexMudTheme.Mana, 20);
    private readonly NexResourceBar _move = new(NexMudTheme.Movement, 20);
    private readonly TextBlock _position = new();
    private readonly TextBlock _combat = new();
    private readonly TextBlock _target = new();
    private readonly TextBlock _jev = new();
    private readonly NexIconView _positionIcon = NexMudIcons.Create(NexIconKind.Position, 14, NexMudTheme.Muted);
    private readonly NexIconView _combatIcon = NexMudIcons.Create(NexIconKind.Combat, 14, NexMudTheme.Success);
    private readonly NexIconView _targetIcon = NexMudIcons.Create(NexIconKind.Target, 14, NexMudTheme.Muted);
    private readonly NexIconView _jevIcon = NexMudIcons.Create(NexIconKind.JevState, 14, NexMudTheme.Muted);

    public GameplayHudPanel()
    {
        Content = Build();
    }

    public void Update(CharacterHudViewModel viewModel)
    {
        _level.Text = $"LVL {viewModel.Level}";
        _next.Text = $"NEXT {viewModel.ExperienceRemainingText}";

        UpdateVital(_hp, viewModel.HitPoints);
        UpdateVital(_mana, viewModel.Mana);
        UpdateVital(_move, viewModel.Movement);

        UpdatePosition(viewModel.Position);

        _combat.Text = viewModel.CombatActive ? "ENGAGED" : "Clear";
        _combat.Foreground = viewModel.CombatActive ? NexMudTheme.Danger : NexMudTheme.Success;
        _combat.FontWeight = viewModel.CombatActive ? FontWeight.Bold : FontWeight.SemiBold;
        _combat.Opacity = viewModel.CombatActive ? 1 : 0.72;
        _combatIcon.Stroke = viewModel.CombatActive ? NexMudTheme.Danger : NexMudTheme.Success;
        _combatIcon.Opacity = _combat.Opacity;

        bool hasTarget = !string.IsNullOrWhiteSpace(viewModel.Target) && viewModel.Target != "-";
        _target.Text = hasTarget ? viewModel.Target : "No target";
        _target.Foreground = hasTarget ? NexMudTheme.Target : NexMudTheme.Muted;
        _target.FontWeight = hasTarget ? FontWeight.SemiBold : FontWeight.Normal;
        _target.Opacity = hasTarget ? 1 : 0.68;
        _targetIcon.Stroke = hasTarget ? NexMudTheme.Target : NexMudTheme.Muted;
        _targetIcon.Opacity = _target.Opacity;

        string jevState = FormatJevState(viewModel.JevEnabled, viewModel.Jev);
        _jev.Text = jevState;
        bool jevBusy = jevState is "Acting" or "Deciding";
        bool jevReady = jevState == "Ready";
        IBrush jevBrush = jevBusy
            ? NexMudTheme.AccentBright
            : jevReady
                ? NexMudTheme.AccentPrimary
                : NexMudTheme.Muted;
        _jev.Foreground = jevBrush;
        _jev.FontWeight = jevBusy ? FontWeight.Bold : FontWeight.Normal;
        _jev.Opacity = jevState == "Off" ? 0.68 : 1;
        _jevIcon.Stroke = jevBrush;
        _jevIcon.Opacity = _jev.Opacity;
        ToolTip.SetTip(_jev, jevBusy && !string.IsNullOrWhiteSpace(viewModel.Jev) ? viewModel.Jev : null);
    }

    private Control Build()
    {
        Grid rail = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,*,*,Auto"),
            ColumnSpacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        rail.Children.Add(BuildProgression());

        Control hp = Vital("HP", _hp, NexMudTheme.Hp);
        Grid.SetColumn(hp, 1);
        rail.Children.Add(hp);

        Control mana = Vital("MA", _mana, NexMudTheme.Mana);
        Grid.SetColumn(mana, 2);
        rail.Children.Add(mana);

        Control move = Vital("MV", _move, NexMudTheme.Movement);
        Grid.SetColumn(move, 3);
        rail.Children.Add(move);

        Control tactical = BuildTacticalCluster();
        Grid.SetColumn(tactical, 4);
        rail.Children.Add(tactical);

        return new Border
        {
            Background = NexMudTheme.InsetGradient,
            BorderBrush = NexMudTheme.BronzeShadow,
            BorderThickness = new Thickness(0, 1, 0, 1),
            MinHeight = 48,
            MaxHeight = 54,
            Padding = new Thickness(12, 4),
            Child = rail
        };
    }

    private Control BuildProgression()
    {
        StackPanel progression = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        _level.Foreground = NexMudTheme.AccentBright;
        _level.FontFamily = NexMudTheme.Interface;
        _level.FontSize = NexTypography.HudStrong;
        _level.FontWeight = FontWeight.Bold;
        _level.VerticalAlignment = VerticalAlignment.Center;
        progression.Children.Add(_level);

        _next.Foreground = NexMudTheme.Xp;
        _next.FontFamily = NexMudTheme.Interface;
        _next.FontSize = NexTypography.Hud;
        _next.FontWeight = FontWeight.SemiBold;
        _next.VerticalAlignment = VerticalAlignment.Center;
        progression.Children.Add(_next);

        return progression;
    }

    private static Control Vital(string label, NexResourceBar bar, IBrush color)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 92
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = color,
            FontSize = NexTypography.Hud,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        bar.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);
        return row;
    }

    private Control BuildTacticalCluster()
    {
        Grid state = new()
        {
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            RowSpacing = 3,
            ColumnSpacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        state.Children.Add(Tactical(_positionIcon, _position, 76));

        Control combat = Tactical(_combatIcon, _combat, 62);
        Grid.SetColumn(combat, 1);
        state.Children.Add(combat);

        Control target = Tactical(_targetIcon, _target, 76);
        Grid.SetRow(target, 1);
        state.Children.Add(target);

        Control jev = Tactical(_jevIcon, _jev, 62);
        Grid.SetRow(jev, 1);
        Grid.SetColumn(jev, 1);
        state.Children.Add(jev);

        return state;
    }

    private static Control Tactical(NexIconView icon, TextBlock value, double maxWidth)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            ColumnSpacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(icon);
        value.Foreground = NexMudTheme.Parchment;
        value.FontSize = NexTypography.Hud;
        value.VerticalAlignment = VerticalAlignment.Center;
        value.MaxWidth = maxWidth;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private void UpdatePosition(string position)
    {
        string normalized = string.IsNullOrWhiteSpace(position) || position == "-"
            ? "Unknown"
            : position.Trim();
        string key = normalized.ToLowerInvariant();
        IBrush brush = key switch
        {
            "standing" => NexMudTheme.Muted,
            "resting" => NexMudTheme.Warning,
            "sleeping" => NexMudTheme.Mana,
            "stunned" => NexMudTheme.Warning,
            "incapacitated" or "dying" or "dead" => NexMudTheme.Danger,
            "unknown" => NexMudTheme.Muted,
            _ => NexMudTheme.Parchment
        };
        bool abnormal = key is "resting" or "sleeping" or "stunned" or "incapacitated" or "dying" or "dead";

        _position.Text = char.ToUpperInvariant(normalized[0]) + normalized[1..];
        _position.Foreground = brush;
        _position.FontWeight = abnormal ? FontWeight.SemiBold : FontWeight.Normal;
        _position.Opacity = key is "standing" or "unknown" ? 0.78 : 1;
        _positionIcon.Stroke = brush;
        _positionIcon.Opacity = _position.Opacity;
    }

    private static string FormatJevState(bool enabled, string detail)
    {
        if (!enabled) return "Off";
        if (string.Equals(detail, "BUSY", StringComparison.OrdinalIgnoreCase)) return "Deciding";
        if (string.IsNullOrWhiteSpace(detail) || detail == "-") return "Ready";
        return "Acting";
    }

    private static void UpdateVital(NexResourceBar bar, VitalMeterViewModel vital)
    {
        bar.Set(vital.CurrentValue, vital.MaximumValue, vital.Display);
    }
}

/// <summary>
/// Persistent character identity and stable combat-relevant character facts. Critical live
/// resources are intentionally omitted because they live in the foveal gameplay HUD.
/// </summary>
internal sealed class CharacterHudPanel : UserControl
{
    private readonly Func<string, string?, ItemInspectionData> _inspectionResolver;
    private readonly TextBlock _name = new();
    private readonly TextBlock _identity = new();
    private readonly TextBlock _alignment = new();
    private readonly Grid _attributes = new()
    {
        ColumnDefinitions = new ColumnDefinitions("*,*,*"),
        RowDefinitions = new RowDefinitions("Auto,Auto"),
        ColumnSpacing = 8,
        RowSpacing = 3
    };
    private readonly TextBlock _hitroll = new();
    private readonly TextBlock _damroll = new();
    private readonly TextBlock _saves = new();
    private readonly TextBlock _armor = new();
    private readonly TextBlock _exploration = new();
    private readonly TextBlock _wealth = new();
    private readonly TextBlock _items = new();
    private readonly TextBlock _weight = new();
    private readonly StackPanel _loadout = new() { Spacing = 2 };
    private readonly TextBlock _conditions = new();
    private readonly StackPanel _observedDetails = new() { Spacing = NexSpacing.Tight };

    public CharacterHudPanel(Func<string, string?, ItemInspectionData> inspectionResolver)
    {
        _inspectionResolver = inspectionResolver;
        Content = Build();
    }

    public void Update(CharacterHudViewModel viewModel)
    {
        _name.Text = viewModel.Name;
        _identity.Text = viewModel.Identity;
        _identity.IsVisible = !string.IsNullOrWhiteSpace(viewModel.Identity);
        _alignment.Text = viewModel.Alignment ?? string.Empty;
        _alignment.IsVisible = !string.IsNullOrWhiteSpace(viewModel.Alignment);

        bool identified = !string.Equals(viewModel.Name, "Character not identified", StringComparison.Ordinal);
        _observedDetails.IsVisible = identified;

        _attributes.Children.Clear();
        int attributeIndex = 0;
        foreach (CharacterAttributeViewModel attribute in viewModel.Attributes.Take(6))
        {
            Control cell = Attribute(attribute.Name, attribute.Display, AttributeIcon(attribute.Name));
            Grid.SetColumn(cell, attributeIndex % 3);
            Grid.SetRow(cell, attributeIndex / 3);
            _attributes.Children.Add(cell);
            attributeIndex++;
        }

        _hitroll.Text = viewModel.Hitroll;
        _damroll.Text = viewModel.Damroll;
        _saves.Text = viewModel.Saves;
        _armor.Text = viewModel.ArmorClass;
        _exploration.Text = viewModel.Exploration;
        _wealth.Text = viewModel.Wealth;
        _items.Text = viewModel.Items;
        _weight.Text = viewModel.Weight;

        RenderLoadout(viewModel.Equipment);

        _conditions.Text = viewModel.Conditions.Count == 0 ? string.Empty : string.Join(" · ", viewModel.Conditions.Take(4));
        _conditions.IsVisible = viewModel.Conditions.Count > 0;
    }

    private Control Build()
    {
        StackPanel body = new() { Spacing = NexSpacing.Tight };

        StackPanel identity = new() { Spacing = 1 };
        _name.Foreground = NexMudTheme.Parchment;
        _name.FontFamily = NexMudTheme.Display;
        _name.FontSize = NexMudTheme.CharacterNameText;
        _name.FontWeight = FontWeight.Bold;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _identity.Foreground = NexMudTheme.Muted;
        _identity.FontSize = NexTypography.Body;
        _identity.TextWrapping = TextWrapping.Wrap;
        _alignment.Foreground = NexMudTheme.AccentBright;
        _alignment.FontSize = NexTypography.Metadata;
        identity.Children.Add(_name);
        identity.Children.Add(_identity);
        identity.Children.Add(_alignment);
        body.Children.Add(identity);

        _observedDetails.Children.Add(new Border
        {
            Height = 1,
            Background = NexMudTheme.Divider,
            Margin = new Thickness(0, 1)
        });
        _observedDetails.Children.Add(_attributes);

        Grid combat = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            ColumnSpacing = 8
        };
        combat.Children.Add(Fact("Hit", _hitroll));
        Control damroll = Fact("Dam", _damroll);
        Grid.SetColumn(damroll, 1);
        combat.Children.Add(damroll);
        Control saves = Fact("Save", _saves);
        Grid.SetColumn(saves, 2);
        combat.Children.Add(saves);
        _observedDetails.Children.Add(combat);

        Grid support = new() { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        support.Children.Add(Fact("AC", _armor));
        Control exploration = Fact("Explore", _exploration);
        Grid.SetColumn(exploration, 1);
        support.Children.Add(exploration);
        _observedDetails.Children.Add(support);

        Grid inventory = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*"),
            ColumnSpacing = 8
        };
        inventory.Children.Add(Fact("Wealth", _wealth));
        Control items = Fact("Items", _items);
        Grid.SetColumn(items, 1);
        inventory.Children.Add(items);
        Control weight = Fact("Weight", _weight);
        Grid.SetColumn(weight, 2);
        inventory.Children.Add(weight);
        _observedDetails.Children.Add(inventory);

        StackPanel loadout = new() { Spacing = 2 };
        loadout.Children.Add(new TextBlock
        {
            Text = "LOADOUT",
            Foreground = NexMudTheme.Faint,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.SemiBold
        });
        loadout.Children.Add(_loadout);
        _observedDetails.Children.Add(loadout);

        _conditions.Foreground = NexMudTheme.Warning;
        _conditions.FontSize = NexTypography.Metadata;
        _conditions.TextWrapping = TextWrapping.Wrap;
        _observedDetails.Children.Add(_conditions);
        body.Children.Add(_observedDetails);

        return new Border
        {
            Background = NexMudTheme.PrimarySurfaceGradient,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4),
            Child = body
        };
    }

    private void RenderLoadout(IReadOnlyList<CharacterEquipmentViewModel> equipment)
    {
        _loadout.Children.Clear();
        CharacterEquipmentViewModel[] important = equipment
            .Select(item => (Item: item, Projection: ProjectLoadoutSlot(item.Slot)))
            .Where(pair => pair.Projection is not null)
            .OrderBy(pair => pair.Projection!.Value.Priority)
            .ThenBy(pair => pair.Item.Slot, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new CharacterEquipmentViewModel(pair.Projection!.Value.Label, pair.Item.Item))
            .Take(5)
            .ToArray();

        if (important.Length == 0)
        {
            _loadout.Children.Add(new TextBlock
            {
                Text = "Equipment not observed",
                Foreground = NexMudTheme.Faint,
                FontSize = NexTypography.Metadata
            });
            return;
        }

        foreach (CharacterEquipmentViewModel item in important)
        {
            Grid row = new() { ColumnDefinitions = new ColumnDefinitions("54,*"), ColumnSpacing = 6, MinHeight = 20 };
            row.Children.Add(new TextBlock
            {
                Text = item.Slot,
                Foreground = NexMudTheme.Faint,
                FontSize = NexTypography.Metadata,
                VerticalAlignment = VerticalAlignment.Center
            });
            Button value = new()
            {
                Content = item.Item,
                Background = Brushes.Transparent,
                Foreground = NexMudTheme.Parchment,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(0),
                FontSize = NexTypography.Metadata,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            ToolTip.SetTip(value, ItemInspectionPopover.Build(_inspectionResolver(item.Item, item.Slot)));
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            _loadout.Children.Add(row);
        }
    }

    private static (string Label, int Priority)? ProjectLoadoutSlot(string slot)
    {
        string normalized = slot.Trim().ToLowerInvariant();
        if (normalized.Contains("dual wield", StringComparison.Ordinal)) return ("Off", 1);
        if (normalized.Contains("wield", StringComparison.Ordinal)) return ("Main", 0);
        if (normalized.Contains("torso", StringComparison.Ordinal)) return ("Torso", 2);
        if (normalized.Contains("floating", StringComparison.Ordinal)) return ("Float", 3);
        if (normalized is "body" || normalized.Contains("about body", StringComparison.Ordinal)) return ("Body", 4);
        if (normalized.Contains("shield", StringComparison.Ordinal)) return ("Shield", 5);
        return null;
    }

    private static Control Attribute(string name, string value, NexIconKind icon)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 4 };
        row.Children.Add(NexMudIcons.Create(icon, 10, NexMudTheme.AccentPrimary));
        TextBlock label = new()
        {
            Text = name,
            Foreground = NexMudTheme.Muted,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        TextBlock score = new()
        {
            Text = value,
            Foreground = NexMudTheme.Parchment,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(score, 2);
        row.Children.Add(score);
        return row;
    }

    private static Control Fact(string label, TextBlock value)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 4 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = NexMudTheme.Faint,
            FontSize = NexTypography.Metadata,
            VerticalAlignment = VerticalAlignment.Center
        });
        value.Foreground = NexMudTheme.Parchment;
        value.FontSize = NexTypography.BodyStrong;
        value.FontWeight = FontWeight.SemiBold;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private static NexIconKind AttributeIcon(string name) => name.ToUpperInvariant() switch
    {
        "STR" => NexIconKind.Strength,
        "DEX" => NexIconKind.Dexterity,
        "CON" => NexIconKind.Constitution,
        "INT" => NexIconKind.Intelligence,
        "WIS" => NexIconKind.Wisdom,
        "CHR" or "CHA" => NexIconKind.Charisma,
        _ => NexIconKind.Abilities
    };
}

/// <summary>
/// Persistent room intelligence for active play. Room identity, exits and named entities are
/// always favored over dashboard-style empty cards.
/// </summary>
internal sealed class RoomContextPanel : UserControl
{
    private readonly Func<string, Task> _sendCommand;
    private readonly Action _showMap;
    private readonly TextBlock _roomName = new();
    private readonly TextBlock _metadata = new();
    private readonly WrapPanel _exits = new();
    private readonly TextBlock _emptyEntities = new();
    private readonly StackPanel _entityGroups = new() { Spacing = NexSpacing.Tight };
    private readonly StackPanel _observations = new() { Spacing = NexSpacing.Tight };
    private readonly TextBlock _memorySummary = new();
    private readonly TextBlock _lastSeen = new();

    public RoomContextPanel(Func<string, Task> sendCommand, Action showMap)
    {
        _sendCommand = sendCommand;
        _showMap = showMap;
        Content = Build();
    }

    public void Update(RoomContextViewModel viewModel)
    {
        _roomName.Text = viewModel.Name;
        _metadata.Text = viewModel.Metadata;
        _metadata.IsVisible = !string.IsNullOrWhiteSpace(viewModel.Metadata);

        _exits.Children.Clear();
        if (viewModel.Exits.Count == 0)
        {
            _exits.Children.Add(new TextBlock
            {
                Text = "No exits observed",
                Foreground = NexMudTheme.Faint,
                FontSize = NexTypography.Body,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        else
        {
            foreach (RoomExitViewModel exit in viewModel.Exits)
            {
                _exits.Children.Add(ExitButton(exit));
            }
        }

        bool hasVisibleEntities = viewModel.People.Count > 0 ||
                                  viewModel.Fixtures.Count > 0 ||
                                  viewModel.Objects.Count > 0 ||
                                  viewModel.Corpses.Count > 0;
        _emptyEntities.IsVisible = !hasVisibleEntities;

        _entityGroups.Children.Clear();
        AddEntitySection("People", NexIconKind.People, viewModel.People);
        AddEntitySection("Fixtures", NexIconKind.Fixtures, viewModel.Fixtures);
        AddEntitySection("Objects", NexIconKind.Objects, viewModel.Objects);
        AddEntitySection("Corpses", NexIconKind.Corpses, viewModel.Corpses);

        _observations.Children.Clear();
        if (viewModel.RecentObservations.Count == 0)
        {
            _observations.Children.Add(new TextBlock
            {
                Text = "No recent room activity observed",
                Foreground = NexMudTheme.Faint,
                FontSize = NexTypography.Body
            });
        }
        else
        {
            foreach (string observation in viewModel.RecentObservations.Take(3))
            {
                Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 7 };
                row.Children.Add(new Border
                {
                    Width = 4,
                    Height = 4,
                    Background = NexMudTheme.AccentPrimary,
                    CornerRadius = new CornerRadius(2),
                    Margin = new Thickness(1, 6, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top
                });
                TextBlock copy = new()
                {
                    Text = observation,
                    Foreground = NexMudTheme.Muted,
                    FontSize = NexTypography.Body,
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetColumn(copy, 1);
                row.Children.Add(copy);
                _observations.Children.Add(row);
            }
        }

        List<string> memory = [];
        if (viewModel.VisitCount is int visits) memory.Add($"{visits:N0} visits");
        if (viewModel.KnownRoutes is int routes) memory.Add($"{routes:N0} routes");
        if (viewModel.RecurringEntities is int recurring) memory.Add($"{recurring:N0} recurring entities");
        _memorySummary.Text = memory.Count == 0 ? "No durable room memory yet" : string.Join("  •  ", memory);
        _lastSeen.Text = viewModel.LastSeenAt is DateTimeOffset lastSeen
            ? $"Last observed {lastSeen.ToLocalTime():g}"
            : "Last observed —";
    }

    private Control Build()
    {
        Grid shell = new() { RowDefinitions = new RowDefinitions("*,Auto") };
        StackPanel body = new() { Spacing = NexSpacing.Tight };

        StackPanel roomIdentity = new() { Spacing = NexSpacing.Micro };
        _roomName.Foreground = NexMudTheme.Parchment;
        _roomName.FontFamily = NexMudTheme.Display;
        _roomName.FontSize = NexMudTheme.RoomTitleText;
        _roomName.FontWeight = FontWeight.Bold;
        _roomName.TextWrapping = TextWrapping.Wrap;
        _metadata.Foreground = NexMudTheme.Muted;
        _metadata.FontSize = NexTypography.Metadata;
        roomIdentity.Children.Add(_roomName);
        roomIdentity.Children.Add(_metadata);
        body.Children.Add(roomIdentity);

        Grid exitsSection = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 10
        };
        exitsSection.Children.Add(SectionLabel("EXITS", NexIconKind.Position));
        _exits.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_exits, 1);
        exitsSection.Children.Add(_exits);
        body.Children.Add(exitsSection);

        _emptyEntities.Text = "No visible entities observed";
        _emptyEntities.Foreground = NexMudTheme.Faint;
        _emptyEntities.FontSize = NexTypography.CompactData;
        _emptyEntities.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(_entityGroups);
        body.Children.Add(_emptyEntities);

        StackPanel observations = new() { Spacing = NexSpacing.Micro };
        observations.Children.Add(SectionLabel("RECENT", NexIconKind.History));
        observations.Children.Add(_observations);
        body.Children.Add(observations);

        StackPanel memory = new() { Spacing = NexSpacing.Micro };
        memory.Children.Add(SectionLabel("MEMORY", NexIconKind.Codex));
        _memorySummary.Foreground = NexMudTheme.Muted;
        _memorySummary.FontSize = NexTypography.CompactData;
        _memorySummary.TextWrapping = TextWrapping.Wrap;
        _lastSeen.Foreground = NexMudTheme.Faint;
        _lastSeen.FontSize = NexTypography.Metadata;
        memory.Children.Add(_memorySummary);
        memory.Children.Add(_lastSeen);
        body.Children.Add(memory);

        ScrollViewer scroll = new()
        {
            Content = body,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        shell.Children.Add(scroll);

        WrapPanel actions = new() { Orientation = Orientation.Horizontal };
        actions.Children.Add(ActionButton("Look", NexIconKind.Look, "look"));
        actions.Children.Add(ActionButton("Where", NexIconKind.Where, "where"));
        actions.Children.Add(ActionButton("Inv", NexIconKind.Inventory, "inventory"));
        NexQuickAction map = new("Map", NexIconKind.Map) { Margin = new Thickness(0, 0, 5, 0) };
        map.Click += (_, _) => _showMap();
        actions.Children.Add(map);

        Border actionBar = new()
        {
            Background = NexMudTheme.InsetGradient,
            BorderBrush = NexMudTheme.Divider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 5, 0, 0),
            Child = actions
        };
        Grid.SetRow(actionBar, 1);
        shell.Children.Add(actionBar);

        return new Border
        {
            Background = NexMudTheme.PrimarySurfaceGradient,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 4),
            Child = shell
        };
    }

    private Button ExitButton(RoomExitViewModel exit)
    {
        bool blocked = exit.Traversability == JevMud.Contracts.State.ExitTraversability.Blocked;
        bool doorProblem = exit.DoorState is JevMud.Contracts.State.ExitDoorState.Closed or JevMud.Contracts.State.ExitDoorState.Locked;
        Button button = new()
        {
            Content = Abbreviate(exit.Direction),
            MinWidth = 34,
            MinHeight = 27,
            Margin = new Thickness(0, 0, 5, 0),
            Padding = new Thickness(7, 2),
            CornerRadius = new CornerRadius(2),
            Background = NexMudTheme.RaisedSurfaceGradient,
            Foreground = blocked ? NexMudTheme.Danger : NexMudTheme.Parchment,
            BorderBrush = doorProblem ? NexMudTheme.Warning : NexMudTheme.AntiqueBrass,
            BorderThickness = new Thickness(1),
            FontSize = NexTypography.Body,
            FontWeight = FontWeight.Bold
        };
        string state = exit.DoorState switch
        {
            JevMud.Contracts.State.ExitDoorState.Locked => "locked",
            JevMud.Contracts.State.ExitDoorState.Closed => "closed",
            _ when blocked => "blocked",
            _ => "traversable/unknown"
        };
        string direction = exit.Direction.Trim();
        string stateDetail = $"{direction} · {state}{(string.IsNullOrWhiteSpace(exit.BlockReason) ? string.Empty : $" · {exit.BlockReason}")}";
        ToolTip.SetTip(button, $"{stateDetail} · right-click for movement/scan actions");
        button.Click += async (_, _) => await _sendCommand(direction);

        MenuItem go = new() { Header = $"Go {TitleDirection(direction)}" };
        go.Click += async (_, _) => await _sendCommand(direction);
        MenuItem scan = new() { Header = $"Scan {TitleDirection(direction)}" };
        scan.Click += async (_, _) => await _sendCommand($"scan {direction}");
        button.ContextMenu = new ContextMenu
        {
            ItemsSource = new object[] { go, scan }
        };
        return button;
    }

    private NexQuickAction ActionButton(string label, NexIconKind icon, string command)
    {
        NexQuickAction button = new(label, icon) { Margin = new Thickness(0, 0, 5, 0) };
        button.Click += async (_, _) => await _sendCommand(command);
        return button;
    }

    private void AddEntitySection(string title, NexIconKind icon, IReadOnlyList<RoomEntityViewModel> entities)
    {
        if (entities.Count == 0) return;

        StackPanel section = new() { Spacing = NexSpacing.Tight };
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6 };
        header.Children.Add(NexMudIcons.Create(icon, 13, NexMudTheme.AccentPrimary));
        TextBlock label = new()
        {
            Text = title.ToUpperInvariant(),
            Foreground = NexMudTheme.Parchment,
            FontSize = NexTypography.Body,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        header.Children.Add(label);
        NexCountBadge count = new(entities.Count);
        Grid.SetColumn(count, 2);
        header.Children.Add(count);
        section.Children.Add(header);

        foreach (RoomEntityViewModel entity in entities.Take(6))
        {
            TextBlock row = new()
            {
                Text = $"• {entity.Description}",
                Foreground = NexMudTheme.Muted,
                FontSize = NexTypography.CompactData,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(19, 0, 0, 0)
            };
            section.Children.Add(row);
        }
        if (entities.Count > 6)
        {
            section.Children.Add(new TextBlock
            {
                Text = $"+{entities.Count - 6} more",
                Foreground = NexMudTheme.Cyan,
                FontSize = NexTypography.CompactData,
                Margin = new Thickness(19, 0, 0, 0)
            });
        }
        _entityGroups.Children.Add(section);
    }

    private static StackPanel SectionLabel(string label, NexIconKind icon)
    {
        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(NexMudIcons.Create(icon, 12, NexMudTheme.AccentPrimary));
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = NexMudTheme.Faint,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }

    private static string TitleDirection(string direction)
    {
        string trimmed = direction.Trim();
        return trimmed.Length == 0
            ? trimmed
            : char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
    }

    private static string Abbreviate(string direction) => direction.Trim().ToLowerInvariant() switch
    {
        "north" or "n" => "N",
        "south" or "s" => "S",
        "east" or "e" => "E",
        "west" or "w" => "W",
        "northeast" or "ne" => "NE",
        "northwest" or "nw" => "NW",
        "southeast" or "se" => "SE",
        "southwest" or "sw" => "SW",
        "up" or "u" => "U",
        "down" or "d" => "D",
        var value => value.ToUpperInvariant()
    };
}

