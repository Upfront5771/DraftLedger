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
    public static readonly string[] Names = ["System", "Pen & Paper", "Warm Sepia", "Muted Sage", "Ocean Mist", "Retro Terminal", "Midnight Ink", "Charcoal", "Deep Navy", "Forest Night", "Plum Noir"];

    private static readonly ThemePalette PenAndPaper = new(
        "Pen & Paper", false, "#E9E3D6", "#F3EEE3", "#FFFDF7", "#2E2923", "#71695E", "#D6CCBC", "#87523A", "#A56547", "#E7DDCE", "#CFAE98", "#251D19", "#DCCBBD", "#9C958B");

    private static readonly ThemePalette MutedSage = new(
        "Muted Sage", false, "#E4E8E1", "#EEF1EA", "#F9FAF6", "#2D3731", "#68736C", "#CAD2C8", "#557A66", "#426854", "#DFE7DF", "#B7CEBD", "#1E2A23", "#C8D4CB", "#959E98");

    private static readonly ThemePalette WarmSepia = new(
        "Warm Sepia", false, "#DED0B8", "#E9DDC8", "#F7EDD9", "#392E24", "#796A58", "#CABB9F", "#9A5D35", "#B36E41", "#E1D2B8", "#D2AD83", "#2F2118", "#D8C3A5", "#9D907D");

    private static readonly ThemePalette OceanMist = new(
        "Ocean Mist", false, "#DCE8EA", "#E8F1F2", "#F7FBFB", "#26383C", "#61777C", "#C3D5D8", "#387B88", "#2B6874", "#D7E7E9", "#A8CDD3", "#18343A", "#BED7DB", "#899EA2");

    private static readonly ThemePalette RetroTerminal = new(
        "Retro Terminal", true, "#11130E", "#181B13", "#0D100B", "#D8D9AD", "#929A78", "#343A27", "#D99845", "#EEAF5B", "#24291B", "#66461F", "#FFF1CB", "#4F3B22", "#68705A");

    private static readonly ThemePalette MidnightInk = new(
        "Midnight Ink", true, "#111620", "#181F2B", "#0D131D", "#E6EAF2", "#98A3B6", "#2D3748", "#7898E8", "#96AFF0", "#222B3A", "#31558D", "#FFFFFF", "#293F64", "#6F7888");

    private static readonly ThemePalette Charcoal = new(
        "Charcoal", true, "#181818", "#222222", "#111111", "#ECECEC", "#A7A7A7", "#383838", "#B58ACA", "#CBA4DA", "#2B2B2B", "#5A3F67", "#FFFFFF", "#493651", "#747474");

    private static readonly ThemePalette DeepNavy = new(
        "Deep Navy", true, "#08131E", "#0D1C2B", "#081724", "#E1EBF2", "#8FA5B5", "#20384C", "#4EA2C8", "#6AB9D8", "#14283A", "#1C607A", "#FFFFFF", "#184A60", "#657C8C");

    private static readonly ThemePalette ForestNight = new(
        "Forest Night", true, "#0C1712", "#13221A", "#0A130F", "#E0EADF", "#91A496", "#294034", "#70A780", "#8FC09B", "#1A2C22", "#2E6742", "#FFFFFF", "#28553A", "#66766B");

    private static readonly ThemePalette PlumNoir = new(
        "Plum Noir", true, "#17101A", "#241929", "#100C13", "#F0E6F1", "#AF98B3", "#3D2A43", "#C07DB5", "#D79ACD", "#2D2032", "#70436A", "#FFFFFF", "#573650", "#7C697F");

    public static string NormalizeName(string name) => name switch { "Light" => "Pen & Paper", "Dark" => "Midnight Ink", _ when Names.Contains(name) => name, _ => "System" };

    public static ThemePalette Resolve(string name, bool systemDark) => name switch
    {
        "System" => systemDark ? MidnightInk : PenAndPaper,
        "Light" => PenAndPaper,
        "Dark" => MidnightInk,
        "Warm Sepia" => WarmSepia,
        "Muted Sage" => MutedSage,
        "Ocean Mist" => OceanMist,
        "Retro Terminal" => RetroTerminal,
        "Midnight Ink" => MidnightInk,
        "Charcoal" => Charcoal,
        "Deep Navy" => DeepNavy,
        "Forest Night" => ForestNight,
        "Plum Noir" => PlumNoir,
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
