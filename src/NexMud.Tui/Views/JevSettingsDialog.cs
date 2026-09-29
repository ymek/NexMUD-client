using NexMud.Contracts.Jev;
using NexMud.Core.Jev;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace NexMud.Tui.Views;

public sealed class JevSettingsDialog : Runnable<JevAuthoritySnapshot?>
{
    private readonly Dictionary<JevDomain, Button> _domainButtons = [];
    private readonly Dictionary<JevDomain, JevAuthority> _draft;
    private JevPreset _preset;

    public JevSettingsDialog(JevAuthoritySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);

        Title = "Jev authority settings";
        Width = 72;
        Height = 18;
        SetScheme(UiTheme.Panel);

        _preset = current.Preset;
        _draft = current.Domains.ToDictionary(pair => pair.Key, pair => pair.Value);

        Label presetLabel = new()
        {
            Text = "Presets:",
            X = 1,
            Y = 1,
            Width = 9,
            CanFocus = false
        };
        Add(presetLabel);

        int presetX = 11;
        foreach (JevPreset preset in new[] { JevPreset.Off, JevPreset.Copilot, JevPreset.Bot, JevPreset.Autonomous })
        {
            Button button = new()
            {
                Text = preset.ToString(),
                X = presetX,
                Y = 1,
                Width = 13
            };
            button.Accepted += (_, _) => ApplyPreset(preset);
            Add(button);
            presetX += 14;
        }

        Label hint = new()
        {
            Text = "Select a preset, then cycle individual domains as needed.",
            X = 1,
            Y = 3,
            Width = Dim.Fill(1),
            CanFocus = false
        };
        Add(hint);

        int row = 5;
        foreach (JevDomain domain in Enum.GetValues<JevDomain>())
        {
            Label label = new()
            {
                Text = domain.ToString(),
                X = 2,
                Y = row,
                Width = 16,
                CanFocus = false
            };

            Button authorityButton = new()
            {
                Text = _draft[domain].ToString(),
                X = 20,
                Y = row,
                Width = 14
            };
            authorityButton.Accepted += (_, _) => CycleAuthority(domain);
            _domainButtons[domain] = authorityButton;

            Add(label, authorityButton);
            row++;
        }

        Button cancel = new()
        {
            Text = "Cancel",
            X = Pos.AnchorEnd(22),
            Y = Pos.AnchorEnd(2),
            Width = 10
        };
        cancel.Accepted += (_, _) => App!.RequestStop();

        Button save = new()
        {
            Text = "Save",
            X = Pos.AnchorEnd(11),
            Y = Pos.AnchorEnd(2),
            Width = 10
        };
        save.Accepted += (_, _) =>
        {
            Result = JevAuthoritySnapshot.Create(_preset, _draft);
            App!.RequestStop();
        };

        Add(cancel, save);
    }

    protected override bool OnKeyDown(Key key)
    {
        if (key == Key.Esc)
        {
            App?.RequestStop();
            return true;
        }

        return base.OnKeyDown(key);
    }

    private void ApplyPreset(JevPreset preset)
    {
        JevAuthoritySnapshot template = JevAuthorityService.CreatePreset(preset);
        _preset = preset;
        foreach (JevDomain domain in Enum.GetValues<JevDomain>())
        {
            _draft[domain] = template.Domains[domain];
            _domainButtons[domain].Text = _draft[domain].ToString();
        }
    }

    private void CycleAuthority(JevDomain domain)
    {
        JevAuthority[] values = Enum.GetValues<JevAuthority>();
        int index = Array.IndexOf(values, _draft[domain]);
        JevAuthority next = values[(index + 1) % values.Length];
        _draft[domain] = next;
        _preset = JevPreset.Custom;
        _domainButtons[domain].Text = next.ToString();
    }
}
