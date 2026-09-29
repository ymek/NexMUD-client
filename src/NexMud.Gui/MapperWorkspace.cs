using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NexMud.Client.Knowledge;
using NexMud.Client.Runtime;
using NexMud.Client.Settings;
using NexMud.Contracts.Events;
using NexMud.Contracts.State;

namespace NexMud.Gui;

internal sealed record MapperSceneNode(
    string RoomId,
    double X,
    double Y,
    int Level,
    int StackIndex,
    int StackCount,
    string Label,
    string Meta,
    bool Avoid,
    bool Disconnected,
    IReadOnlyList<string> UnexploredDirections);

internal sealed record MapperSceneEdge(
    string FromRoomId,
    string ToRoomId,
    string Direction,
    ExitDoorState DoorState,
    ExitTraversability Traversability,
    string? BlockReason,
    bool Vertical,
    bool ConstraintConflict);

internal sealed record MapperScene(
    string OriginRoomId,
    IReadOnlyDictionary<string, MapperSceneNode> Nodes,
    IReadOnlyList<MapperSceneEdge> Edges,
    int ConstraintConflictCount,
    int CoordinateCollisionCount);

internal static class MapperSceneBuilder
{
    public static MapperScene Build(MapperGraphSnapshot graph)
    {
        MapperTopologySnapshot topology = MapperTopologyLayout.Build(graph);
        Dictionary<string, MapperSceneNode> nodes = new(StringComparer.Ordinal);
        foreach ((string roomId, MapperLayoutRoom layout) in topology.Rooms)
        {
            MapperGraphRoom room = layout.Room;
            string meta = string.Join(" · ", new[]
            {
                room.Area,
                room.Avoid ? "AVOID" : null,
                layout.Disconnected ? "DISCONNECTED" : null,
                room.UnexploredExitCount > 0 ? $"{room.UnexploredExitCount:N0} unexplored" : null
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            if (string.IsNullOrWhiteSpace(meta)) meta = $"{room.VisitCount:N0} visits";
            nodes[roomId] = new MapperSceneNode(
                roomId,
                layout.Coordinate.X,
                layout.Coordinate.Y,
                layout.Coordinate.Level,
                layout.StackIndex,
                layout.StackCount,
                room.Label ?? room.Name ?? "Known room",
                meta,
                room.Avoid,
                layout.Disconnected,
                room.UnexploredDirections ?? Array.Empty<string>());
        }

        List<MapperSceneEdge> edges = topology.Edges
            .Where(edge => nodes.ContainsKey(edge.Edge.FromRoomId) && nodes.ContainsKey(edge.Edge.ToRoomId))
            .Select(edge => new MapperSceneEdge(
                edge.Edge.FromRoomId,
                edge.Edge.ToRoomId,
                edge.Edge.Direction,
                edge.Edge.DoorState,
                ParseTraversability(edge.Edge.Traversability),
                edge.Edge.BlockReason,
                edge.Vertical,
                edge.ConstraintConflict))
            .ToList();

        return new MapperScene(
            topology.OriginRoomId,
            nodes,
            edges,
            topology.ConstraintConflictCount,
            topology.CoordinateCollisionCount);
    }

    private static ExitTraversability ParseTraversability(string? value) =>
        Enum.TryParse(value, true, out ExitTraversability parsed) ? parsed : ExitTraversability.Unknown;
}

/// <summary>
/// Immediate-mode map renderer. The entire map is one Avalonia control, with bounded drawing,
/// pan/zoom, and hit-testing. No room/edge controls are created and no backing Canvas exists.
/// </summary>
internal sealed class MapperViewport : Control
{
    private static readonly Typeface Typeface = new("Menlo");
    private const double XSpacing = 190;
    private const double YSpacing = 112;
    private const double NodeWidth = 164;
    private const double NodeHeight = 62;
    private MapperScene? _scene;
    private string? _currentRoomId;
    private string? _targetRoomId;
    private HashSet<string> _routeRooms = new(StringComparer.Ordinal);
    private HashSet<string> _routeEdges = new(StringComparer.Ordinal);
    private double _zoom = 1.0;
    private Vector _pan;
    private bool _dragging;
    private Point _dragStart;
    private Vector _panStart;
    private bool _moved;

    public MapperViewport()
    {
        ClipToBounds = true;
        Focusable = true;
        PointerWheelChanged += OnWheel;
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
    }

    public event Action<string>? RoomInvoked;

    public void SetScene(MapperScene? scene, string? currentRoomId, string? targetRoomId, KnowledgeRoute? route, bool recenter)
    {
        _scene = scene;
        _currentRoomId = currentRoomId;
        _targetRoomId = targetRoomId;
        _routeRooms = route is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : route.Steps.SelectMany(step => new[] { step.FromRoomId, step.ToRoomId }).ToHashSet(StringComparer.Ordinal);
        _routeEdges = route is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : route.Steps.Select(step => EdgeKey(step.FromRoomId, step.Direction, step.ToRoomId)).ToHashSet(StringComparer.Ordinal);
        if (recenter) _pan = default;
        InvalidateVisual();
    }

    public void UpdateCurrentRoom(string? currentRoomId)
    {
        if (string.Equals(_currentRoomId, currentRoomId, StringComparison.Ordinal)) return;
        _currentRoomId = currentRoomId;
        InvalidateVisual();
    }

    public void ResetView()
    {
        _zoom = 1.0;
        _pan = default;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        Rect bounds = new(Bounds.Size);
        context.DrawRectangle(UiTheme.Console, null, bounds);
        MapperScene? scene = _scene;
        if (scene is null || scene.Nodes.Count == 0)
        {
            DrawText(context, "No mapped rooms yet.", new Point(18, 18), NexTypography.Body, UiTheme.Muted);
            return;
        }

        MapperSceneNode center = ResolveCenter(scene);

        // Unresolved exits are topology, too. Draw them as short stubs so a newly discovered room
        // does not look isolated merely because the destination has not been observed yet.
        foreach (MapperSceneNode node in scene.Nodes.Values)
        {
            Point origin = Project(node, center);
            foreach (string rawDirection in node.UnexploredDirections.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!MapperTopologyLayout.TryGetOffset(rawDirection, out MapperCoordinate offset)) continue;
                Vector delta = offset.Level != 0
                    ? new Vector(Math.Sign(offset.Level) * 22, -Math.Sign(offset.Level) * 18)
                    : new Vector(offset.X * 36, offset.Y * 28);
                Point end = origin + delta * _zoom;
                if (!LineMayBeVisible(origin, end, bounds, 50)) continue;
                context.DrawLine(new Pen(UiTheme.Faint, 1), origin, end);
                string marker = offset.Level > 0 ? "UP ?" : offset.Level < 0 ? "DN ?" : "?";
                DrawText(context, marker, new Point(end.X + 2, end.Y + 2), NexTypography.Metadata, UiTheme.Faint);
            }
        }

        foreach (IGrouping<string, MapperSceneEdge> connection in scene.Edges
                     .GroupBy(edge => ConnectionKey(edge.FromRoomId, edge.ToRoomId), StringComparer.Ordinal))
        {
            MapperSceneEdge representative = connection.First();
            if (!scene.Nodes.TryGetValue(representative.FromRoomId, out MapperSceneNode? from) || from is null ||
                !scene.Nodes.TryGetValue(representative.ToRoomId, out MapperSceneNode? to) || to is null) continue;

            Point start = Project(from, center);
            Point end = Project(to, center);
            if (!LineMayBeVisible(start, end, bounds, 100)) continue;

            bool routeConnection = connection.Any(edge =>
                _routeEdges.Contains(EdgeKey(edge.FromRoomId, edge.Direction, edge.ToRoomId)));
            context.DrawLine(
                new Pen(routeConnection ? UiTheme.Accent : UiTheme.Divider, routeConnection ? 2.5 : 1.2),
                start,
                end);

            foreach (MapperSceneEdge edge in connection)
            {
                if (!scene.Nodes.TryGetValue(edge.FromRoomId, out MapperSceneNode? edgeFrom) || edgeFrom is null ||
                    !scene.Nodes.TryGetValue(edge.ToRoomId, out MapperSceneNode? edgeTo) || edgeTo is null) continue;
                Point edgeStart = Project(edgeFrom, center);
                Point edgeEnd = Project(edgeTo, center);
                bool route = _routeEdges.Contains(EdgeKey(edge.FromRoomId, edge.Direction, edge.ToRoomId));
                DrawDirectionalEdgeState(context, edge, edgeStart, edgeEnd, EdgeBrush(edge, route));
            }
        }

        foreach (MapperSceneNode node in scene.Nodes.Values)
        {
            Point point = Project(node, center);
            Rect nodeBounds = new(point.X - NodeWidth / 2, point.Y - NodeHeight / 2, NodeWidth, NodeHeight);
            if (!nodeBounds.Intersects(bounds.Inflate(20))) continue;
            bool current = string.Equals(node.RoomId, _currentRoomId, StringComparison.Ordinal);
            bool target = string.Equals(node.RoomId, _targetRoomId, StringComparison.Ordinal);
            bool route = _routeRooms.Contains(node.RoomId);
            IBrush fill = current ? UiTheme.Accent : route ? UiTheme.Raised : UiTheme.Surface;
            IBrush border = target ? UiTheme.Warning
                : node.Avoid || node.Disconnected ? UiTheme.Danger
                : current ? UiTheme.Accent
                : UiTheme.Divider;
            context.DrawRectangle(fill, new Pen(border, target || node.Avoid || node.Disconnected ? 2 : 1), nodeBounds, 3, 3);
            IBrush primary = current ? Brushes.Black : UiTheme.Text;
            IBrush secondary = current ? Brushes.Black : node.Avoid || node.Disconnected ? UiTheme.Danger : UiTheme.Muted;
            DrawText(context, Ellipsize(node.Label, 24), new Point(nodeBounds.X + 8, nodeBounds.Y + 8), NexTypography.Metadata, primary);
            DrawText(context, Ellipsize(node.Meta, 27), new Point(nodeBounds.X + 8, nodeBounds.Y + 34), NexTypography.Metadata, secondary);
        }
    }

    private string? RoomAt(Point pointer)
    {
        MapperScene? scene = _scene;
        if (scene is null || scene.Nodes.Count == 0) return null;
        MapperSceneNode center = ResolveCenter(scene);
        foreach (MapperSceneNode node in scene.Nodes.Values)
        {
            Point point = Project(node, center);
            if (new Rect(point.X - NodeWidth / 2, point.Y - NodeHeight / 2, NodeWidth, NodeHeight).Contains(pointer))
                return node.RoomId;
        }
        return null;
    }

    private MapperSceneNode ResolveCenter(MapperScene scene)
    {
        string centerId = _currentRoomId is not null && scene.Nodes.ContainsKey(_currentRoomId)
            ? _currentRoomId
            : scene.OriginRoomId;
        return scene.Nodes.TryGetValue(centerId, out MapperSceneNode? centered) && centered is not null
            ? centered
            : scene.Nodes.Values.First();
    }

    private Point Project(MapperSceneNode node, MapperSceneNode center)
    {
        Point viewportCenter = new(Bounds.Width / 2 + _pan.X, Bounds.Height / 2 + _pan.Y);
        return new Point(
            viewportCenter.X + (node.X - center.X) * XSpacing * _zoom,
            viewportCenter.Y + (node.Y - center.Y) * YSpacing * _zoom);
    }

    private static IBrush EdgeBrush(MapperSceneEdge edge, bool route)
    {
        if (route) return UiTheme.Accent;
        if (edge.DoorState == ExitDoorState.Locked || edge.Traversability == ExitTraversability.Blocked)
            return UiTheme.Danger;
        if (edge.DoorState == ExitDoorState.Closed) return UiTheme.Warning;
        if (edge.Vertical) return UiTheme.Success;
        if (edge.Traversability == ExitTraversability.Unknown) return UiTheme.Faint;
        return UiTheme.Divider;
    }

    private static void DrawDirectionalEdgeState(
        DrawingContext context,
        MapperSceneEdge edge,
        Point start,
        Point end,
        IBrush brush)
    {
        Point marker = new(
            start.X + (end.X - start.X) * 0.30,
            start.Y + (end.Y - start.Y) * 0.30);
        string direction = MapperTopologyLayout.AbbreviateDirection(edge.Direction);
        string? state = edge.DoorState switch
        {
            ExitDoorState.Locked => "LOCKED",
            ExitDoorState.Closed => "CLOSED",
            _ when edge.Traversability == ExitTraversability.Blocked => "BLOCKED",
            _ => null
        };
        string text = string.IsNullOrWhiteSpace(state) ? direction : $"{direction} {state}";
        DrawText(context, text, new Point(marker.X + 4, marker.Y - 13), NexTypography.Metadata, brush);

        if (edge.DoorState is ExitDoorState.Closed or ExitDoorState.Locked)
        {
            Rect door = new(marker.X - 6, marker.Y - 6, 12, 12);
            context.DrawRectangle(UiTheme.Console, new Pen(brush, 1.4), door, 1, 1);
            if (edge.DoorState == ExitDoorState.Locked)
                DrawText(context, "L", new Point(marker.X - 3.4, marker.Y - 6.4), NexTypography.Metadata, brush);
        }
        else if (edge.Traversability == ExitTraversability.Blocked)
        {
            context.DrawLine(new Pen(brush, 1.4), new Point(marker.X - 4, marker.Y - 4), new Point(marker.X + 4, marker.Y + 4));
            context.DrawLine(new Pen(brush, 1.4), new Point(marker.X + 4, marker.Y - 4), new Point(marker.X - 4, marker.Y + 4));
        }
    }

    private static string ConnectionKey(string firstRoomId, string secondRoomId) =>
        string.CompareOrdinal(firstRoomId, secondRoomId) <= 0
            ? $"{firstRoomId}\n{secondRoomId}"
            : $"{secondRoomId}\n{firstRoomId}";

    private static bool LineMayBeVisible(Point a, Point b, Rect bounds, double margin)
    {
        Rect expanded = bounds.Inflate(margin);
        return expanded.Contains(a) || expanded.Contains(b) ||
               new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(1, Math.Abs(a.X - b.X)), Math.Max(1, Math.Abs(a.Y - b.Y))).Intersects(expanded);
    }

    private static string EdgeKey(string from, string direction, string to) => $"{from}\n{direction}\n{to}";

    private static void DrawText(DrawingContext context, string text, Point origin, double size, IBrush brush)
    {
        FormattedText formatted = new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, size, brush);
        context.DrawText(formatted, origin);
    }

    private static string Ellipsize(string text, int maximumCharacters) =>
        text.Length <= maximumCharacters ? text : text[..Math.Max(1, maximumCharacters - 1)] + "…";

    private void OnWheel(object? sender, PointerWheelEventArgs args)
    {
        double factor = args.Delta.Y > 0 ? 1.12 : 1 / 1.12;
        _zoom = Math.Clamp(_zoom * factor, 0.45, 2.4);
        args.Handled = true;
        InvalidateVisual();
    }

    private void OnPressed(object? sender, PointerPressedEventArgs args)
    {
        PointerPoint point = args.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        _dragging = true;
        _moved = false;
        _dragStart = args.GetPosition(this);
        _panStart = _pan;
        Focus();
    }

    private void OnMoved(object? sender, PointerEventArgs args)
    {
        if (!_dragging) return;
        Point current = args.GetPosition(this);
        Vector delta = current - _dragStart;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 4) _moved = true;
        _pan = _panStart + delta;
        InvalidateVisual();
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs args)
    {
        if (!_dragging) return;
        _dragging = false;
        if (_moved) return;
        string? roomId = RoomAt(args.GetPosition(this));
        if (roomId is not null) RoomInvoked?.Invoke(roomId);
    }


}

/// <summary>
/// Persistent mapper workspace. All SQLite reads/layout work happen on workers through a
/// read-only repository. MainWindow only activates/deactivates this control and supplies live state.
/// </summary>
internal sealed class MapperWorkspace : UserControl, IDisposable
{
    private readonly NexMudRuntime _runtime;
    private readonly MapperReadRepository _repository;
    private readonly Func<Task> _openSettings;
    private readonly Func<string, Task> _editRoom;
    private readonly Func<string, Task> _addExit;
    private readonly CancellationToken _applicationToken;
    private readonly MapperViewport _viewport = new();
    private readonly TextBox _search = new();
    private readonly StackPanel _resultList = new() { Spacing = 1 };
    private Border? _resultFrame;
    private readonly TextBlock _room = new();
    private readonly TextBlock _graphStatus = new();
    private readonly TextBlock _navigationStatus = new();
    private readonly TextBlock _destination = new();
    private readonly TextBlock _route = new();
    private readonly TextBlock _message = new();
    private readonly Button _go = new();
    private readonly Button _pauseResume = new();
    private readonly Button _step = new();
    private readonly Button _stop = new();
    private readonly Button _more = new();
    private readonly Button _nearest = new();
    private readonly Button _edit = new();
    private readonly Button _addExitButton = new();
    private CancellationTokenSource? _graphCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _routeCts;
    private StateSnapshot _state = StateSnapshot.Initial;
    private MapperScene? _scene;
    private MapperGraphSnapshot? _graph;
    private MapperDestinationSearchResult? _selectedDestination;
    private KnowledgeRoute? _plannedRoute;
    private string? _loadedOrigin;
    private int _visibleRoomBudget = 48;
    private bool _active;
    private bool _loadingGraph;
    private int _graphGeneration;

    public MapperWorkspace(
        NexMudRuntime runtime,
        Func<Task> openSettings,
        Func<string, Task> editRoom,
        Func<string, Task> addExit,
        CancellationToken applicationToken)
    {
        _runtime = runtime;
        FontFamily = UiTheme.Sans;
        FontSize = NexTypography.Body;
        _repository = new MapperReadRepository(runtime.Knowledge.DatabasePath);
        _openSettings = openSettings;
        _editRoom = editRoom;
        _addExit = addExit;
        _applicationToken = applicationToken;
        _runtime.Knowledge.MapperKnowledgeChanged += HandleMapperKnowledgeChanged;
        Content = Build();
        UpdateNavigation(runtime.Navigator.Current);
    }

    private void HandleMapperKnowledgeChanged()
    {
        if (!_active) return;
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_active) QueueGraphLoad(force: true);
        });
    }

    public void Activate()
    {
        _active = true;
        UpdateState(_runtime.State.Current);
        QueueGraphLoad(force: _graph is null);
    }

    public void Deactivate()
    {
        _active = false;
        _searchCts?.Cancel();
        _routeCts?.Cancel();
        _graphCts?.Cancel();
        _loadingGraph = false;
    }

    public void UpdateState(StateSnapshot state)
    {
        string? previousRoomId = _state.Room.Id;
        _state = state;
        _room.Text = state.Room.Name ?? "Current room unavailable";
        _edit.IsEnabled = !string.IsNullOrWhiteSpace(state.Room.Id);
        _addExitButton.IsEnabled = !string.IsNullOrWhiteSpace(state.Room.Id);
        _viewport.UpdateCurrentRoom(state.Room.Id);
        if (_active && !string.Equals(previousRoomId, state.Room.Id, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(state.Room.Id))
        {
            QueueGraphLoad(force: true);
            if (_selectedDestination is not null) _ = PlanRouteAsync();
        }
        UpdateNavigation(_runtime.Navigator.Current);
    }

    public void UpdateNavigation(AutoMoveStateChanged navigation)
    {
        _navigationStatus.Text = FormatNavigation(navigation);
        _navigationStatus.Foreground = navigation.Status switch
        {
            AutoMoveStatus.Moving or AutoMoveStatus.Replanning => UiTheme.Accent,
            AutoMoveStatus.Paused or AutoMoveStatus.Recovering => UiTheme.Warning,
            AutoMoveStatus.Failed => UiTheme.Danger,
            AutoMoveStatus.Completed => UiTheme.Success,
            _ => UiTheme.Muted
        };
        bool moving = navigation.Status is AutoMoveStatus.Moving or AutoMoveStatus.Recovering or AutoMoveStatus.Replanning;
        bool paused = navigation.Status == AutoMoveStatus.Paused;
        bool busy = navigation.Status is AutoMoveStatus.Moving or AutoMoveStatus.Paused or AutoMoveStatus.Planning or AutoMoveStatus.Recovering or AutoMoveStatus.Replanning;
        bool hasRoute = _selectedDestination is not null && _plannedRoute is { Steps.Count: > 0 };
        MapperPreferences settings = _runtime.Settings.Mapper ?? new MapperPreferences();
        _go.IsEnabled = settings.AutoMoveEnabled && hasRoute && !moving;
        _step.IsEnabled = settings.AutoMoveEnabled && hasRoute && !moving;
        _pauseResume.IsVisible = moving || paused;
        _pauseResume.Content = paused ? "Resume" : "Pause";
        _stop.IsEnabled = busy;
        if (!string.IsNullOrWhiteSpace(navigation.Reason)) SetMessage(navigation.Reason!, navigation.Status == AutoMoveStatus.Failed);
    }

    public void Refresh()
    {
        QueueGraphLoad(force: true);
    }

    public async Task SetDestinationAsync(MapperDestinationSearchResult destination)
    {
        _selectedDestination = destination;
        _destination.Text = destination.DisplayName;
        // Clear the originating query before route planning. Route planning can take long enough
        // for the user to begin another search; clearing after the await would erase that newer query.
        ClearSearchAfterSelection();
        await PlanRouteAsync().ConfigureAwait(true);
    }

    private void ClearSearchAfterSelection()
    {
        _resultList.Children.Clear();
        if (_resultFrame is not null) _resultFrame.IsVisible = false;
        _search.Text = string.Empty;
    }

    private Control Build()
    {
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Background = UiTheme.Window
        };

        StackPanel header = new() { Spacing = 6, Margin = new Thickness(10, 8, 10, 8) };
        Grid searchRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 5 };
        _search.PlaceholderText = "Find a room, MOB, object, fixture, or item source…";
        _search.MinHeight = 30;
        _search.TextChanged += (_, _) => QueueSearch();
        searchRow.Children.Add(_search);

        _nearest.Content = "Nearest";
        _nearest.Padding = new Thickness(7, 2);
        ToolTip.SetTip(_nearest, "Route to the nearest known match for the search text");
        _nearest.Click += async (_, _) => await FindNearestAsync().ConfigureAwait(true);
        Grid.SetColumn(_nearest, 1);
        searchRow.Children.Add(_nearest);

        _edit.Content = "Edit room";
        _edit.Padding = new Thickness(7, 2);
        _edit.Click += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_state.Room.Id)) await _editRoom(_state.Room.Id!).ConfigureAwait(true);
        };
        Grid.SetColumn(_edit, 2);
        searchRow.Children.Add(_edit);

        _addExitButton.Content = "Add exit";
        _addExitButton.Padding = new Thickness(7, 2);
        _addExitButton.Click += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_state.Room.Id)) await _addExit(_state.Room.Id!).ConfigureAwait(true);
        };
        Grid.SetColumn(_addExitButton, 3);
        searchRow.Children.Add(_addExitButton);

        Button reset = new() { Content = "Center", Padding = new Thickness(7, 2) };
        reset.Click += (_, _) => _viewport.ResetView();
        Grid.SetColumn(reset, 4);
        searchRow.Children.Add(reset);

        _more.Content = "More";
        _more.Padding = new Thickness(7, 2);
        _more.Click += (_, _) =>
        {
            int maximum = Math.Max(10, (_runtime.Settings.Mapper ?? new MapperPreferences()).VisualMapMaximumRooms);
            _visibleRoomBudget = Math.Min(maximum, _visibleRoomBudget + 48);
            QueueGraphLoad(force: true);
        };
        Grid.SetColumn(_more, 5);
        searchRow.Children.Add(_more);
        header.Children.Add(searchRow);

        Border resultFrame = new()
        {
            Background = UiTheme.Raised,
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(1),
            IsVisible = false,
            Child = new ScrollViewer
            {
                MaxHeight = 150,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = _resultList
            }
        };
        _resultFrame = resultFrame;
        header.Children.Add(resultFrame);

        Grid current = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        StackPanel identityPanel = new() { Spacing = 1 };
        _room.Foreground = UiTheme.Text;
        _room.FontSize = NexTypography.BodyStrong;
        _room.FontWeight = FontWeight.SemiBold;
        identityPanel.Children.Add(_room);
        _graphStatus.Foreground = UiTheme.Muted;
        _graphStatus.FontSize = NexTypography.Metadata;
        _graphStatus.Text = "Map idle";
        identityPanel.Children.Add(_graphStatus);
        current.Children.Add(identityPanel);
        _navigationStatus.Foreground = UiTheme.Muted;
        _navigationStatus.FontSize = NexTypography.Metadata;
        _navigationStatus.FontWeight = FontWeight.SemiBold;
        _navigationStatus.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_navigationStatus, 1);
        current.Children.Add(_navigationStatus);
        header.Children.Add(current);
        header.Children.Add(new TextBlock
        {
            Text = "Exit labels show direction · U/D are portal links, not global floors · □ closed · L locked · × blocked · ? unexplored",
            Foreground = UiTheme.Faint,
            FontSize = NexTypography.Metadata
        });
        root.Children.Add(header);

        Border mapFrame = new()
        {
            Background = UiTheme.Console,
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(10, 0, 10, 8),
            ClipToBounds = true,
            Child = _viewport
        };
        _viewport.RoomInvoked += roomId =>
        {
            if (_scene is null || !_scene.Nodes.TryGetValue(roomId, out MapperSceneNode? node) || node is null) return;
            HandleDestinationSelection(new MapperDestinationSearchResult(
                MapperDestinationKind.Room,
                node.Label,
                node.RoomId,
                node.Label,
                null,
                0,
                null,
                node.Avoid));
        };
        Grid.SetRow(mapFrame, 1);
        root.Children.Add(mapFrame);

        Border footer = new()
        {
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 8),
            Background = UiTheme.Surface
        };
        Grid footerGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        StackPanel routeInfo = new() { Spacing = 2 };
        _destination.Text = "No destination selected";
        _destination.Foreground = UiTheme.Text;
        _destination.FontSize = NexTypography.BodyStrong;
        _destination.FontWeight = FontWeight.SemiBold;
        routeInfo.Children.Add(_destination);
        _route.Text = "Search or click a room to plan a route.";
        _route.Foreground = UiTheme.Muted;
        _route.FontSize = NexTypography.Body;
        _route.TextWrapping = TextWrapping.Wrap;
        routeInfo.Children.Add(_route);
        _message.Foreground = UiTheme.Muted;
        _message.FontSize = NexTypography.Body;
        _message.TextWrapping = TextWrapping.Wrap;
        _message.IsVisible = false;
        routeInfo.Children.Add(_message);
        footerGrid.Children.Add(routeInfo);

        StackPanel controls = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        _go.Content = "Go";
        _go.Click += async (_, _) =>
        {
            if (_selectedDestination is not null)
                await _runtime.Navigator.StartAsync(_selectedDestination.RoomId, _selectedDestination.DisplayName, _applicationToken).ConfigureAwait(true);
        };
        controls.Children.Add(_go);
        _pauseResume.Content = "Pause";
        _pauseResume.Click += async (_, _) =>
        {
            if (_runtime.Navigator.Current.Status == AutoMoveStatus.Paused)
                await _runtime.Navigator.ResumeAsync(_applicationToken).ConfigureAwait(true);
            else
                await _runtime.Navigator.PauseAsync(cancellationToken: _applicationToken).ConfigureAwait(true);
        };
        controls.Children.Add(_pauseResume);
        _step.Content = "Step";
        _step.Click += async (_, _) =>
        {
            if (_selectedDestination is not null)
                await _runtime.Navigator.StepAsync(_selectedDestination.RoomId, _selectedDestination.DisplayName, _applicationToken).ConfigureAwait(true);
        };
        controls.Children.Add(_step);
        _stop.Content = "Stop";
        _stop.Click += async (_, _) => await _runtime.Navigator.StopAsync(cancellationToken: _applicationToken).ConfigureAwait(true);
        controls.Children.Add(_stop);
        Button settings = new() { Content = "Settings", Padding = new Thickness(7, 2) };
        settings.Click += async (_, _) => await _openSettings().ConfigureAwait(true);
        controls.Children.Add(settings);
        Grid.SetColumn(controls, 1);
        footerGrid.Children.Add(controls);
        footer.Child = footerGrid;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        return root;
    }

    private void QueueGraphLoad(bool force)
    {
        MapperPreferences mapperSettings = _runtime.Settings.Mapper ?? new MapperPreferences();
        if (!mapperSettings.Enabled)
        {
            _graphStatus.Text = "Mapper disabled";
            SetMessage("Enable the mapper in Settings to record and browse world topology.");
            return;
        }
        if (!_active || string.IsNullOrWhiteSpace(_state.Room.Id)) return;
        string origin = _state.Room.Id!;
        if (!force && _scene is not null && string.Equals(_loadedOrigin, origin, StringComparison.Ordinal)) return;
        if (_loadingGraph)
        {
            if (!force) return;
            _graphCts?.Cancel();
            _loadingGraph = false;
        }

        ResetCancellationTokenSource(ref _graphCts);
        _graphCts = CancellationTokenSource.CreateLinkedTokenSource(_applicationToken);
        CancellationToken token = _graphCts.Token;
        int generation = ++_graphGeneration;
        _loadingGraph = true;
        _graphStatus.Text = "Loading map…";
        SetMessage(string.Empty);
        _ = LoadGraphAsync(origin, generation, token);
    }

    private async Task LoadGraphAsync(string origin, int generation, CancellationToken token)
    {
        try
        {
            MapperPreferences settings = _runtime.Settings.Mapper ?? new MapperPreferences();
            int maximumRooms = Math.Min(Math.Max(10, settings.VisualMapMaximumRooms), _visibleRoomBudget);
            Task<(MapperGraphSnapshot? Graph, MapperScene? Scene)> worker = Task.Run(async () =>
            {
                MapperGraphSnapshot? graph = await _repository.LoadNeighborhoodAsync(origin, settings.VisualMapDepth, maximumRooms, token).ConfigureAwait(false);
                return (graph, graph is null ? null : MapperSceneBuilder.Build(graph));
            }, token);

            Task completed = await Task.WhenAny(worker, Task.Delay(TimeSpan.FromSeconds(5), token)).ConfigureAwait(false);
            if (completed != worker)
            {
                token.ThrowIfCancellationRequested();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation != _graphGeneration) return;
                    _loadingGraph = false;
                    _graphStatus.Text = "Map load timed out";
                    SetMessage("The mapper read exceeded 5 seconds. It was isolated from the UI and cancelled; the client remains usable.", true);
                });
                _graphCts?.Cancel();
                return;
            }

            (MapperGraphSnapshot? graph, MapperScene? scene) = await worker.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _graphGeneration || token.IsCancellationRequested) return;
                _loadingGraph = false;
                _graph = graph;
                _scene = scene;
                _loadedOrigin = origin;
                _graphStatus.Text = graph is null
                    ? "No observed topology yet"
                    : FormatGraphStatus(graph, scene);
                int configuredMaximum = Math.Max(10, settings.VisualMapMaximumRooms);
                _more.IsEnabled = graph is not null && graph.Rooms.Count >= maximumRooms && _visibleRoomBudget < configuredMaximum;
                _viewport.SetScene(scene, _state.Room.Id, _selectedDestination?.RoomId, _plannedRoute, recenter: true);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _graphGeneration) return;
                _loadingGraph = false;
                _graphStatus.Text = "Map unavailable";
                SetMessage(exception.Message, true);
            });
        }
    }

    private static string FormatGraphStatus(MapperGraphSnapshot graph, MapperScene? scene) =>
        $"{graph.Rooms.Count:N0} rooms · {graph.Edges.Count:N0} observed exits";

    private void QueueSearch()
    {
        ResetCancellationTokenSource(ref _searchCts);
        string query = _search.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            ReplaceSearchResults([]);
            return;
        }
        _searchCts = CancellationTokenSource.CreateLinkedTokenSource(_applicationToken);
        CancellationToken token = _searchCts.Token;
        _ = SearchAsync(query, token);
    }

    private async Task SearchAsync(string query, CancellationToken token)
    {
        try
        {
            await Task.Delay(160, token).ConfigureAwait(false);
            IReadOnlyList<MapperDestinationSearchResult> results = await Task.Run(
                async () => await _repository.SearchAsync(query, 24, token).ConfigureAwait(false),
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() => ReplaceSearchResults(results));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetMessage($"Search unavailable: {exception.Message}", true));
        }
    }

    private void ReplaceSearchResults(IReadOnlyList<MapperDestinationSearchResult> results)
    {
        _resultList.Children.Clear();
        foreach (MapperDestinationSearchResult result in results)
        {
            _resultList.Children.Add(BuildSearchResultButton(result));
        }
        if (_resultFrame is not null) _resultFrame.IsVisible = results.Count > 0;
    }

    private Control BuildSearchResultButton(MapperDestinationSearchResult entry)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 7,
            Margin = new Thickness(6, 4)
        };
        row.Children.Add(new TextBlock
        {
            Text = entry.Kind switch
            {
                MapperDestinationKind.Entity => "MOB",
                MapperDestinationKind.Object => "OBJ",
                MapperDestinationKind.Fixture => "FIX",
                MapperDestinationKind.ItemSource => "ITEM",
                _ => "ROOM"
            },
            Foreground = entry.Kind switch
            {
                MapperDestinationKind.Entity => UiTheme.Warning,
                MapperDestinationKind.Object => UiTheme.Text,
                MapperDestinationKind.Fixture => UiTheme.Muted,
                MapperDestinationKind.ItemSource => UiTheme.Success,
                _ => UiTheme.Accent
            },
            FontSize = NexTypography.Metadata,
            VerticalAlignment = VerticalAlignment.Center
        });
        StackPanel identity = new() { Spacing = 0 };
        identity.Children.Add(new TextBlock
        {
            Text = entry.DisplayName,
            Foreground = UiTheme.Text,
            FontSize = NexTypography.Body,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        identity.Children.Add(new TextBlock
        {
            Text = string.Join(" · ", new[] { entry.RoomName, entry.Area }.Where(value => !string.IsNullOrWhiteSpace(value))),
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Metadata,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(identity, 1);
        row.Children.Add(identity);
        TextBlock count = new()
        {
            Text = $"×{entry.ObservationCount:N0}",
            Foreground = UiTheme.Faint,
            FontSize = NexTypography.Metadata,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(count, 2);
        row.Children.Add(count);

        Button button = new()
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderBrush = UiTheme.Divider,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0),
            Content = row
        };
        button.Click += (_, _) => HandleDestinationSelection(entry);
        return button;
    }

    private void HandleDestinationSelection(MapperDestinationSearchResult destination)
    {
        _ = HandleDestinationSelectionAsync(destination);
    }

    private async Task HandleDestinationSelectionAsync(MapperDestinationSearchResult destination)
    {
        try
        {
            await SetDestinationAsync(destination).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetMessage($"Unable to select destination: {exception.Message}", true);
        }
    }

    private async Task FindNearestAsync()
    {
        string query = _search.Text?.Trim() ?? string.Empty;
        string? fromRoom = _state.Room.Id;
        if (query.Length == 0 || string.IsNullOrWhiteSpace(fromRoom))
        {
            SetMessage("Enter a room, MOB, or item to find the nearest known source.");
            return;
        }

        ResetCancellationTokenSource(ref _routeCts);
        _routeCts = CancellationTokenSource.CreateLinkedTokenSource(_applicationToken);
        CancellationToken token = _routeCts.Token;
        _route.Text = "Finding nearest reachable match…";
        try
        {
            MapperPreferences settings = _runtime.Settings.Mapper ?? new MapperPreferences();
            (MapperDestinationSearchResult Destination, KnowledgeRoute Route)? nearest = await Task.Run(
                async () => await _repository.FindNearestAsync(
                    fromRoom!, query, RoutePlanningOptions.From(settings), 30, token).ConfigureAwait(false), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (nearest is null)
                {
                    _plannedRoute = null;
                    _route.Text = "No reachable known match.";
                    return;
                }
                _selectedDestination = nearest.Value.Destination;
                _plannedRoute = nearest.Value.Route;
                _destination.Text = nearest.Value.Destination.DisplayName;
                UpdateRouteDisplay();
                _viewport.SetScene(_scene, _state.Room.Id, nearest.Value.Destination.RoomId, nearest.Value.Route, recenter: false);
                UpdateNavigation(_runtime.Navigator.Current);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetMessage($"Nearest search unavailable: {exception.Message}", true));
        }
    }

    private async Task PlanRouteAsync()
    {
        ResetCancellationTokenSource(ref _routeCts);
        _routeCts = CancellationTokenSource.CreateLinkedTokenSource(_applicationToken);
        CancellationToken token = _routeCts.Token;
        MapperDestinationSearchResult? destination = _selectedDestination;
        string? fromRoom = _state.Room.Id;
        if (destination is null || string.IsNullOrWhiteSpace(fromRoom))
        {
            _plannedRoute = null;
            UpdateRouteDisplay();
            return;
        }

        _route.Text = "Planning route…";
        try
        {
            MapperPreferences settings = _runtime.Settings.Mapper ?? new MapperPreferences();
            KnowledgeRoute? route = await Task.Run(
                async () => await _repository.FindRouteAsync(
                    fromRoom!, destination.RoomId, RoutePlanningOptions.From(settings), token).ConfigureAwait(false),
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _plannedRoute = route;
                UpdateRouteDisplay();
                _viewport.SetScene(_scene, _state.Room.Id, destination.RoomId, route, recenter: false);
                UpdateNavigation(_runtime.Navigator.Current);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _plannedRoute = null;
                _route.Text = "No route available.";
                SetMessage(exception.Message, true);
                UpdateNavigation(_runtime.Navigator.Current);
            });
        }
    }

    private void UpdateRouteDisplay()
    {
        _destination.Text = _selectedDestination?.DisplayName ?? "No destination selected";
        _route.Text = _plannedRoute is null
            ? _selectedDestination is null ? "Search or click a room to plan a route." : "No known route."
            : _plannedRoute.Steps.Count == 0
                ? "You are already here."
                : FormatRouteSummary(_plannedRoute);
    }

    private static string FormatRouteSummary(KnowledgeRoute route)
    {
        int closedDoors = route.Steps.Count(step => step.DoorState == ExitDoorState.Closed);
        int unknownExits = route.Steps.Count(step => step.Traversability == ExitTraversability.Unknown);
        List<string> details = [$"{route.Steps.Count:N0} steps", route.Speedwalk];
        if (closedDoors > 0) details.Add($"{closedDoors:N0} closed door{(closedDoors == 1 ? string.Empty : "s")}");
        if (unknownExits > 0) details.Add($"{unknownExits:N0} unconfirmed exit{(unknownExits == 1 ? string.Empty : "s")}");
        return string.Join(" · ", details);
    }

    private static void ResetCancellationTokenSource(ref CancellationTokenSource? source)
    {
        CancellationTokenSource? previous = source;
        source = null;
        if (previous is null) return;
        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            previous.Dispose();
        }
    }

    private void SetMessage(string text, bool error = false)
    {
        _message.Text = text;
        _message.Foreground = error ? UiTheme.Danger : UiTheme.Muted;
        _message.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private static string FormatNavigation(AutoMoveStateChanged state) => AutoMoveDisplay.Format(state);

    public void Dispose()
    {
        _active = false;
        _runtime.Knowledge.MapperKnowledgeChanged -= HandleMapperKnowledgeChanged;
        ResetCancellationTokenSource(ref _graphCts);
        ResetCancellationTokenSource(ref _searchCts);
        ResetCancellationTokenSource(ref _routeCts);
    }
}
