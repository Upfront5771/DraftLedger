using System.Windows;
using System.Windows.Media;

namespace DraftLedger.App;

internal sealed record ThemePalette(
    string Name,
    bool Dark,
    string Canvas,
    string Panel,
    string Paper,
    string Ink,
    string Muted,
    string Line,
    string Accent,
    string AccentHover,
    string Hover,
    string Selection,
    string SelectionText,
    string InactiveSelection,
    string Disabled);

internal static class ThemeCatalog
{
    public static readonly string[] Names = ["System", "Pen & Paper", "Muted Sage", "Retro Terminal", "Midnight Ink", "Charcoal", "Deep Navy"];

    private static readonly ThemePalette PenAndPaper = new(
        "Pen & Paper", false, "#E9E3D6", "#F3EEE3", "#FFFDF7", "#2E2923", "#71695E", "#D6CCBC", "#87523A", "#A56547", "#E7DDCE", "#CFAE98", "#251D19", "#DCCBBD", "#9C958B");

    private static readonly ThemePalette MutedSage = new(
        "Muted Sage", false, "#E4E8E1", "#EEF1EA", "#F9FAF6", "#2D3731", "#68736C", "#CAD2C8", "#557A66", "#426854", "#DFE7DF", "#B7CEBD", "#1E2A23", "#C8D4CB", "#959E98");

    private static readonly ThemePalette RetroTerminal = new(
        "Retro Terminal", true, "#11130E", "#181B13", "#0D100B", "#D8D9AD", "#929A78", "#343A27", "#D99845", "#EEAF5B", "#24291B", "#66461F", "#FFF1CB", "#4F3B22", "#68705A");

    private static readonly ThemePalette MidnightInk = new(
        "Midnight Ink", true, "#111620", "#181F2B", "#0D131D", "#E6EAF2", "#98A3B6", "#2D3748", "#7898E8", "#96AFF0", "#222B3A", "#31558D", "#FFFFFF", "#293F64", "#6F7888");

    private static readonly ThemePalette Charcoal = new(
        "Charcoal", true, "#181818", "#222222", "#111111", "#ECECEC", "#A7A7A7", "#383838", "#B58ACA", "#CBA4DA", "#2B2B2B", "#5A3F67", "#FFFFFF", "#493651", "#747474");

    private static readonly ThemePalette DeepNavy = new(
        "Deep Navy", true, "#08131E", "#0D1C2B", "#081724", "#E1EBF2", "#8FA5B5", "#20384C", "#4EA2C8", "#6AB9D8", "#14283A", "#1C607A", "#FFFFFF", "#184A60", "#657C8C");

    public static string NormalizeName(string name) => name switch { "Light" => "Pen & Paper", "Dark" => "Midnight Ink", _ when Names.Contains(name) => name, _ => "System" };

    public static ThemePalette Resolve(string name, bool systemDark) => name switch
    {
        "System" => systemDark ? MidnightInk : PenAndPaper,
        "Light" => PenAndPaper,
        "Dark" => MidnightInk,
        "Muted Sage" => MutedSage,
        "Retro Terminal" => RetroTerminal,
        "Midnight Ink" => MidnightInk,
        "Charcoal" => Charcoal,
        "Deep Navy" => DeepNavy,
        _ => PenAndPaper
    };

    public static void Apply(ResourceDictionary resources, ThemePalette palette)
    {
        var values = new Dictionary<object, string>
        {
            ["CanvasBrush"] = palette.Canvas,
            ["PanelBrush"] = palette.Panel,
            ["PaperBrush"] = palette.Paper,
            ["InkBrush"] = palette.Ink,
            ["MutedBrush"] = palette.Muted,
            ["LineBrush"] = palette.Line,
            ["AccentBrush"] = palette.Accent,
            ["AccentHoverBrush"] = palette.AccentHover,
            ["AccentTextBrush"] = palette.Dark ? "#10151C" : "#FFFFFF",
            ["HoverBrush"] = palette.Hover,
            ["SelectionBrush"] = palette.Selection,
            ["SelectionTextBrush"] = palette.SelectionText,
            ["InactiveSelectionBrush"] = palette.InactiveSelection,
            ["DisabledBrush"] = palette.Disabled,
            [SystemColors.WindowBrushKey] = palette.Paper,
            [SystemColors.WindowTextBrushKey] = palette.Ink,
            [SystemColors.ControlBrushKey] = palette.Panel,
            [SystemColors.ControlTextBrushKey] = palette.Ink,
            [SystemColors.MenuBrushKey] = palette.Paper,
            [SystemColors.MenuTextBrushKey] = palette.Ink,
            [SystemColors.HighlightBrushKey] = palette.Selection,
            [SystemColors.HighlightTextBrushKey] = palette.SelectionText,
            [SystemColors.InactiveSelectionHighlightBrushKey] = palette.InactiveSelection,
            [SystemColors.InactiveSelectionHighlightTextBrushKey] = palette.SelectionText,
            [SystemColors.GrayTextBrushKey] = palette.Disabled
        };
        foreach (var pair in values) resources[pair.Key] = Brush(pair.Value);
    }

    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
