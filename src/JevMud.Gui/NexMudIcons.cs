using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace JevMud.Gui;

internal enum NexIconKind
{
    Logo,
    World,
    Character,
    Abilities,
    Codex,
    Map,
    Automation,
    Scripting,
    Jev,
    Settings,
    Search,
    Log,
    History,
    Previous,
    Next,
    Send,
    Look,
    Scan,
    Where,
    Inventory,
    Hp,
    Mana,
    Movement,
    Xp,
    Position,
    Combat,
    Target,
    JevState,
    People,
    Objects,
    Fixtures,
    Corpses,
    Strength,
    Dexterity,
    Constitution,
    Intelligence,
    Wisdom,
    Charisma,
    Connection,
    Command,
    CharacterCrest
}

internal sealed class NexIconView : Viewbox
{
    private readonly Avalonia.Controls.Shapes.Path _path;

    public NexIconView(string geometry, double size, IBrush stroke)
    {
        Width = size;
        Height = size;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _path = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse(geometry),
            Stroke = stroke,
            StrokeThickness = 1.6,
            Fill = Brushes.Transparent,
            Stretch = Stretch.Uniform
        };
        Child = _path;
    }

    public IBrush Stroke
    {
        get => _path.Stroke ?? Brushes.Transparent;
        set => _path.Stroke = value;
    }
}

internal static class NexMudIcons
{
    private static readonly IReadOnlyDictionary<NexIconKind, string> Geometry =
        new Dictionary<NexIconKind, string>
        {
            [NexIconKind.Logo] = "M12 1 L14 8 L23 10 L16 13 L14 23 L11 16 L2 14 L8 11 Z M12 5 L12 19 M5 12 L19 12",
            [NexIconKind.World] = "M12 2 A10 10 0 1 0 12 22 A10 10 0 1 0 12 2 M2 12 L22 12 M12 2 C8 6 8 18 12 22 M12 2 C16 6 16 18 12 22",
            [NexIconKind.Character] = "M12 3 A4 4 0 1 0 12 11 A4 4 0 1 0 12 3 M5 21 C5 15 19 15 19 21",
            [NexIconKind.Abilities] = "M12 2 L14.5 9.5 L22 12 L14.5 14.5 L12 22 L9.5 14.5 L2 12 L9.5 9.5 Z",
            [NexIconKind.Codex] = "M4 4 C7 3 10 4 12 6 C14 4 17 3 20 4 L20 20 C17 19 14 20 12 22 C10 20 7 19 4 20 Z M12 6 L12 22",
            [NexIconKind.Map] = "M3 5 L8 3 L16 6 L21 4 L21 19 L16 21 L8 18 L3 20 Z M8 3 L8 18 M16 6 L16 21",
            [NexIconKind.Automation] = "M5 4 A2 2 0 1 0 5 8 A2 2 0 1 0 5 4 M19 4 A2 2 0 1 0 19 8 A2 2 0 1 0 19 4 M5 16 A2 2 0 1 0 5 20 A2 2 0 1 0 5 16 M19 16 A2 2 0 1 0 19 20 A2 2 0 1 0 19 16 M7 6 L12 6 L12 18 L17 18 M12 12 L17 6",
            [NexIconKind.Scripting] = "M8 6 L3 12 L8 18 M16 6 L21 12 L16 18 M14 4 L10 20",
            [NexIconKind.Jev] = "M12 2 L19 6 L22 12 L19 18 L12 22 L5 18 L2 12 L5 6 Z M7 12 C9 8 15 8 17 12 C15 16 9 16 7 12 Z M12 10 A2 2 0 1 0 12 14 A2 2 0 1 0 12 10",
            [NexIconKind.Settings] = "M12 4 L14 4.5 L15 7 L18 8 L20 7 L22 10 L20 12 L20 15 L22 17 L20 20 L17 19 L15 20 L14 22 L10 22 L9 20 L6 19 L4 20 L2 17 L4 15 L4 12 L2 10 L4 7 L7 8 L9 7 L10 4.5 Z M9 13 A3 3 0 1 0 15 13 A3 3 0 1 0 9 13",
            [NexIconKind.Search] = "M4 10 A6 6 0 1 0 16 10 A6 6 0 1 0 4 10 M15 15 L21 21",
            [NexIconKind.Log] = "M5 3 L17 3 L20 6 L20 21 L5 21 Z M17 3 L17 7 L20 7 M8 10 L17 10 M8 14 L17 14 M8 18 L14 18",
            [NexIconKind.History] = "M4 7 L4 3 L8 3 M4 4 C1 8 2 15 7 19 C12 23 19 20 21 14 C23 8 19 3 13 3 C10 3 7 4 5 6 M12 7 L12 13 L16 15",
            [NexIconKind.Previous] = "M5 15 L12 8 L19 15",
            [NexIconKind.Next] = "M5 9 L12 16 L19 9",
            [NexIconKind.Send] = "M3 4 L22 12 L3 20 L7 13 L16 12 L7 11 Z",
            [NexIconKind.Look] = "M2 12 C6 6 18 6 22 12 C18 18 6 18 2 12 Z M9 12 A3 3 0 1 0 15 12 A3 3 0 1 0 9 12",
            [NexIconKind.Scan] = "M4 8 L4 4 L8 4 M16 4 L20 4 L20 8 M20 16 L20 20 L16 20 M8 20 L4 20 L4 16 M8 12 L16 12 M12 8 L12 16",
            [NexIconKind.Where] = "M12 22 C17 16 20 12 20 8 A8 8 0 1 0 4 8 C4 12 7 16 12 22 Z M9 8 A3 3 0 1 0 15 8 A3 3 0 1 0 9 8",
            [NexIconKind.Inventory] = "M5 8 L19 8 L21 21 L3 21 Z M8 8 C8 3 16 3 16 8",
            [NexIconKind.Hp] = "M12 21 C2 15 2 7 7 5 C10 4 12 7 12 7 C12 7 14 4 17 5 C22 7 22 15 12 21 Z",
            [NexIconKind.Mana] = "M12 2 C16 8 19 11 19 15 A7 7 0 1 1 5 15 C5 11 8 8 12 2 Z",
            [NexIconKind.Movement] = "M4 17 C8 6 15 4 21 4 C20 11 17 18 7 20 M5 19 C9 15 13 12 18 9",
            [NexIconKind.Xp] = "M12 2 L15 9 L22 9 L17 14 L19 22 L12 18 L5 22 L7 14 L2 9 L9 9 Z",
            [NexIconKind.Position] = "M12 2 L16 10 L22 12 L16 14 L12 22 L8 14 L2 12 L8 10 Z",
            [NexIconKind.Combat] = "M5 3 L21 19 M19 3 L3 19 M4 20 L8 16 M20 20 L16 16",
            [NexIconKind.Target] = "M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 M12 7 A5 5 0 1 0 12 17 A5 5 0 1 0 12 7 M12 10 L12 14 M10 12 L14 12",
            [NexIconKind.JevState] = "M12 3 L19 7 L19 17 L12 21 L5 17 L5 7 Z M8 12 L11 15 L16 9",
            [NexIconKind.People] = "M8 5 A3 3 0 1 0 8 11 A3 3 0 1 0 8 5 M3 20 C3 15 13 15 13 20 M17 7 A2.5 2.5 0 1 0 17 12 A2.5 2.5 0 1 0 17 7 M14 15 C19 14 22 16 22 20",
            [NexIconKind.Objects] = "M12 2 L21 7 L21 17 L12 22 L3 17 L3 7 Z M3 7 L12 12 L21 7 M12 12 L12 22",
            [NexIconKind.Fixtures] = "M4 6 L20 6 L20 18 L4 18 Z M8 18 L8 22 M16 18 L16 22 M9 10 L15 10 M9 14 L15 14",
            [NexIconKind.Corpses] = "M5 20 L7 8 L17 8 L19 20 Z M8 8 C8 3 16 3 16 8 M10 13 L14 17 M14 13 L10 17",
            [NexIconKind.Strength] = "M5 20 L19 6 M7 4 L20 17 M4 17 L7 20 M17 4 L20 7",
            [NexIconKind.Dexterity] = "M3 12 L20 5 L15 20 L11 14 Z M11 14 L7 18",
            [NexIconKind.Constitution] = "M12 2 L20 5 L19 13 C18 18 15 21 12 22 C9 21 6 18 5 13 L4 5 Z",
            [NexIconKind.Intelligence] = "M5 4 L19 4 L19 20 L5 20 Z M8 8 L16 8 M8 12 L16 12 M8 16 L13 16",
            [NexIconKind.Wisdom] = "M2 12 C6 7 18 7 22 12 C18 17 6 17 2 12 Z M10 12 A2 2 0 1 0 14 12 A2 2 0 1 0 10 12",
            [NexIconKind.Charisma] = "M12 3 L14 8 L20 8 L16 12 L18 18 L12 15 L6 18 L8 12 L4 8 L10 8 Z",
            [NexIconKind.Connection] = "M4 12 L9 12 M15 12 L20 12 M9 8 L15 8 L15 16 L9 16 Z",
            [NexIconKind.Command] = "M4 7 L10 12 L4 17 M12 18 L20 18",
            [NexIconKind.CharacterCrest] = "M12 2 L20 6 L19 15 C18 19 15 22 12 23 C9 22 6 19 5 15 L4 6 Z M8 10 L12 6 L16 10 M8 14 L12 18 L16 14"
        };

    public static NexIconView Create(NexIconKind icon, double size, IBrush stroke) =>
        new(Geometry.TryGetValue(icon, out string? data) ? data : Geometry[NexIconKind.World], size, stroke);
}
