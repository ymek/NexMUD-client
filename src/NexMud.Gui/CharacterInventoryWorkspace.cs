using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NexMud.Contracts.State;

namespace NexMud.Gui;

/// <summary>
/// Text-first Character/Inventory workspace. World remains visible in MainWindow; this control
/// owns only the right-side workspace projection of canonical character/equipment/inventory state.
/// </summary>
internal sealed class CharacterInventoryWorkspace : UserControl
{
    private sealed record SlotSpec(
        string Key,
        string Label,
        int Ordinal,
        int Row,
        int Column,
        NexIconKind Icon);

    private static readonly SlotSpec[] PaperDollSlots =
    [
        new("worn on head", "Head", 1, 0, 1, NexIconKind.CharacterCrest),
        new("worn around neck", "Neck 1", 1, 1, 0, NexIconKind.Character),
        new("worn around neck", "Neck 2", 2, 1, 1, NexIconKind.Character),
        new("floating nearby", "Floating", 1, 1, 2, NexIconKind.Abilities),
        new("worn on arms", "Arms", 1, 2, 0, NexIconKind.Strength),
        new("worn on torso", "Torso", 1, 2, 1, NexIconKind.Constitution),
        new("worn on hands", "Hands", 1, 2, 2, NexIconKind.Dexterity),
        new("worn around wrist", "Wrist 1", 1, 3, 0, NexIconKind.Inventory),
        new("worn about body", "About body", 1, 3, 1, NexIconKind.CharacterCrest),
        new("worn around wrist", "Wrist 2", 2, 3, 2, NexIconKind.Inventory),
        new("worn on finger", "Ring 1", 1, 4, 0, NexIconKind.Inventory),
        new("worn about waist", "Waist", 1, 4, 1, NexIconKind.Inventory),
        new("worn on finger", "Ring 2", 2, 4, 2, NexIconKind.Inventory),
        new("branded", "Brand", 1, 5, 0, NexIconKind.Abilities),
        new("worn on legs", "Legs", 1, 5, 1, NexIconKind.Movement),
        new("worn as shield", "Shield", 1, 5, 2, NexIconKind.Constitution),
        new("worn on feet", "Feet", 1, 6, 1, NexIconKind.Movement),
        new("wielded", "Main hand", 1, 7, 0, NexIconKind.Combat),
        new("dual wielded", "Off hand", 1, 7, 2, NexIconKind.Combat)
    ];

    private readonly Func<string, string?, ItemInspectionData> _inspectionResolver;
    private readonly Func<string, Task> _sendCommand;
    private readonly TextBlock _name = new();
    private readonly TextBlock _identity = new();
    private readonly TextBlock _alignment = new();
    private readonly Grid _paperDoll = new();
    private readonly WrapPanel _attributes = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _combat = new();
    private readonly TextBlock _capacity = new();
    private readonly TextBlock _weight = new();
    private readonly TextBlock _wealth = new();
    private readonly TextBox _search = new();
    private readonly StackPanel _inventoryItems = new() { Spacing = 1 };
    private readonly TextBlock _inventoryStatus = new();
    private readonly ContentControl _selectedItemDetail = new();
    private readonly TextBlock _selectedItemHint = new();
    private readonly Button _identifySelected = new();
    private CharacterState _character = StateSnapshot.Initial.Character;
    private CharacterHudViewModel _viewModel = CharacterHudViewModel.From(StateSnapshot.Initial, false, "OFF");
    private string? _selectedItem;
    private string? _selectedSlot;
    private string? _hoveredItem;
    private string? _hoveredSlot;

    public CharacterInventoryWorkspace(
        Func<string, string?, ItemInspectionData> inspectionResolver,
        Func<string, Task> sendCommand)
    {
        _inspectionResolver = inspectionResolver;
        _sendCommand = sendCommand;
        Content = Build();
    }

    public void Update(StateSnapshot state, CharacterHudViewModel viewModel)
    {
        _character = state.Character;
        _viewModel = viewModel;

        _name.Text = viewModel.Name;
        _identity.Text = viewModel.Identity;
        _alignment.Text = viewModel.Alignment ?? string.Empty;
        _alignment.IsVisible = !string.IsNullOrWhiteSpace(viewModel.Alignment);

        RenderPaperDoll();
        RenderStats();
        RenderInventory();
        RenderInspectedItem();
    }

    private Control Build()
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Background = NexMudTheme.ApplicationBackground
        };

        StackPanel identity = new() { Spacing = 2, Margin = new Thickness(10, 8, 10, 7) };
        _name.Foreground = NexMudTheme.Parchment;
        _name.FontFamily = NexMudTheme.Display;
        _name.FontSize = NexTypography.Display;
        _name.FontWeight = FontWeight.Bold;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _identity.Foreground = NexMudTheme.Muted;
        _identity.FontSize = NexTypography.Body;
        _alignment.Foreground = NexMudTheme.AccentBright;
        _alignment.FontSize = NexTypography.Metadata;
        _alignment.FontWeight = FontWeight.SemiBold;
        identity.Children.Add(_name);
        identity.Children.Add(_identity);
        identity.Children.Add(_alignment);
        root.Children.Add(identity);

        Grid upper = new()
        {
            ColumnDefinitions = new ColumnDefinitions("3*,1.15*"),
            ColumnSpacing = 10,
            Margin = new Thickness(8, 0, 8, 8)
        };
        ConfigurePaperDollGrid();
        Border dollSurface = new()
        {
            Background = NexMudTheme.InsetGradient,
            BorderBrush = NexMudTheme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(7),
            Child = _paperDoll
        };
        upper.Children.Add(dollSurface);

        StackPanel detail = new() { Spacing = 8 };
        detail.Children.Add(SectionLabel("ITEM INSPECTION"));
        _selectedItemHint.Text = "Hover an equipped item for a quick view. Select one for persistent detail.";
        _selectedItemHint.Foreground = NexMudTheme.Faint;
        _selectedItemHint.FontSize = NexTypography.Metadata;
        _selectedItemHint.TextWrapping = TextWrapping.Wrap;
        detail.Children.Add(_selectedItemHint);
        _selectedItemDetail.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        detail.Children.Add(_selectedItemDetail);
        _identifySelected.Content = "Identify";
        _identifySelected.MinHeight = 32;
        _identifySelected.Padding = new Thickness(10, 5);
        _identifySelected.Background = NexMudTheme.RaisedSurfaceGradient;
        _identifySelected.Foreground = NexMudTheme.Parchment;
        _identifySelected.BorderBrush = NexMudTheme.AntiqueBrass;
        _identifySelected.BorderThickness = new Thickness(1);
        _identifySelected.FontSize = NexTypography.Body;
        _identifySelected.FontWeight = FontWeight.SemiBold;
        _identifySelected.IsVisible = false;
        _identifySelected.HorizontalAlignment = HorizontalAlignment.Left;
        _identifySelected.Click += async (_, _) =>
        {
            string? inspectedItem = CurrentInspectedItem;
            if (!string.IsNullOrWhiteSpace(inspectedItem))
            {
                await _sendCommand($"id {inspectedItem}");
            }
        };
        detail.Children.Add(_identifySelected);
        Border detailSurface = new()
        {
            Background = NexMudTheme.PrimarySurfaceGradient,
            BorderBrush = NexMudTheme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(10),
            MinWidth = 280,
            Child = detail
        };
        Grid.SetColumn(detailSurface, 1);
        upper.Children.Add(detailSurface);
        Grid.SetRow(upper, 1);
        root.Children.Add(upper);

        Grid lower = new()
        {
            ColumnDefinitions = new ColumnDefinitions("240,*"),
            ColumnSpacing = 10,
            Margin = new Thickness(8, 0, 8, 8)
        };

        StackPanel stats = new() { Spacing = 7 };
        stats.Children.Add(SectionLabel("CHARACTER"));
        stats.Children.Add(_attributes);
        _combat.Foreground = NexMudTheme.Parchment;
        _combat.FontSize = NexTypography.Body;
        _combat.FontWeight = FontWeight.SemiBold;
        _combat.TextWrapping = TextWrapping.Wrap;
        stats.Children.Add(_combat);
        stats.Children.Add(CompactFact("Items", _capacity));
        stats.Children.Add(CompactFact("Weight", _weight));
        stats.Children.Add(CompactFact("Wealth", _wealth));
        Border statsSurface = new()
        {
            Background = NexMudTheme.PrimarySurfaceGradient,
            BorderBrush = NexMudTheme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(9),
            Child = stats
        };
        lower.Children.Add(statsSurface);

        Grid inventory = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        Grid inventoryHeader = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        inventoryHeader.Children.Add(SectionLabel("INVENTORY"));
        Button refresh = new()
        {
            Content = "Refresh",
            Padding = new Thickness(8, 3),
            MinHeight = 28,
            Background = NexMudTheme.RaisedSurfaceGradient,
            Foreground = NexMudTheme.Muted,
            BorderBrush = NexMudTheme.Divider,
            BorderThickness = new Thickness(1),
            FontSize = NexTypography.Metadata
        };
        refresh.Click += async (_, _) => await _sendCommand("inventory");
        Grid.SetColumn(refresh, 1);
        inventoryHeader.Children.Add(refresh);
        inventory.Children.Add(inventoryHeader);

        _search.PlaceholderText = "Search carried items…";
        _search.Margin = new Thickness(0, 7, 0, 6);
        _search.MinHeight = 34;
        _search.Background = NexMudTheme.InsetGradient;
        _search.Foreground = NexMudTheme.Parchment;
        _search.BorderBrush = NexMudTheme.Divider;
        _search.BorderThickness = new Thickness(1);
        _search.Padding = new Thickness(8, 5);
        _search.FontSize = NexTypography.Body;
        _search.TextChanged += (_, _) => RenderInventory();
        Grid.SetRow(_search, 1);
        inventory.Children.Add(_search);

        ScrollViewer inventoryScroll = new()
        {
            Content = _inventoryItems,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
        };
        Grid.SetRow(inventoryScroll, 2);
        inventory.Children.Add(inventoryScroll);

        _inventoryStatus.Foreground = NexMudTheme.Faint;
        _inventoryStatus.FontSize = NexTypography.Metadata;
        _inventoryStatus.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(_inventoryStatus, 3);
        inventory.Children.Add(_inventoryStatus);

        Border inventorySurface = new()
        {
            Background = NexMudTheme.PrimarySurfaceGradient,
            BorderBrush = NexMudTheme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(9),
            Child = inventory
        };
        Grid.SetColumn(inventorySurface, 1);
        lower.Children.Add(inventorySurface);
        Grid.SetRow(lower, 2);
        root.Children.Add(lower);

        TextBlock footer = new()
        {
            Text = "Equipment and inventory are projections of observed Avendar state; unknown data is never fabricated.",
            Foreground = NexMudTheme.Faint,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 0, 10, 7)
        };
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        return root;
    }

    private string? CurrentInspectedItem => _hoveredItem ?? _selectedItem;
    private string? CurrentInspectedSlot => _hoveredItem is not null ? _hoveredSlot : _selectedSlot;

    private void ConfigurePaperDollGrid()
    {
        _paperDoll.ColumnDefinitions.Clear();
        _paperDoll.RowDefinitions.Clear();
        for (int column = 0; column < 3; column++)
        {
            _paperDoll.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)));
        }
        for (int row = 0; row < 8; row++)
        {
            _paperDoll.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        _paperDoll.ColumnSpacing = 5;
        _paperDoll.RowSpacing = 4;
    }

    private void RenderPaperDoll()
    {
        _paperDoll.Children.Clear();
        foreach (SlotSpec spec in PaperDollSlots)
        {
            EquipmentSlotState? slot = _character.Equipment.Slots.FirstOrDefault(candidate =>
                NormalizeSlot(candidate.Slot).Equals(spec.Key, StringComparison.OrdinalIgnoreCase) &&
                candidate.Ordinal == spec.Ordinal);

            string? item = slot?.Item;
            Border cell = BuildEquipmentSlotCell(spec, item);
            Grid.SetRow(cell, spec.Row);
            Grid.SetColumn(cell, spec.Column);
            _paperDoll.Children.Add(cell);
        }
    }

    private Border BuildEquipmentSlotCell(SlotSpec spec, string? item)
    {
        bool occupied = !string.IsNullOrWhiteSpace(item);
        bool selected = occupied && string.Equals(item, _selectedItem, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(spec.Label, _selectedSlot, StringComparison.OrdinalIgnoreCase);

        Grid body = new() { RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 2 };
        StackPanel label = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        label.Children.Add(NexMudIcons.Create(spec.Icon, 12, occupied ? NexMudTheme.AccentPrimary : NexMudTheme.Faint));
        label.Children.Add(new TextBlock
        {
            Text = spec.Label.ToUpperInvariant(),
            Foreground = occupied ? NexMudTheme.Muted : NexMudTheme.Faint,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.SemiBold
        });
        body.Children.Add(label);

        TextBlock value = new()
        {
            Text = occupied ? item! : "—",
            Foreground = occupied ? NexMudTheme.Parchment : NexMudTheme.Faint,
            FontSize = NexTypography.CompactData,
            FontWeight = occupied ? FontWeight.SemiBold : FontWeight.Normal,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 38,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetRow(value, 1);
        body.Children.Add(value);

        Border cell = new()
        {
            Background = occupied ? NexMudTheme.RaisedSurfaceGradient : NexMudTheme.InsetGradient,
            BorderBrush = selected ? NexMudTheme.AccentBright : occupied ? NexMudTheme.AntiqueBrass : NexMudTheme.Divider,
            BorderThickness = new Thickness(selected ? 2 : 1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(5, 5),
            MinHeight = 48,
            Opacity = occupied ? 1 : 0.62,
            Child = body
        };

        if (occupied)
        {
            cell.PointerEntered += (_, _) => SetHoveredItem(item!, spec.Label);
            cell.PointerExited += (_, _) => ClearHoveredItem(item!, spec.Label);
            cell.PointerPressed += (_, args) =>
            {
                _selectedItem = item;
                _selectedSlot = spec.Label;
                RenderPaperDoll();
                RenderInventory();
                RenderInspectedItem();
                args.Handled = true;
            };
        }
        else
        {
            ToolTip.SetTip(cell, $"{spec.Label}: empty or not observed");
        }

        return cell;
    }

    private void RenderStats()
    {
        _attributes.Children.Clear();
        foreach (CharacterAttributeViewModel attribute in _viewModel.Attributes.Take(6))
        {
            _attributes.Children.Add(AttributeFact(attribute.Name, attribute.Display));
        }

        _combat.Text = $"Hit {_viewModel.Hitroll}   Dam {_viewModel.Damroll}   Save {_viewModel.Saves}\n" +
                       $"AC {_viewModel.ArmorClass}   Explore {_viewModel.Exploration}";
        _capacity.Text = _viewModel.Items;
        _weight.Text = _viewModel.Weight;
        _wealth.Text = _viewModel.Wealth;
    }

    private void RenderInventory()
    {
        _inventoryItems.Children.Clear();
        string query = (_search.Text ?? string.Empty).Trim();
        IReadOnlyList<string> source = _character.CarriedItems;
        string[] visible = source
            .Where(item => query.Length == 0 || item.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (_character.InventoryCompleteness == ObservationCompleteness.Unknown && source.Count == 0)
        {
            _inventoryItems.Children.Add(EmptyInventoryText("Inventory not observed. Use Refresh or the Inv shortcut to populate it."));
        }
        else if (source.Count == 0)
        {
            _inventoryItems.Children.Add(EmptyInventoryText("Nothing carried."));
        }
        else if (visible.Length == 0)
        {
            _inventoryItems.Children.Add(EmptyInventoryText("No carried items match the search."));
        }
        else
        {
            foreach (string item in visible)
            {
                Button row = new()
                {
                    Content = item,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = string.Equals(item, _selectedItem, StringComparison.OrdinalIgnoreCase)
                        ? NexMudTheme.InteractiveSurface
                        : Brushes.Transparent,
                    Foreground = NexMudTheme.Parchment,
                    BorderBrush = NexMudTheme.Divider,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    CornerRadius = new CornerRadius(0),
                    Padding = new Thickness(6, 6),
                    FontSize = NexTypography.Body
                };
                row.PointerEntered += (_, _) => SetHoveredItem(item, null);
                row.PointerExited += (_, _) => ClearHoveredItem(item, null);
                row.Click += (_, _) =>
                {
                    _selectedItem = item;
                    _selectedSlot = null;
                    RenderInventory();
                    RenderPaperDoll();
                    RenderInspectedItem();
                };
                _inventoryItems.Children.Add(row);
            }
        }

        string completeness = _character.InventoryCompleteness == ObservationCompleteness.Unknown
            ? "not observed"
            : _character.InventoryCompleteness.ToString().ToLowerInvariant();
        _inventoryStatus.Text = source.Count == 0
            ? completeness
            : $"{visible.Length:N0} shown • {source.Count:N0} observed • {completeness}";
    }

    private void SetHoveredItem(string item, string? slot)
    {
        _hoveredItem = item;
        _hoveredSlot = slot;
        RenderInspectedItem();
    }

    private void ClearHoveredItem(string item, string? slot)
    {
        if (!string.Equals(_hoveredItem, item, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_hoveredSlot, slot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _hoveredItem = null;
        _hoveredSlot = null;
        RenderInspectedItem();
    }

    private void RenderInspectedItem()
    {
        string? item = CurrentInspectedItem;
        string? slot = CurrentInspectedSlot;
        if (string.IsNullOrWhiteSpace(item))
        {
            _selectedItemDetail.Content = null;
            _selectedItemHint.IsVisible = true;
            _identifySelected.IsVisible = false;
            return;
        }

        _selectedItemHint.IsVisible = false;
        _selectedItemDetail.Content = ItemInspectionPopover.Build(
            _inspectionResolver(item, slot),
            compact: false);
        _identifySelected.IsVisible = true;
    }

    private static Control AttributeFact(string label, string value)
    {
        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(0, 0, 12, 4)
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = NexMudTheme.Muted,
            FontSize = NexTypography.Hud,
            FontWeight = FontWeight.SemiBold
        });
        row.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = NexMudTheme.Parchment,
            FontSize = NexTypography.BodyStrong,
            FontWeight = FontWeight.Bold
        });
        return row;
    }

    private static TextBlock EmptyInventoryText(string text) => new()
    {
        Text = text,
        Foreground = NexMudTheme.Faint,
        FontSize = NexTypography.Body,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(4, 7)
    };

    private static Control CompactFact(string label, TextBlock value)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = NexMudTheme.Faint,
            FontSize = NexTypography.Metadata
        });
        value.Foreground = NexMudTheme.Parchment;
        value.FontSize = NexTypography.Body;
        value.FontWeight = FontWeight.SemiBold;
        value.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Foreground = NexMudTheme.AccentBright,
        FontSize = NexTypography.SectionTitle,
        FontFamily = NexMudTheme.Interface,
        FontWeight = FontWeight.Bold
    };

    private static string NormalizeSlot(string slot) => slot.Trim().ToLowerInvariant();
}
