using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using NexMud.Client.Runtime;
using NexMud.Client.Settings;
using NexMud.Contracts.Events;

namespace NexMud.Gui;

/// <summary>First-class profile manager. Server and protocol policy are never global UI settings.</summary>
internal sealed class ConnectionProfilesEditor : UserControl
{
    private sealed class ProfileDraft
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public required string Host { get; set; }
        public required int Port { get; set; }
        public required bool UseTls { get; set; }
        public required string TerminalType { get; set; }
        public required ConnectionProtocolPreferences Protocols { get; set; }
    }

    private readonly NexMudRuntime _runtime;
    private readonly ObservableCollection<ProfileDraft> _profiles = [];
    private readonly ListBox _list = new();
    private readonly TextBox _name = Field();
    private readonly TextBox _host = Field();
    private readonly TextBox _port = Field();
    private readonly CheckBox _tls = new();
    private readonly TextBox _terminal = Field();
    private readonly Dictionary<string, ComboBox> _protocols = [];
    private readonly TextBlock _status = new() { Foreground = UiTheme.Muted, FontSize = NexTypography.Metadata };
    private string _activeProfileId;
    private ProfileDraft? _editingProfile;
    private bool _loading;

    public ConnectionProfilesEditor(NexMudRuntime runtime)
    {
        _runtime = runtime;
        _activeProfileId = runtime.ActiveConnectionProfile.Id;
        foreach (ConnectionProfile profile in runtime.ConnectionProfiles)
        {
            _profiles.Add(ToDraft(profile));
        }
        Content = Build();
        _list.SelectedItem = _profiles.FirstOrDefault(profile => profile.Id == _activeProfileId) ?? _profiles.FirstOrDefault();
        LoadSelection();
    }

    public string ActiveProfileId => _activeProfileId;

    public bool TryReadProfiles(out IReadOnlyList<ConnectionProfile> profiles, out string? validation)
    {
        SaveSelection();
        validation = null;
        if (_profiles.Count == 0)
        {
            validation = "At least one connection profile is required.";
            profiles = [];
            return false;
        }

        List<ConnectionProfile> result = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProfileDraft draft in _profiles)
        {
            string name = draft.Name.Trim();
            string host = draft.Host.Trim();
            string terminal = draft.TerminalType.Trim();
            if (name.Length == 0 || host.Length == 0 || terminal.Length == 0)
            {
                validation = "Each connection profile needs a name, host, and terminal type.";
                profiles = [];
                return false;
            }
            if (!names.Add(name))
            {
                validation = $"Connection profile name '{name}' is duplicated.";
                profiles = [];
                return false;
            }
            if (draft.Port is < 1 or > 65535)
            {
                validation = $"Connection profile '{name}' has an invalid port.";
                profiles = [];
                return false;
            }
            result.Add(new ConnectionProfile(
                draft.Id,
                name,
                host,
                draft.Port,
                draft.UseTls,
                terminal,
                draft.Protocols));
        }

        if (result.All(profile => profile.Id != _activeProfileId)) _activeProfileId = result[0].Id;
        profiles = result;
        return true;
    }

    private Control Build()
    {
        Grid root = new() { ColumnDefinitions = new ColumnDefinitions("260,*"), ColumnSpacing = 14 };

        Grid library = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
        library.Children.Add(new TextBlock
        {
            Text = "PROFILE LIBRARY",
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.Bold
        });
        _list.ItemsSource = _profiles;
        _list.Background = UiTheme.Console;
        _list.BorderBrush = UiTheme.Divider;
        _list.BorderThickness = new Thickness(1);
        _list.ItemTemplate = new FuncDataTemplate<ProfileDraft>((profile, _) => BuildProfileRow(profile), true);
        _list.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            SaveSelection();
            LoadSelection();
        };
        Grid.SetRow(_list, 1);
        library.Children.Add(_list);

        StackPanel libraryActions = new() { Orientation = Orientation.Horizontal, Spacing = 5 };
        Button add = QuietButton("Add");
        add.Click += (_, _) => AddProfile();
        Button duplicate = QuietButton("Duplicate");
        duplicate.Click += (_, _) => DuplicateProfile();
        Button delete = QuietButton("Delete");
        delete.Click += (_, _) => DeleteProfile();
        libraryActions.Children.Add(add);
        libraryActions.Children.Add(duplicate);
        libraryActions.Children.Add(delete);
        Grid.SetRow(libraryActions, 2);
        library.Children.Add(libraryActions);
        root.Children.Add(library);

        ScrollViewer detailScroll = new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = BuildDetail()
        };
        Grid.SetColumn(detailScroll, 1);
        root.Children.Add(detailScroll);
        return root;
    }

    private Control BuildDetail()
    {
        StackPanel detail = new() { Spacing = 10 };
        detail.Children.Add(SectionTitle("Connection Profile", "Server/account-facing configuration and protocol policy are owned by this profile."));

        _name.PlaceholderText = "Profile name";
        _host.PlaceholderText = "mud.example.net";
        _port.Width = 100;
        _terminal.PlaceholderText = "xterm-256color";
        _tls.Content = "Use TLS";
        _tls.Foreground = UiTheme.Text;
        detail.Children.Add(FormRow("Name", _name));
        detail.Children.Add(FormRow("Host", _host));
        detail.Children.Add(FormRow("Port", _port));
        detail.Children.Add(_tls);
        detail.Children.Add(FormRow("Terminal type", _terminal));

        detail.Children.Add(SectionTitle("Advanced Protocols", "Auto is normal. Override only for servers with specific compatibility requirements."));
        detail.Children.Add(new TextBlock
        {
            Text = "Structured game data",
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.Bold
        });
        detail.Children.Add(ProtocolRow("GMCP", "Gmcp"));
        detail.Children.Add(ProtocolRow("MSDP", "Msdp"));
        detail.Children.Add(ProtocolRow("MSSP", "Mssp"));
        detail.Children.Add(new TextBlock
        {
            Text = "Telnet / transport",
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 5, 0, 0)
        });
        detail.Children.Add(ProtocolRow("MCCP2", "Mccp2"));
        detail.Children.Add(ProtocolRow("NAWS", "Naws"));
        detail.Children.Add(ProtocolRow("CHARSET", "Charset"));
        detail.Children.Add(ProtocolRow("NEW-ENVIRON", "NewEnvironment"));
        detail.Children.Add(ProtocolRow("MTTS", "Mtts"));
        detail.Children.Add(ProtocolRow("EOR", "Eor"));

        StackPanel actions = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        Button active = QuietButton("Set active");
        active.Click += (_, _) => SetActive();
        Button test = QuietButton("Test");
        test.Click += (_, _) => TestSelected();
        Button connect = UiTheme.PrimaryButton("Connect");
        connect.Click += async (_, _) => await ConnectSelectedAsync().ConfigureAwait(true);
        actions.Children.Add(active);
        actions.Children.Add(test);
        actions.Children.Add(connect);
        detail.Children.Add(actions);
        detail.Children.Add(_status);
        return detail;
    }

    private Control BuildProfileRow(ProfileDraft? profile)
    {
        if (profile is null) return new TextBlock();
        bool active = profile.Id == _activeProfileId;
        StackPanel row = new() { Spacing = 2, Margin = new Thickness(7, 5) };
        row.Children.Add(new TextBlock
        {
            Text = active ? $"{profile.Name}  ● Active" : profile.Name,
            Foreground = active ? UiTheme.Accent : UiTheme.Text,
            FontSize = NexTypography.Body,
            FontWeight = FontWeight.SemiBold
        });
        row.Children.Add(new TextBlock
        {
            Text = $"{profile.Host}:{profile.Port} · {profile.TerminalType} · Protocols: {ProtocolSummary(profile.Protocols)}",
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        return row;
    }

    private Control ProtocolRow(string label, string key)
    {
        ComboBox combo = new()
        {
            ItemsSource = Enum.GetValues<ProtocolPolicy>(),
            SelectedItem = ProtocolPolicy.Auto,
            MinWidth = 110
        };
        _protocols[key] = combo;
        return FormRow(label, combo);
    }

    private void SaveSelection()
    {
        if (_loading || _editingProfile is not ProfileDraft draft) return;
        draft.Name = (_name.Text ?? string.Empty).Trim();
        draft.Host = (_host.Text ?? string.Empty).Trim();
        if (int.TryParse(_port.Text, out int port)) draft.Port = port;
        draft.UseTls = _tls.IsChecked == true;
        draft.TerminalType = (_terminal.Text ?? string.Empty).Trim();
        draft.Protocols = ReadProtocols();
    }

    private void LoadSelection()
    {
        _loading = true;
        try
        {
            if (_list.SelectedItem is not ProfileDraft draft)
            {
                _editingProfile = null;
                _name.Text = _host.Text = _port.Text = _terminal.Text = string.Empty;
                return;
            }
            _editingProfile = draft;
            _name.Text = draft.Name;
            _host.Text = draft.Host;
            _port.Text = draft.Port.ToString();
            _tls.IsChecked = draft.UseTls;
            _terminal.Text = draft.TerminalType;
            WriteProtocols(draft.Protocols);
            _status.Text = draft.Id == _activeProfileId ? "Active connection profile." : string.Empty;
        }
        finally
        {
            _loading = false;
        }
    }

    private ConnectionProtocolPreferences ReadProtocols() => new(
        Naws: SelectedProtocol("Naws"),
        Gmcp: SelectedProtocol("Gmcp"),
        Msdp: SelectedProtocol("Msdp"),
        Mssp: SelectedProtocol("Mssp"),
        Mccp2: SelectedProtocol("Mccp2"),
        Charset: SelectedProtocol("Charset"),
        NewEnvironment: SelectedProtocol("NewEnvironment"),
        Mtts: SelectedProtocol("Mtts"),
        Eor: SelectedProtocol("Eor"));

    private void WriteProtocols(ConnectionProtocolPreferences protocols)
    {
        _protocols["Naws"].SelectedItem = protocols.Naws;
        _protocols["Gmcp"].SelectedItem = protocols.Gmcp;
        _protocols["Msdp"].SelectedItem = protocols.Msdp;
        _protocols["Mssp"].SelectedItem = protocols.Mssp;
        _protocols["Mccp2"].SelectedItem = protocols.Mccp2;
        _protocols["Charset"].SelectedItem = protocols.Charset;
        _protocols["NewEnvironment"].SelectedItem = protocols.NewEnvironment;
        _protocols["Mtts"].SelectedItem = protocols.Mtts;
        _protocols["Eor"].SelectedItem = protocols.Eor;
    }

    private ProtocolPolicy SelectedProtocol(string key) =>
        _protocols[key].SelectedItem is ProtocolPolicy value ? value : ProtocolPolicy.Auto;

    private void AddProfile()
    {
        SaveSelection();
        ProfileDraft draft = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "New profile",
            Host = "",
            Port = 4000,
            UseTls = false,
            TerminalType = "xterm-256color",
            Protocols = new ConnectionProtocolPreferences()
        };
        _profiles.Add(draft);
        _list.SelectedItem = draft;
    }

    private void DuplicateProfile()
    {
        SaveSelection();
        if (_list.SelectedItem is not ProfileDraft selected) return;
        ProfileDraft draft = new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = selected.Name + " Copy",
            Host = selected.Host,
            Port = selected.Port,
            UseTls = selected.UseTls,
            TerminalType = selected.TerminalType,
            Protocols = selected.Protocols
        };
        _profiles.Add(draft);
        _list.SelectedItem = draft;
    }

    private void DeleteProfile()
    {
        if (_profiles.Count <= 1)
        {
            _status.Text = "At least one connection profile is required.";
            return;
        }
        if (_list.SelectedItem is not ProfileDraft selected) return;
        int index = _profiles.IndexOf(selected);
        _profiles.Remove(selected);
        if (ReferenceEquals(_editingProfile, selected)) _editingProfile = null;
        if (_activeProfileId == selected.Id) _activeProfileId = _profiles[0].Id;
        _list.SelectedIndex = Math.Clamp(index, 0, _profiles.Count - 1);
        RefreshList();
    }

    private void SetActive()
    {
        SaveSelection();
        if (_list.SelectedItem is not ProfileDraft selected) return;
        _activeProfileId = selected.Id;
        _status.Text = $"{selected.Name} will be used by Connect.";
        RefreshList();
        _list.SelectedItem = selected;
    }

    private void TestSelected()
    {
        SaveSelection();
        if (!TryReadProfiles(out IReadOnlyList<ConnectionProfile> profiles, out string? validation))
        {
            _status.Text = validation;
            return;
        }
        ProfileDraft? selectedDraft = _list.SelectedItem as ProfileDraft;
        ConnectionProfile? selected = profiles.FirstOrDefault(profile => profile.Id == selectedDraft?.Id);
        if (selected is null) return;
        try
        {
            _runtime.CreateConnectionOptions(selected).Validate();
            _status.Text = $"{selected.Name}: profile configuration is valid.";
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            _status.Text = exception.Message;
        }
    }

    private async Task ConnectSelectedAsync()
    {
        SaveSelection();
        if (!TryReadProfiles(out IReadOnlyList<ConnectionProfile> profiles, out string? validation))
        {
            _status.Text = validation;
            return;
        }
        if (_list.SelectedItem is not ProfileDraft selectedDraft) return;
        ConnectionProfile? selected = profiles.FirstOrDefault(profile => profile.Id == selectedDraft.Id);
        if (selected is null) return;
        if (_runtime.State.Current.Session.ConnectionStatus != ConnectionStatus.Disconnected)
        {
            _status.Text = "Disconnect the current session before connecting another profile.";
            return;
        }

        try
        {
            _activeProfileId = selected.Id;
            await _runtime.SaveConnectionProfilesAsync(profiles, selected.Id, _runtime.CancellationToken).ConfigureAwait(true);
            await _runtime.Transport.ConnectAsync(_runtime.CreateConnectionOptions(selected), _runtime.CancellationToken).ConfigureAwait(true);
            _status.Text = $"Connected with {selected.Name}.";
            RefreshList();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _status.Text = $"Connect failed: {exception.Message}";
        }
    }

    private void RefreshList()
    {
        ProfileDraft? selected = _list.SelectedItem as ProfileDraft;
        _loading = true;
        try
        {
            _list.ItemsSource = null;
            _list.ItemsSource = _profiles;
            _list.SelectedItem = selected ?? _profiles.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }
        LoadSelection();
    }

    private static ProfileDraft ToDraft(ConnectionProfile profile) => new()
    {
        Id = profile.Id,
        Name = profile.Name,
        Host = profile.Host,
        Port = profile.Port,
        UseTls = profile.UseTls,
        TerminalType = profile.TerminalType,
        Protocols = profile.EffectiveProtocols
    };

    private static string ProtocolSummary(ConnectionProtocolPreferences protocols)
    {
        ProtocolPolicy[] values =
        [
            protocols.Naws, protocols.Gmcp, protocols.Msdp, protocols.Mssp, protocols.Mccp2,
            protocols.Charset, protocols.NewEnvironment, protocols.Mtts, protocols.Eor
        ];
        return values.All(value => value == ProtocolPolicy.Auto) ? "Auto" : "Overrides";
    }

    private static Control SectionTitle(string title, string subtitle)
    {
        StackPanel stack = new() { Spacing = 3 };
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = UiTheme.Text,
            FontSize = NexTypography.SectionTitle,
            FontWeight = FontWeight.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = UiTheme.Muted,
            FontSize = NexTypography.Metadata,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(UiTheme.DividerLine());
        return stack;
    }

    private static Control FormRow(string label, Control control)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("135,*"), ColumnSpacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = UiTheme.Text,
            FontSize = NexTypography.Body,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    private static TextBox Field() => UiTheme.FieldBox();
    private static Button QuietButton(string text) => UiTheme.QuietButton(text);
}
