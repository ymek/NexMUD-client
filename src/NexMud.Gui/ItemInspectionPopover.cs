using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NexMud.Client.Knowledge;
using NexMud.Contracts.State;

namespace NexMud.Gui;

internal sealed record ItemInspectionData(
    string ItemName,
    string? Slot,
    ItemIdentification? LiveIdentification,
    ItemKnowledge? CodexKnowledge);

/// <summary>
/// Shared item-inspection presentation used by the gameplay rail and Character/Inventory workspace.
/// It turns canonical identify/Codex data into a player-facing item hierarchy without leaking parser
/// field formatting into the UI.
/// </summary>
internal static class ItemInspectionPopover
{
    private static readonly IReadOnlyDictionary<string, string> PlayerFacingLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hp"] = "Hit Points",
            ["hit points"] = "Hit Points",
            ["mana"] = "Mana",
            ["moves"] = "Movement",
            ["move"] = "Movement",
            ["movement"] = "Movement",
            ["ac"] = "Armor Class",
            ["armor class"] = "Armor Class",
            ["hit roll"] = "Hit Roll",
            ["hitroll"] = "Hit Roll",
            ["dam roll"] = "Damage Roll",
            ["damroll"] = "Damage Roll",
            ["damage roll"] = "Damage Roll",
            ["save"] = "Saving Throw",
            ["saves"] = "Saving Throw",
            ["saving throw"] = "Saving Throw",
            ["strength"] = "Strength",
            ["dexterity"] = "Dexterity",
            ["constitution"] = "Constitution",
            ["intelligence"] = "Intelligence",
            ["wisdom"] = "Wisdom",
            ["charisma"] = "Charisma"
        };

    private static readonly HashSet<string> LowerIsBeneficial = new(StringComparer.OrdinalIgnoreCase)
    {
        "ac", "armor class", "save", "saves", "saving throw"
    };

    private static readonly HashSet<string> HigherIsBeneficial = new(StringComparer.OrdinalIgnoreCase)
    {
        "hp", "hit points", "mana", "moves", "move", "movement",
        "hit roll", "hitroll", "dam roll", "damroll", "damage roll",
        "strength", "dexterity", "constitution", "intelligence", "wisdom", "charisma"
    };

    public static Control Build(ItemInspectionData data, bool compact = true)
    {
        ItemIdentification? live = data.LiveIdentification;
        ItemKnowledge? persisted = data.CodexKnowledge;

        StackPanel content = new()
        {
            Spacing = compact ? 6 : 9,
            Margin = new Thickness(compact ? 2 : 0)
        };
        if (compact)
        {
            content.Width = 300;
        }

        string displayName = DisplayItemName(data.ItemName);
        content.Children.Add(new TextBlock
        {
            Text = displayName,
            Foreground = NexMudTheme.Parchment,
            FontFamily = NexMudTheme.Display,
            FontSize = compact ? NexTypography.SectionTitle : NexTypography.SectionTitle,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        });

        if (live is null && persisted is null)
        {
            string context = BuildIdentityLine(data.Slot, null, null, Array.Empty<string>());
            if (context.Length > 0)
            {
                content.Children.Add(IdentityLine(context));
            }
            content.Children.Add(new TextBlock
            {
                Text = "Observed this session",
                Foreground = NexMudTheme.Muted,
                FontSize = NexTypography.Metadata
            });
            content.Children.Add(new TextBlock
            {
                Text = "No identify or Codex details observed yet.",
                Foreground = NexMudTheme.Faint,
                FontSize = NexTypography.Metadata,
                TextWrapping = TextWrapping.Wrap
            });
            return compact ? Wrap(content) : content;
        }

        string? itemType = live?.ItemType ?? persisted?.ItemType;
        string? weaponType = live?.WeaponType ?? persisted?.WeaponType;
        string? material = live?.Material ?? persisted?.Material;
        int? level = live?.Level ?? persisted?.Level;
        decimal? weight = live?.Weight ?? persisted?.Weight;
        string? damageDice = live?.DamageDice ?? persisted?.DamageDice;
        decimal? damageAverage = live?.DamageAverage ?? persisted?.DamageAverage;
        string? damageType = live?.DamageType ?? persisted?.DamageType;
        IReadOnlyList<string> wear = live?.WearLocations ?? persisted?.WearLocations ?? Array.Empty<string>();
        IReadOnlyList<string> flags = live?.Flags ?? persisted?.Flags ?? Array.Empty<string>();
        IReadOnlyList<string> weaponFlags = live?.WeaponFlags ?? persisted?.WeaponFlags ?? Array.Empty<string>();
        IReadOnlyDictionary<string, string> extra = live?.ExtraFields
            ?? persisted?.ExtraFields
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string identity = BuildIdentityLine(data.Slot, itemType, weaponType, wear);
        if (identity.Length > 0)
        {
            content.Children.Add(IdentityLine(identity));
        }

        string metadata = BuildMetadataLine(material, level, weight);
        if (metadata.Length > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = metadata,
                Foreground = NexMudTheme.Muted,
                FontSize = NexTypography.Metadata,
                TextWrapping = TextWrapping.Wrap
            });
        }

        IReadOnlyList<(string Label, string Value)> primaryStats = BuildPrimaryStats(
            damageDice,
            damageAverage,
            damageType,
            extra);
        if (primaryStats.Count > 0)
        {
            content.Children.Add(BuildPrimaryStatsGrid(primaryStats));
        }

        (string Key, string Label, string Value)[] modifiers = extra
            .Where(pair => pair.Key.StartsWith("affect ", StringComparison.OrdinalIgnoreCase))
            .Select(pair =>
            {
                string key = pair.Key[7..].Trim();
                return (Key: key, Label: PlayerFacingLabel(key), Value: FormatModifier(pair.Value));
            })
            .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (modifiers.Length > 0)
        {
            content.Children.Add(SectionHeading("MODIFIERS"));
            int modifierLimit = compact ? Math.Min(5, modifiers.Length) : modifiers.Length;
            for (int index = 0; index < modifierLimit; index++)
            {
                (string key, string label, string value) = modifiers[index];
                content.Children.Add(BuildModifierRow(key, label, value));
            }
            if (compact && modifiers.Length > modifierLimit)
            {
                content.Children.Add(new TextBlock
                {
                    Text = $"+{modifiers.Length - modifierLimit} more",
                    Foreground = NexMudTheme.Faint,
                    FontSize = NexTypography.Metadata
                });
            }
        }

        string[] allFlags = flags
            .Concat(weaponFlags)
            .Where(flag => !string.IsNullOrWhiteSpace(flag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (allFlags.Length > 0)
        {
            content.Children.Add(SectionHeading("FLAGS"));
            content.Children.Add(BuildFlagChips(allFlags));
        }

        string observation = live is not null
            ? "Observed this session • Identified"
            : persisted is not null
                ? $"Codex • observed {persisted.ObservationCount:N0}×"
                : "Observed";
        content.Children.Add(new TextBlock
        {
            Text = observation,
            Foreground = NexMudTheme.Faint,
            FontSize = NexTypography.Metadata,
            Margin = new Thickness(0, compact ? 1 : 3, 0, 0)
        });

        return compact ? Wrap(content) : content;
    }

    private static Control IdentityLine(string text) => new TextBlock
    {
        Text = text,
        Foreground = NexMudTheme.AccentBright,
        FontSize = NexTypography.Body,
        FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap
    };

    private static string BuildIdentityLine(
        string? selectedSlot,
        string? itemType,
        string? weaponType,
        IReadOnlyList<string> wear)
    {
        List<string> parts = [];
        string? slot = !string.IsNullOrWhiteSpace(selectedSlot)
            ? selectedSlot
            : wear.Count > 0
                ? WearLabel(wear[0])
                : null;
        if (!string.IsNullOrWhiteSpace(slot))
        {
            parts.Add(DisplayPhrase(slot!));
        }
        if (!string.IsNullOrWhiteSpace(itemType))
        {
            parts.Add(DisplayPhrase(itemType!));
        }
        if (!string.IsNullOrWhiteSpace(weaponType) &&
            !string.Equals(weaponType, itemType, StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(DisplayPhrase(weaponType!));
        }
        return string.Join(" • ", parts);
    }

    private static string BuildMetadataLine(string? material, int? level, decimal? weight)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(material)) parts.Add(DisplayPhrase(material!));
        if (level is not null) parts.Add($"Level {level}");
        if (weight is not null) parts.Add($"Weight {weight.Value:0.##}");
        return string.Join(" • ", parts);
    }

    private static IReadOnlyList<(string Label, string Value)> BuildPrimaryStats(
        string? damageDice,
        decimal? damageAverage,
        string? damageType,
        IReadOnlyDictionary<string, string> extra)
    {
        List<(string Label, string Value)> stats = [];
        if (!string.IsNullOrWhiteSpace(damageDice) || damageAverage is not null || !string.IsNullOrWhiteSpace(damageType))
        {
            List<string> damage = [];
            if (!string.IsNullOrWhiteSpace(damageDice)) damage.Add(damageDice!);
            if (damageAverage is not null) damage.Add($"avg {damageAverage:0.##}");
            if (!string.IsNullOrWhiteSpace(damageType)) damage.Add(DisplayPhrase(damageType!));
            stats.Add(("Damage", string.Join(" • ", damage)));
        }

        AddPrimaryStat(extra, stats, ["ac", "armor class"], "Armor Class");
        AddPrimaryStat(extra, stats, ["value"], "Value");
        AddPrimaryStat(extra, stats, ["charges"], "Charges");
        AddPrimaryStat(extra, stats, ["capacity"], "Capacity");
        return stats;
    }

    private static void AddPrimaryStat(
        IReadOnlyDictionary<string, string> extra,
        ICollection<(string Label, string Value)> stats,
        IReadOnlyList<string> keys,
        string label)
    {
        foreach (string key in keys)
        {
            if (!extra.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }
            stats.Add((label, value.Trim()));
            return;
        }
    }

    private static Control BuildPrimaryStatsGrid(IReadOnlyList<(string Label, string Value)> stats)
    {
        WrapPanel grid = new() { Orientation = Orientation.Horizontal };
        foreach ((string label, string value) in stats)
        {
            StackPanel text = new() { Spacing = 1 };
            text.Children.Add(new TextBlock
            {
                Text = label.ToUpperInvariant(),
                Foreground = NexMudTheme.Faint,
                FontSize = NexTypography.Metadata,
                FontWeight = FontWeight.Bold
            });
            text.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = NexMudTheme.Parchment,
                FontSize = NexTypography.BodyStrong,
                FontWeight = FontWeight.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            grid.Children.Add(new Border
            {
                Background = NexMudTheme.RaisedSurfaceGradient,
                BorderBrush = NexMudTheme.Divider,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 0, 6, 5),
                Padding = new Thickness(8, 6),
                Width = 126,
                MinHeight = 52,
                Child = text
            });
        }
        return grid;
    }

    private static Control BuildModifierRow(string key, string label, string value)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
            MinHeight = 24
        };
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = NexMudTheme.Parchment,
            FontSize = NexTypography.Body,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        TextBlock amount = new()
        {
            Text = value,
            Foreground = ModifierBrush(key, value),
            FontSize = NexTypography.BodyStrong,
            FontWeight = FontWeight.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(amount, 1);
        row.Children.Add(amount);
        return row;
    }

    private static Control BuildFlagChips(IReadOnlyList<string> flags)
    {
        WrapPanel chips = new() { Orientation = Orientation.Horizontal };
        foreach (string flag in flags)
        {
            chips.Children.Add(new Border
            {
                Background = NexMudTheme.RaisedSurfaceGradient,
                BorderBrush = NexMudTheme.Divider,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 5, 5),
                Padding = new Thickness(7, 2),
                Child = new TextBlock
                {
                    Text = flag,
                    Foreground = NexMudTheme.Muted,
                    FontFamily = NexMudTheme.Mono,
                    FontSize = NexTypography.Metadata
                }
            });
        }
        return chips;
    }

    private static TextBlock SectionHeading(string text) => new()
    {
        Text = text,
        Foreground = NexMudTheme.AccentBright,
        FontSize = NexTypography.Metadata,
        FontWeight = FontWeight.Bold,
        Margin = new Thickness(0, 2, 0, 0)
    };

    private static Border Wrap(Control content) => new()
    {
        Background = NexMudTheme.PrimarySurfaceGradient,
        BorderBrush = NexMudTheme.AntiqueBrass,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(10),
        Child = content
    };

    private static IBrush ModifierBrush(string key, string formattedValue)
    {
        if (!decimal.TryParse(
                formattedValue.TrimStart('+'),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal value) || value == 0)
        {
            return NexMudTheme.Parchment;
        }

        if (LowerIsBeneficial.Contains(key))
        {
            return value < 0 ? NexMudTheme.Success : NexMudTheme.Danger;
        }
        if (HigherIsBeneficial.Contains(key))
        {
            return value > 0 ? NexMudTheme.Success : NexMudTheme.Danger;
        }
        return NexMudTheme.Parchment;
    }

    private static string FormatModifier(string value)
    {
        if (decimal.TryParse(
                value,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out decimal number))
        {
            return number > 0
                ? $"+{number:0.##}"
                : number.ToString("0.##", CultureInfo.InvariantCulture);
        }
        return value;
    }

    private static string PlayerFacingLabel(string key)
    {
        string normalized = key.Trim().Replace('_', ' ');
        return PlayerFacingLabels.TryGetValue(normalized, out string? label)
            ? label
            : DisplayPhrase(normalized);
    }

    private static string WearLabel(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "wield" or "wielded" => "Wielded",
            "dual wield" or "dual wielded" => "Off Hand",
            "float" or "floating" or "floating nearby" => "Floating",
            "head" or "worn on head" => "Head",
            "neck" or "worn around neck" => "Neck",
            "body" or "worn about body" => "About Body",
            "torso" or "worn on torso" => "Torso",
            "hands" or "worn on hands" => "Hands",
            "arms" or "worn on arms" => "Arms",
            "legs" or "worn on legs" => "Legs",
            "feet" or "worn on feet" => "Feet",
            "waist" or "worn about waist" => "Waist",
            "shield" or "worn as shield" => "Shield",
            _ => DisplayPhrase(value)
        };
    }

    private static string DisplayItemName(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0) return trimmed;
        if (trimmed.Any(char.IsUpper)) return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];

        string[] words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < words.Length; index++)
        {
            bool minorWord = index > 0 && words[index] is "a" or "an" or "and" or "of" or "the" or "to" or "in" or "on";
            if (!minorWord)
            {
                words[index] = char.ToUpperInvariant(words[index][0]) + words[index][1..];
            }
        }
        return string.Join(' ', words);
    }

    private static string DisplayPhrase(string value)
    {
        string normalized = value.Trim().Replace('_', ' ');
        if (normalized.Length == 0) return normalized;
        return char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }
}
