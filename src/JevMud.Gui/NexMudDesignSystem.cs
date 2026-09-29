using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace JevMud.Gui;

internal enum NexAccentTheme
{
    EmberBrass,
    ArcaneCyan,
    MysticViolet,
    BloodCrimson,
    VerdantEmerald
}

internal sealed record NexAccentPalette(string Primary, string Bright, string Dark);

/// <summary>
/// Resource-driven visual system for the NexMUD game surface. Base material and
/// semantic colors remain stable; only interaction accents change between variants.
/// </summary>
internal static class NexMudTheme
{
    public const string AccentPrimaryKey = "NexMud.Accent.Primary";
    public const string AccentBrightKey = "NexMud.Accent.Bright";
    public const string AccentDarkKey = "NexMud.Accent.Dark";

    public static readonly IReadOnlyDictionary<NexAccentTheme, NexAccentPalette> AccentPalettes =
        new Dictionary<NexAccentTheme, NexAccentPalette>
        {
            [NexAccentTheme.EmberBrass] = new("#D4A65A", "#F0C675", "#8B5C28"),
            [NexAccentTheme.ArcaneCyan] = new("#22C7D6", "#66E5EF", "#127A86"),
            [NexAccentTheme.MysticViolet] = new("#8B6FDB", "#B59AF3", "#514080"),
            [NexAccentTheme.BloodCrimson] = new("#C64B55", "#EB7680", "#792B33"),
            [NexAccentTheme.VerdantEmerald] = new("#36A86E", "#63D594", "#236846")
        };

    public static readonly IBrush FrameDarkest = Brush("#080A0D");
    public static readonly IBrush FrameMetal = Brush("#181613");
    public static readonly IBrush BronzeShadow = Brush("#594127");
    public static readonly IBrush AntiqueBrass = Brush("#A97B3F");
    public static readonly IBrush BrassHighlight = Brush("#D4AF6A");
    public static readonly IBrush BrightEdge = Brush("#E1BF78");
    public static readonly IBrush SoftMetalHighlight = Brush("#72E1BF78");
    public static readonly IBrush DeepMetalShadow = Brush("#B2080A0D");

    public static readonly IBrush ApplicationBackground = Brush("#080B0F");
    public static readonly IBrush PrimarySurface = Brush("#0B1015");
    public static readonly IBrush SecondarySurface = Brush("#101820");
    public static readonly IBrush RaisedSurface = Brush("#16212B");
    public static readonly IBrush InteractiveSurface = Brush("#1B2934");
    public static readonly IBrush RecessedSurface = Brush("#070C11");
    public static readonly IBrush DeepConsole = Brush("#080D12");
    public static readonly IBrush Divider = Brush("#293540");

    public static readonly IBrush Parchment = Brush("#E6DDC6");
    public static readonly IBrush Muted = Brush("#9CA3AF");
    public static readonly IBrush Faint = Brush("#66717C");
    public static readonly IBrush Cyan = Brush("#22D4BF");
    public static readonly IBrush Success = Brush("#22C55E");
    public static readonly IBrush Warning = Brush("#F59E0B");
    public static readonly IBrush Danger = Brush("#EF4444");
    public static readonly IBrush Hp = Brush("#E24D5B");
    public static readonly IBrush Mana = Brush("#36B9E8");
    public static readonly IBrush Movement = Brush("#43BD82");
    public static readonly IBrush Target = Brush("#9B7DE3");
    public static readonly IBrush Xp = Brush("#DDB460");

    public static readonly FontFamily Display = new("Georgia, Charter, Times New Roman, serif");
    public static readonly FontFamily Interface = new("Inter, SF Pro Text, Helvetica Neue, sans-serif");
    public static readonly FontFamily Mono = new("Menlo, SFMono-Regular, Consolas, monospace");
    public static readonly FontFamily Terminal = ResolveTerminalFont();

    public const double BrandText = NexTypography.Display;
    public const double MajorSectionText = NexTypography.SectionTitle;
    public const double CharacterNameText = NexTypography.SectionTitle;
    public const double RoomTitleText = NexTypography.SectionTitle;
    public const double NavigationText = NexTypography.CompactData;
    public const double BodyText = NexTypography.Body;
    public const double SecondaryText = NexTypography.Metadata;
    public const double TranscriptText = NexTranscriptTypography.DefaultFontSize;
    public const double CommandText = NexTranscriptTypography.DefaultFontSize;

    public static NexAccentTheme CurrentAccent { get; private set; } = NexAccentTheme.EmberBrass;
    public static event Action? AccentChanged;

    public static IBrush AccentPrimary => Brush(AccentPalettes[CurrentAccent].Primary);
    public static IBrush AccentBright => Brush(AccentPalettes[CurrentAccent].Bright);
    public static IBrush AccentDark => Brush(AccentPalettes[CurrentAccent].Dark);

    public static void Install(Application app)
    {
        app.Resources["NexMud.Base.Application"] = ApplicationBackground;
        app.Resources["NexMud.Base.PrimarySurface"] = PrimarySurface;
        app.Resources["NexMud.Base.SecondarySurface"] = SecondarySurface;
        app.Resources["NexMud.Base.RaisedSurface"] = RaisedSurface;
        app.Resources["NexMud.Base.Parchment"] = Parchment;
        app.Resources["NexMud.Base.Brass"] = AntiqueBrass;
        app.Resources["Transcript.FontSize"] = NexTranscriptTypography.DefaultFontSize;
        app.Resources["Transcript.LineHeight"] = NexTranscriptTypography.LineHeight(NexTranscriptTypography.DefaultFontSize);
        app.Resources["Typography.Display"] = NexTypography.Display;
        app.Resources["Typography.SectionTitle"] = NexTypography.SectionTitle;
        app.Resources["Typography.Body"] = NexTypography.Body;
        app.Resources["Typography.BodyStrong"] = NexTypography.BodyStrong;
        app.Resources["Typography.Metadata"] = NexTypography.Metadata;
        app.Resources["Typography.Compact"] = NexTypography.CompactData;
        app.Resources["Typography.Mono"] = NexTypography.Monospace;
        app.Resources["Typography.Hud"] = NexTypography.Hud;
        app.Resources["Typography.HudStrong"] = NexTypography.HudStrong;
        app.Resources["Spacing.Micro"] = NexSpacing.Micro;
        app.Resources["Spacing.Tight"] = NexSpacing.Tight;
        app.Resources["Spacing.Normal"] = NexSpacing.Normal;
        app.Resources["Spacing.Section"] = NexSpacing.Section;
        ApplyAccent(app, NexAccentTheme.EmberBrass);
    }

    public static void ApplyAccent(Application app, NexAccentTheme theme)
    {
        CurrentAccent = theme;
        NexAccentPalette palette = AccentPalettes[theme];
        app.Resources[AccentPrimaryKey] = Brush(palette.Primary);
        app.Resources[AccentBrightKey] = Brush(palette.Bright);
        app.Resources[AccentDarkKey] = Brush(palette.Dark);
        AccentChanged?.Invoke();
    }

    public static LinearGradientBrush VerticalGradient(string top, string bottom) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops = new GradientStops
        {
            new(Color.Parse(top), 0),
            new(Color.Parse(bottom), 1)
        }
    };

    public static IBrush PrimarySurfaceGradient => VerticalGradient("#101820", "#0A0F14");
    public static IBrush SecondarySurfaceGradient => VerticalGradient("#17222C", "#0D151C");
    public static IBrush RaisedSurfaceGradient => VerticalGradient("#1A2934", "#111A22");
    public static IBrush MetalGradient => VerticalGradient("#211D18", "#11100E");
    public static IBrush InsetGradient => VerticalGradient("#0B1218", "#060A0E");

    public static Bitmap? TryLoadTexture(string fileName)
    {
        try
        {
            Uri uri = new($"avares://NexMUD/Assets/Textures/{fileName}");
            using Stream stream = AssetLoader.Open(uri);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static FontFamily ResolveTerminalFont()
    {
        if (OperatingSystem.IsMacOS()) return new FontFamily("Menlo");
        if (OperatingSystem.IsWindows()) return new FontFamily("Consolas");
        return new FontFamily("DejaVu Sans Mono");
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}

/// <summary>
/// Application-wide typography roles. Keep component code on these semantic roles instead of
/// inventing local micro-font sizes or applying one global line-height policy to every surface.
/// </summary>
internal static class NexTypography
{
    public const double Display = 24;
    public const double SectionTitle = 17;
    public const double Body = 13.5;
    public const double BodyStrong = 14;
    public const double Metadata = 11.5;
    public const double CompactData = 12.5;
    public const double Monospace = 13;
    public const double Hud = 12.5;
    public const double HudStrong = 13.5;

    public const double CompactLineHeightRatio = 1.2;
    public const double BodyLineHeightRatio = 1.3;
    public const double ProseLineHeightRatio = 1.4;
}

internal static class NexSpacing
{
    public const double Micro = 2;
    public const double Tight = 4;
    public const double Normal = 8;
    public const double Section = 12;
}

/// <summary>
/// Terminal typography is intentionally independent from normal UI typography. A single
/// platform-native monospace face, zero tracking, and a fixed line grid prevent ANSI runs,
/// network chunk boundaries, and font fallback metrics from changing row geometry.
/// </summary>
internal static class NexTranscriptTypography
{
    public const double DefaultFontSize = 14;
    public const double MinimumFontSize = 8;
    public const double MaximumFontSize = 24;
    public const double LineHeightRatio = 8d / 7d;

    public static double NormalizeFontSize(double fontSize) =>
        Math.Clamp(fontSize, MinimumFontSize, MaximumFontSize);

    public static double LineHeight(double fontSize) =>
        Math.Round(NormalizeFontSize(fontSize) * LineHeightRatio, 2);

    public static double HeightForRows(int rows, double fontSize) =>
        Math.Max(0, rows) * LineHeight(fontSize);

    public static void Apply(SelectableTextBlock text, double fontSize)
    {
        double normalized = NormalizeFontSize(fontSize);
        text.FontFamily = NexMudTheme.Terminal;
        text.FontSize = normalized;
        text.LineHeight = LineHeight(normalized);
        text.LineSpacing = 0;
        text.LetterSpacing = 0;
    }
}

internal static class NexApprovedAssets
{
    public static Bitmap? TryLoadApplicationIcon()
    {
        try
        {
            using Stream stream = AssetLoader.Open(new Uri("avares://NexMUD/Assets/app-icon.png"));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public static Image CreateApplicationIcon(double size)
    {
        Image image = new()
        {
            Source = TryLoadApplicationIcon(),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false
        };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        return image;
    }
}

internal sealed class NexFrameLightingOverlay : Control
{
    private readonly double _inset;
    private readonly bool _junctions;

    public NexFrameLightingOverlay(double inset, bool junctions)
    {
        _inset = inset;
        _junctions = junctions;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double left = _inset;
        double top = _inset;
        double right = Math.Max(left, Bounds.Width - _inset);
        double bottom = Math.Max(top, Bounds.Height - _inset);
        Pen highlight = new(NexMudTheme.SoftMetalHighlight, 1);
        Pen shadow = new(NexMudTheme.DeepMetalShadow, 2);
        Pen engraved = new(NexMudTheme.BronzeShadow, 1);

        context.DrawLine(highlight, new Point(left, top), new Point(right, top));
        context.DrawLine(highlight, new Point(left, top), new Point(left, bottom));
        context.DrawLine(shadow, new Point(left, bottom), new Point(right, bottom));
        context.DrawLine(shadow, new Point(right, top), new Point(right, bottom));

        double inner = _inset + 4;
        if (Bounds.Width > inner * 2 && Bounds.Height > inner * 2)
        {
            context.DrawLine(engraved, new Point(inner + 16, inner), new Point(right - 20, inner));
            context.DrawLine(engraved, new Point(inner + 16, bottom - 4), new Point(right - 20, bottom - 4));
        }

        if (!_junctions || Bounds.Width < 80) return;
        DrawJunction(context, new Point(Bounds.Width / 2, top + 1));
        DrawJunction(context, new Point(Bounds.Width / 2, bottom - 1));
    }

    private static void DrawJunction(DrawingContext context, Point center)
    {
        Pen brass = new(NexMudTheme.AntiqueBrass, 1);
        const double radius = 4;
        Point top = new(center.X, center.Y - radius);
        Point right = new(center.X + radius, center.Y);
        Point bottom = new(center.X, center.Y + radius);
        Point left = new(center.X - radius, center.Y);
        context.DrawLine(brass, top, right);
        context.DrawLine(brass, right, bottom);
        context.DrawLine(brass, bottom, left);
        context.DrawLine(brass, left, top);
    }
}

internal sealed class NexGameFrame : UserControl
{
    public NexGameFrame(Control child)
    {
        Grid root = new();
        root.Children.Add(new Border
        {
            Background = NexMudTheme.MetalGradient,
            BorderBrush = NexMudTheme.FrameDarkest,
            BorderThickness = new Thickness(3)
        });

        Bitmap? texture = NexMudTheme.TryLoadTexture("dark-metal.png");
        if (texture is not null)
        {
            root.Children.Add(new Image
            {
                Source = texture,
                Stretch = Stretch.UniformToFill,
                Opacity = 0.12,
                IsHitTestVisible = false
            });
        }

        Border bronze = new()
        {
            BorderBrush = NexMudTheme.BronzeShadow,
            BorderThickness = new Thickness(2),
            Margin = new Thickness(3),
            Padding = new Thickness(1),
            Child = new Border
            {
                BorderBrush = NexMudTheme.AntiqueBrass,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(3),
                Child = new Border
                {
                    BorderBrush = NexMudTheme.FrameDarkest,
                    BorderThickness = new Thickness(2),
                    Background = NexMudTheme.ApplicationBackground,
                    Child = child
                }
            }
        };
        root.Children.Add(bronze);
        root.Children.Add(new NexFrameLightingOverlay(5, junctions: false));
        Content = root;
    }
}

internal sealed class NexMajorPanel : UserControl
{
    public NexMajorPanel(string title, NexIconKind icon, Control body, Control? trailing = null)
    {
        bool hasHeader = !string.IsNullOrWhiteSpace(title);
        Grid content = new() { RowDefinitions = hasHeader ? new RowDefinitions("Auto,*") : new RowDefinitions("*") };
        if (hasHeader)
        {
            content.Children.Add(new NexPanelHeader(title, icon, trailing));
        }
        Border recess = new()
        {
            Background = NexMudTheme.PrimarySurfaceGradient,
            BorderBrush = NexMudTheme.FrameDarkest,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10),
            Child = body
        };
        if (hasHeader) Grid.SetRow(recess, 1);
        content.Children.Add(recess);

        Grid frame = new();
        frame.Children.Add(new Border
        {
            Background = NexMudTheme.SecondarySurfaceGradient,
            BorderBrush = NexMudTheme.FrameDarkest,
            BorderThickness = new Thickness(2)
        });
        Bitmap? panelTexture = NexMudTheme.TryLoadTexture("obsidian-grain.png");
        if (panelTexture is not null)
        {
            frame.Children.Add(new Image
            {
                Source = panelTexture,
                Stretch = Stretch.UniformToFill,
                Opacity = 0.07,
                IsHitTestVisible = false
            });
        }
        frame.Children.Add(new Border
        {
            BorderBrush = NexMudTheme.BronzeShadow,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(2),
            Padding = new Thickness(1),
            Child = new Border
            {
                BorderBrush = NexMudTheme.AntiqueBrass,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(2),
                Child = content
            }
        });
        frame.Children.Add(new NexFrameLightingOverlay(4, junctions: false));
        Content = frame;
    }
}

internal sealed class NexMinorPanel : UserControl
{
    public NexMinorPanel(Control child, Thickness? padding = null)
    {
        Content = new Border
        {
            Background = NexMudTheme.InsetGradient,
            BorderBrush = NexMudTheme.FrameDarkest,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(1),
            Child = new Border
            {
                BorderBrush = NexMudTheme.Divider,
                BorderThickness = new Thickness(1),
                Background = NexMudTheme.SecondarySurface,
                Padding = padding ?? new Thickness(9, 8),
                Child = child
            }
        };
    }
}

internal sealed class NexPanelHeader : UserControl
{
    public NexPanelHeader(string title, NexIconKind icon, Control? trailing = null)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            MinHeight = 42,
            Background = NexMudTheme.RaisedSurfaceGradient
        };
        StackPanel titleRow = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(13, 7)
        };
        titleRow.Children.Add(NexMudIcons.Create(icon, 19, NexMudTheme.AccentPrimary));
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = NexMudTheme.Parchment,
            FontFamily = NexMudTheme.Display,
            FontSize = NexMudTheme.MajorSectionText,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(titleRow);
        if (trailing is not null)
        {
            trailing.Margin = new Thickness(0, 6, 10, 6);
            Grid.SetColumn(trailing, 1);
            row.Children.Add(trailing);
        }

        Grid shell = new() { RowDefinitions = new RowDefinitions("*,2") };
        shell.Children.Add(row);
        Border accent = new()
        {
            Background = NexMudTheme.BronzeShadow,
            BorderBrush = NexMudTheme.AntiqueBrass,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        Grid.SetRow(accent, 1);
        shell.Children.Add(accent);
        Content = shell;
    }
}

internal sealed class NexSectionDivider : UserControl
{
    public NexSectionDivider()
    {
        Height = 12;
        Content = new Border
        {
            Height = 1,
            Margin = new Thickness(8, 5),
            Background = NexMudTheme.Divider,
            VerticalAlignment = VerticalAlignment.Center
        };
    }
}

internal sealed class NexCountBadge : Border
{
    public NexCountBadge(int count)
    {
        Background = NexMudTheme.InteractiveSurface;
        BorderBrush = NexMudTheme.AccentDark;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(9);
        Padding = new Thickness(6, 1);
        Child = new TextBlock
        {
            Text = count.ToString(),
            Foreground = NexMudTheme.Parchment,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.Bold
        };
    }
}

internal class NexResourceBar : UserControl
{
    private readonly ColumnDefinition _fillColumn = new();
    private readonly ColumnDefinition _remainingColumn = new();
    private readonly TextBlock _value = new();

    public NexResourceBar(IBrush fill, double height = 11)
    {
        Height = height;
        MinWidth = 76;

        Grid columns = new();
        columns.ColumnDefinitions.Add(_fillColumn);
        columns.ColumnDefinitions.Add(_remainingColumn);
        Border filled = new()
        {
            Background = fill,
            BorderBrush = NexMudTheme.BrightEdge,
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(2)
        };
        columns.Children.Add(filled);

        Border track = new()
        {
            Background = NexMudTheme.FrameDarkest,
            BorderBrush = NexMudTheme.Divider,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(1),
            Child = columns
        };

        _value.Foreground = NexMudTheme.Parchment;
        _value.FontSize = height >= 18 ? NexTypography.HudStrong : NexTypography.Metadata;
        _value.FontWeight = FontWeight.Bold;
        _value.HorizontalAlignment = HorizontalAlignment.Center;
        _value.VerticalAlignment = VerticalAlignment.Center;
        _value.TextAlignment = TextAlignment.Center;

        Border valueBackdrop = new()
        {
            Background = NexMudTheme.DeepMetalShadow,
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(4, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _value
        };

        Grid surface = new();
        surface.Children.Add(track);
        surface.Children.Add(valueBackdrop);
        Content = surface;
        Set(0, 1);
    }

    public void Set(double current, double maximum, string? valueText = null)
    {
        double ratio = maximum > 0 ? Math.Clamp(current / maximum, 0, 1) : 0;
        _fillColumn.Width = new GridLength(Math.Max(0.0001, ratio), GridUnitType.Star);
        _remainingColumn.Width = new GridLength(Math.Max(0.0001, 1 - ratio), GridUnitType.Star);
        _value.Text = valueText ?? string.Empty;
    }
}

internal sealed class NexXpBar : NexResourceBar
{
    public NexXpBar() : base(NexMudTheme.Xp, 12)
    {
    }
}

internal sealed class NexStatusCell : UserControl
{
    public TextBlock ValueText { get; } = new();

    public NexStatusCell(string label, NexIconKind icon)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 7 };
        row.Children.Add(NexMudIcons.Create(icon, 14, NexMudTheme.Muted));
        StackPanel text = new() { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            Foreground = NexMudTheme.Faint,
            FontSize = NexTypography.Metadata,
            FontWeight = FontWeight.SemiBold
        });
        ValueText.Foreground = NexMudTheme.Parchment;
        ValueText.FontSize = NexTypography.CompactData;
        ValueText.FontWeight = FontWeight.SemiBold;
        ValueText.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(ValueText);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Content = new NexMinorPanel(row, new Thickness(8, 7));
    }
}

internal sealed class NexAttributeCell : UserControl
{
    public NexAttributeCell(string name, string value, NexIconKind icon)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 6 };
        row.Children.Add(NexMudIcons.Create(icon, 13, NexMudTheme.AccentPrimary));
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
            FontSize = NexTypography.CompactData,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(score, 2);
        row.Children.Add(score);
        Content = new NexMinorPanel(row, new Thickness(7, 6));
    }
}

internal sealed class NexEntityGroup : UserControl
{
    public NexEntityGroup(string title, NexIconKind icon, IReadOnlyList<RoomEntityViewModel> entities)
    {
        StackPanel content = new() { Spacing = 6 };
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 7 };
        header.Children.Add(NexMudIcons.Create(icon, 14, NexMudTheme.AccentPrimary));
        TextBlock label = new()
        {
            Text = title.ToUpperInvariant(),
            Foreground = NexMudTheme.Parchment,
            FontSize = NexTypography.CompactData,
            FontWeight = FontWeight.Bold
        };
        Grid.SetColumn(label, 1);
        header.Children.Add(label);
        NexCountBadge badge = new(entities.Count);
        Grid.SetColumn(badge, 2);
        header.Children.Add(badge);
        content.Children.Add(header);

        if (entities.Count == 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = "None visible",
                Foreground = NexMudTheme.Faint,
                FontSize = NexTypography.CompactData
            });
        }
        else
        {
            foreach (RoomEntityViewModel entity in entities.Take(4))
            {
                content.Children.Add(new TextBlock
                {
                    Text = $"• {entity.Description}",
                    Foreground = NexMudTheme.Muted,
                    FontSize = NexTypography.CompactData,
                    TextWrapping = TextWrapping.Wrap
                });
            }
            if (entities.Count > 4)
            {
                content.Children.Add(new TextBlock
                {
                    Text = $"+{entities.Count - 4} more",
                    Foreground = NexMudTheme.Cyan,
                    FontSize = NexTypography.CompactData
                });
            }
        }
        Content = new NexMinorPanel(content, new Thickness(9, 8));
    }
}

internal sealed class NexQuickAction : Button
{
    public NexQuickAction(string label, NexIconKind icon)
    {
        MinHeight = 34;
        Padding = new Thickness(9, 5);
        Background = NexMudTheme.RaisedSurfaceGradient;
        Foreground = NexMudTheme.Parchment;
        BorderBrush = NexMudTheme.Divider;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(2);
        FontSize = NexTypography.CompactData;
        Content = IconLabel(icon, label, NexMudTheme.Muted, 14);
    }

    internal static StackPanel IconLabel(NexIconKind icon, string label, IBrush iconBrush, double iconSize)
    {
        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(NexMudIcons.Create(icon, iconSize, iconBrush));
        row.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = NexMudTheme.Parchment,
            FontSize = NexTypography.CompactData,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }
}

internal sealed class NexPrimaryButton : Button
{
    public NexPrimaryButton(string label, NexIconKind icon)
    {
        MinHeight = 34;
        MinWidth = 88;
        Padding = new Thickness(10, 4);
        Background = NexMudTheme.MetalGradient;
        Foreground = NexMudTheme.Parchment;
        BorderBrush = NexMudTheme.AccentBright;
        BorderThickness = new Thickness(2, 1, 2, 2);
        CornerRadius = new CornerRadius(2);
        Content = NexQuickAction.IconLabel(icon, label, NexMudTheme.AccentBright, 15);
    }
}

internal sealed class NexCommandBar : UserControl
{
    public NexCommandBar(Control child)
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Content = new Border
        {
            Background = NexMudTheme.MetalGradient,
            BorderBrush = NexMudTheme.BronzeShadow,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(5, 3),
            MinHeight = 40,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = child
        };
    }
}

internal sealed class NexIconButton : Button
{
    public NexIconButton(NexIconKind icon, string toolTip)
    {
        MinWidth = 38;
        MinHeight = 38;
        Padding = new Thickness(8);
        Background = NexMudTheme.RaisedSurfaceGradient;
        Foreground = NexMudTheme.Parchment;
        BorderBrush = NexMudTheme.Divider;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(2);
        Content = NexMudIcons.Create(icon, 17, NexMudTheme.Muted);
        ToolTip.SetTip(this, toolTip);
    }
}

internal sealed class NexNavItem : Button
{
    private readonly NexIconView _icon;
    private readonly TextBlock _label;
    private bool _active;

    public NexNavItem(string label, NexIconKind icon)
    {
        MinHeight = 40;
        Padding = new Thickness(NexSpacing.Normal, NexSpacing.Tight);
        CornerRadius = new CornerRadius(0);
        BorderThickness = new Thickness(0, 0, 0, 3);
        Background = Brushes.Transparent;
        BorderBrush = Brushes.Transparent;

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };
        _icon = NexMudIcons.Create(icon, 17, NexMudTheme.Muted);
        _label = new TextBlock
        {
            Text = label,
            Foreground = NexMudTheme.Muted,
            FontSize = NexMudTheme.NavigationText,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_icon);
        row.Children.Add(_label);
        Content = row;
        ToolTip.SetTip(this, label);
        NexMudTheme.AccentChanged += Restyle;
    }

    public void SetCompact(bool compact)
    {
        _label.IsVisible = !compact;
        Padding = compact
            ? new Thickness(9, NexSpacing.Tight)
            : new Thickness(NexSpacing.Normal, NexSpacing.Tight);
    }

    public void SetActive(bool active)
    {
        _active = active;
        Restyle();
    }

    private void Restyle()
    {
        Background = _active ? NexMudTheme.InteractiveSurface : Brushes.Transparent;
        BorderBrush = _active ? NexMudTheme.AccentPrimary : Brushes.Transparent;
        _label.Foreground = _active ? NexMudTheme.Parchment : NexMudTheme.Muted;
        _icon.Stroke = _active ? NexMudTheme.AccentBright : NexMudTheme.Muted;
        Opacity = _active ? 1 : 0.88;
    }
}
