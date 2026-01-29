namespace RVUCounter.Core;

/// <summary>
/// A single theme preset with a name, dark/light flag, and all brush colors.
/// </summary>
public record ThemePreset(string Key, string DisplayName, bool IsDark, Dictionary<string, string> Colors);

/// <summary>
/// Defines all built-in theme presets and brush resource key metadata.
/// </summary>
public static class ThemePresets
{
    // All 23 brush resource keys used in themes
    public static readonly string[] AllKeys =
    {
        "WindowBackgroundBrush",
        "PanelBackgroundBrush",
        "CardBackgroundBrush",
        "HeaderBackgroundBrush",
        "StatusBarBackgroundBrush",
        "PrimaryTextBrush",
        "SecondaryTextBrush",
        "HeaderTextBrush",
        "ButtonTextBrush",
        "AccentBrush",
        "LightAccentBrush",
        "SuccessBrush",
        "DangerBrush",
        "CompBrush",
        "BorderBrush",
        "SeparatorBrush",
        "ButtonBackgroundBrush",
        "ButtonHoverBrush",
        "HoverBackgroundBrush",
        "InputBackgroundBrush",
        "HeaderIconBrush",
        "PaceMarkerBrush",
        "AlternatingRowBackgroundBrush"
    };

    // Grouped keys for UI display
    public static readonly string[] BackgroundKeys =
    {
        "WindowBackgroundBrush",
        "PanelBackgroundBrush",
        "CardBackgroundBrush",
        "HeaderBackgroundBrush",
        "StatusBarBackgroundBrush"
    };

    public static readonly string[] TextKeys =
    {
        "PrimaryTextBrush",
        "SecondaryTextBrush",
        "HeaderTextBrush",
        "ButtonTextBrush"
    };

    public static readonly string[] AccentKeys =
    {
        "AccentBrush",
        "LightAccentBrush",
        "SuccessBrush",
        "DangerBrush",
        "CompBrush"
    };

    public static readonly string[] UIElementKeys =
    {
        "BorderBrush",
        "SeparatorBrush",
        "ButtonBackgroundBrush",
        "ButtonHoverBrush",
        "HoverBackgroundBrush",
        "InputBackgroundBrush",
        "HeaderIconBrush",
        "PaceMarkerBrush",
        "AlternatingRowBackgroundBrush"
    };

    /// <summary>
    /// Human-friendly display names for each resource key.
    /// </summary>
    public static readonly Dictionary<string, string> DisplayNames = new()
    {
        ["WindowBackgroundBrush"] = "Window Background",
        ["PanelBackgroundBrush"] = "Panel Background",
        ["CardBackgroundBrush"] = "Card Background",
        ["HeaderBackgroundBrush"] = "Header Background",
        ["StatusBarBackgroundBrush"] = "Status Bar",
        ["PrimaryTextBrush"] = "Primary Text",
        ["SecondaryTextBrush"] = "Secondary Text",
        ["HeaderTextBrush"] = "Header Text",
        ["ButtonTextBrush"] = "Button Text",
        ["AccentBrush"] = "Accent",
        ["LightAccentBrush"] = "Light Accent",
        ["SuccessBrush"] = "Success",
        ["DangerBrush"] = "Danger",
        ["CompBrush"] = "Compensation",
        ["BorderBrush"] = "Border",
        ["SeparatorBrush"] = "Separator",
        ["ButtonBackgroundBrush"] = "Button Background",
        ["ButtonHoverBrush"] = "Button Hover",
        ["HoverBackgroundBrush"] = "Hover Background",
        ["InputBackgroundBrush"] = "Input Background",
        ["HeaderIconBrush"] = "Header Icon",
        ["PaceMarkerBrush"] = "Pace Marker",
        ["AlternatingRowBackgroundBrush"] = "Alternating Row"
    };

    /// <summary>
    /// All built-in presets indexed by key.
    /// </summary>
    public static readonly Dictionary<string, ThemePreset> Presets = new()
    {
        ["default_dark"] = new ThemePreset("default_dark", "Default Dark", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#1E1E1E",
            ["PanelBackgroundBrush"] = "#2D2D2D",
            ["CardBackgroundBrush"] = "#383838",
            ["HeaderBackgroundBrush"] = "#1A1A1A",
            ["StatusBarBackgroundBrush"] = "#252525",
            ["PrimaryTextBrush"] = "#E0E0E0",
            ["SecondaryTextBrush"] = "#A0A0A0",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#E0E0E0",
            ["AccentBrush"] = "#4DA8FF",
            ["LightAccentBrush"] = "#334DA8FF",
            ["SuccessBrush"] = "#336600",
            ["DangerBrush"] = "#663300",
            ["CompBrush"] = "#90EE90",
            ["BorderBrush"] = "#444444",
            ["SeparatorBrush"] = "#3A3A3A",
            ["ButtonBackgroundBrush"] = "#404040",
            ["ButtonHoverBrush"] = "#505050",
            ["HoverBackgroundBrush"] = "#404040",
            ["InputBackgroundBrush"] = "#252525",
            ["HeaderIconBrush"] = "#888888",
            ["PaceMarkerBrush"] = "#FFFFFF",
            ["AlternatingRowBackgroundBrush"] = "#333333"
        }),

        ["default_light"] = new ThemePreset("default_light", "Default Light", false, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#FAFAFA",
            ["PanelBackgroundBrush"] = "#F5F5F5",
            ["CardBackgroundBrush"] = "#FFFFFF",
            ["HeaderBackgroundBrush"] = "#2D3436",
            ["StatusBarBackgroundBrush"] = "#F0F0F0",
            ["PrimaryTextBrush"] = "#2D3436",
            ["SecondaryTextBrush"] = "#636E72",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#2D3436",
            ["AccentBrush"] = "#0984E3",
            ["LightAccentBrush"] = "#E8F4FD",
            ["SuccessBrush"] = "#336600",
            ["DangerBrush"] = "#663300",
            ["CompBrush"] = "#228B22",
            ["BorderBrush"] = "#DDDDDD",
            ["SeparatorBrush"] = "#EEEEEE",
            ["ButtonBackgroundBrush"] = "#E8E8E8",
            ["ButtonHoverBrush"] = "#D0D0D0",
            ["HoverBackgroundBrush"] = "#E0E0E0",
            ["InputBackgroundBrush"] = "#FFFFFF",
            ["HeaderIconBrush"] = "#AAAAAA",
            ["PaceMarkerBrush"] = "#000000",
            ["AlternatingRowBackgroundBrush"] = "#F9F9F9"
        }),

        ["mosaic"] = new ThemePreset("mosaic", "Mosaic", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#0B1A3B",
            ["PanelBackgroundBrush"] = "#122350",
            ["CardBackgroundBrush"] = "#1A2D5E",
            ["HeaderBackgroundBrush"] = "#071231",
            ["StatusBarBackgroundBrush"] = "#0F1D45",
            ["PrimaryTextBrush"] = "#D0E0F0",
            ["SecondaryTextBrush"] = "#7A8FAA",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#D0E0F0",
            ["AccentBrush"] = "#2EC4B6",
            ["LightAccentBrush"] = "#202EC4B6",
            ["SuccessBrush"] = "#6BCB77",
            ["DangerBrush"] = "#E05555",
            ["CompBrush"] = "#6BCB77",
            ["BorderBrush"] = "#1E3A6E",
            ["SeparatorBrush"] = "#172F5A",
            ["ButtonBackgroundBrush"] = "#1E3A6E",
            ["ButtonHoverBrush"] = "#2A4A80",
            ["HoverBackgroundBrush"] = "#1E3A6E",
            ["InputBackgroundBrush"] = "#0F1D45",
            ["HeaderIconBrush"] = "#4A6FA0",
            ["PaceMarkerBrush"] = "#FFFFFF",
            ["AlternatingRowBackgroundBrush"] = "#0F2248"
        }),

        ["midnight_blue"] = new ThemePreset("midnight_blue", "Midnight Blue", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#0D1B2A",
            ["PanelBackgroundBrush"] = "#1B2838",
            ["CardBackgroundBrush"] = "#243447",
            ["HeaderBackgroundBrush"] = "#091320",
            ["StatusBarBackgroundBrush"] = "#152232",
            ["PrimaryTextBrush"] = "#E0E8F0",
            ["SecondaryTextBrush"] = "#8899AA",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#E0E8F0",
            ["AccentBrush"] = "#48DBFB",
            ["LightAccentBrush"] = "#2048DBFB",
            ["SuccessBrush"] = "#55EFC4",
            ["DangerBrush"] = "#FF6B6B",
            ["CompBrush"] = "#55EFC4",
            ["BorderBrush"] = "#2C3E50",
            ["SeparatorBrush"] = "#233545",
            ["ButtonBackgroundBrush"] = "#2C3E50",
            ["ButtonHoverBrush"] = "#3A5068",
            ["HoverBackgroundBrush"] = "#2C3E50",
            ["InputBackgroundBrush"] = "#152232",
            ["HeaderIconBrush"] = "#5A7A8A",
            ["PaceMarkerBrush"] = "#FFFFFF",
            ["AlternatingRowBackgroundBrush"] = "#172838"
        }),

        ["nord"] = new ThemePreset("nord", "Nord", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#2E3440",
            ["PanelBackgroundBrush"] = "#3B4252",
            ["CardBackgroundBrush"] = "#434C5E",
            ["HeaderBackgroundBrush"] = "#272C36",
            ["StatusBarBackgroundBrush"] = "#333A47",
            ["PrimaryTextBrush"] = "#ECEFF4",
            ["SecondaryTextBrush"] = "#A0AAB8",
            ["HeaderTextBrush"] = "#ECEFF4",
            ["ButtonTextBrush"] = "#ECEFF4",
            ["AccentBrush"] = "#88C0D0",
            ["LightAccentBrush"] = "#2088C0D0",
            ["SuccessBrush"] = "#A3BE8C",
            ["DangerBrush"] = "#BF616A",
            ["CompBrush"] = "#A3BE8C",
            ["BorderBrush"] = "#4C566A",
            ["SeparatorBrush"] = "#434C5E",
            ["ButtonBackgroundBrush"] = "#4C566A",
            ["ButtonHoverBrush"] = "#5E6779",
            ["HoverBackgroundBrush"] = "#4C566A",
            ["InputBackgroundBrush"] = "#333A47",
            ["HeaderIconBrush"] = "#6A7385",
            ["PaceMarkerBrush"] = "#ECEFF4",
            ["AlternatingRowBackgroundBrush"] = "#353B49"
        }),

        ["dracula"] = new ThemePreset("dracula", "Dracula", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#282A36",
            ["PanelBackgroundBrush"] = "#343746",
            ["CardBackgroundBrush"] = "#3E4155",
            ["HeaderBackgroundBrush"] = "#21222C",
            ["StatusBarBackgroundBrush"] = "#2D2F3E",
            ["PrimaryTextBrush"] = "#F8F8F2",
            ["SecondaryTextBrush"] = "#9A9CAE",
            ["HeaderTextBrush"] = "#F8F8F2",
            ["ButtonTextBrush"] = "#F8F8F2",
            ["AccentBrush"] = "#BD93F9",
            ["LightAccentBrush"] = "#20BD93F9",
            ["SuccessBrush"] = "#50FA7B",
            ["DangerBrush"] = "#FF5555",
            ["CompBrush"] = "#50FA7B",
            ["BorderBrush"] = "#44475A",
            ["SeparatorBrush"] = "#3A3D4E",
            ["ButtonBackgroundBrush"] = "#44475A",
            ["ButtonHoverBrush"] = "#555870",
            ["HoverBackgroundBrush"] = "#44475A",
            ["InputBackgroundBrush"] = "#2D2F3E",
            ["HeaderIconBrush"] = "#6272A4",
            ["PaceMarkerBrush"] = "#F8F8F2",
            ["AlternatingRowBackgroundBrush"] = "#303240"
        }),

        ["solarized_dark"] = new ThemePreset("solarized_dark", "Solarized Dark", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#002B36",
            ["PanelBackgroundBrush"] = "#073642",
            ["CardBackgroundBrush"] = "#0A4050",
            ["HeaderBackgroundBrush"] = "#00212B",
            ["StatusBarBackgroundBrush"] = "#04303A",
            ["PrimaryTextBrush"] = "#93A1A1",
            ["SecondaryTextBrush"] = "#657B83",
            ["HeaderTextBrush"] = "#FDF6E3",
            ["ButtonTextBrush"] = "#93A1A1",
            ["AccentBrush"] = "#268BD2",
            ["LightAccentBrush"] = "#20268BD2",
            ["SuccessBrush"] = "#859900",
            ["DangerBrush"] = "#DC322F",
            ["CompBrush"] = "#859900",
            ["BorderBrush"] = "#0D4F5C",
            ["SeparatorBrush"] = "#0A424E",
            ["ButtonBackgroundBrush"] = "#0D4F5C",
            ["ButtonHoverBrush"] = "#136070",
            ["HoverBackgroundBrush"] = "#0D4F5C",
            ["InputBackgroundBrush"] = "#04303A",
            ["HeaderIconBrush"] = "#4A6A72",
            ["PaceMarkerBrush"] = "#FDF6E3",
            ["AlternatingRowBackgroundBrush"] = "#05353F"
        }),

        ["monokai"] = new ThemePreset("monokai", "Monokai", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#272822",
            ["PanelBackgroundBrush"] = "#333428",
            ["CardBackgroundBrush"] = "#3E3F32",
            ["HeaderBackgroundBrush"] = "#1E1F1A",
            ["StatusBarBackgroundBrush"] = "#2D2E26",
            ["PrimaryTextBrush"] = "#F8F8F2",
            ["SecondaryTextBrush"] = "#A0A090",
            ["HeaderTextBrush"] = "#F8F8F2",
            ["ButtonTextBrush"] = "#F8F8F2",
            ["AccentBrush"] = "#66D9EF",
            ["LightAccentBrush"] = "#2066D9EF",
            ["SuccessBrush"] = "#A6E22E",
            ["DangerBrush"] = "#F92672",
            ["CompBrush"] = "#A6E22E",
            ["BorderBrush"] = "#49483E",
            ["SeparatorBrush"] = "#3E3D34",
            ["ButtonBackgroundBrush"] = "#49483E",
            ["ButtonHoverBrush"] = "#5A594E",
            ["HoverBackgroundBrush"] = "#49483E",
            ["InputBackgroundBrush"] = "#2D2E26",
            ["HeaderIconBrush"] = "#6A6A5A",
            ["PaceMarkerBrush"] = "#F8F8F2",
            ["AlternatingRowBackgroundBrush"] = "#2F302A"
        }),

        ["forest"] = new ThemePreset("forest", "Forest", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#1A2418",
            ["PanelBackgroundBrush"] = "#253222",
            ["CardBackgroundBrush"] = "#2F3E2C",
            ["HeaderBackgroundBrush"] = "#141C12",
            ["StatusBarBackgroundBrush"] = "#1F2B1D",
            ["PrimaryTextBrush"] = "#D8E8D0",
            ["SecondaryTextBrush"] = "#8AA880",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#D8E8D0",
            ["AccentBrush"] = "#7BC67E",
            ["LightAccentBrush"] = "#207BC67E",
            ["SuccessBrush"] = "#7BC67E",
            ["DangerBrush"] = "#D05050",
            ["CompBrush"] = "#7BC67E",
            ["BorderBrush"] = "#3A4E36",
            ["SeparatorBrush"] = "#2E4028",
            ["ButtonBackgroundBrush"] = "#3A4E36",
            ["ButtonHoverBrush"] = "#4A6044",
            ["HoverBackgroundBrush"] = "#3A4E36",
            ["InputBackgroundBrush"] = "#1F2B1D",
            ["HeaderIconBrush"] = "#5A7A55",
            ["PaceMarkerBrush"] = "#FFFFFF",
            ["AlternatingRowBackgroundBrush"] = "#222E20"
        }),

        ["high_contrast"] = new ThemePreset("high_contrast", "High Contrast", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#000000",
            ["PanelBackgroundBrush"] = "#0A0A0A",
            ["CardBackgroundBrush"] = "#141414",
            ["HeaderBackgroundBrush"] = "#000000",
            ["StatusBarBackgroundBrush"] = "#050505",
            ["PrimaryTextBrush"] = "#FFFFFF",
            ["SecondaryTextBrush"] = "#C0C0C0",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#FFFFFF",
            ["AccentBrush"] = "#00BFFF",
            ["LightAccentBrush"] = "#2000BFFF",
            ["SuccessBrush"] = "#00FF00",
            ["DangerBrush"] = "#FF0000",
            ["CompBrush"] = "#00FF00",
            ["BorderBrush"] = "#555555",
            ["SeparatorBrush"] = "#333333",
            ["ButtonBackgroundBrush"] = "#333333",
            ["ButtonHoverBrush"] = "#555555",
            ["HoverBackgroundBrush"] = "#333333",
            ["InputBackgroundBrush"] = "#0A0A0A",
            ["HeaderIconBrush"] = "#888888",
            ["PaceMarkerBrush"] = "#FFFFFF",
            ["AlternatingRowBackgroundBrush"] = "#0D0D0D"
        }),

        // --- Additional creative presets ---

        ["rose"] = new ThemePreset("rose", "Rose", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#1F1520",
            ["PanelBackgroundBrush"] = "#2A1E2E",
            ["CardBackgroundBrush"] = "#352838",
            ["HeaderBackgroundBrush"] = "#18101A",
            ["StatusBarBackgroundBrush"] = "#241A28",
            ["PrimaryTextBrush"] = "#F0E0F0",
            ["SecondaryTextBrush"] = "#A08AAA",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#F0E0F0",
            ["AccentBrush"] = "#FF6B9D",
            ["LightAccentBrush"] = "#20FF6B9D",
            ["SuccessBrush"] = "#7BC67E",
            ["DangerBrush"] = "#FF5555",
            ["CompBrush"] = "#7BC67E",
            ["BorderBrush"] = "#4A3050",
            ["SeparatorBrush"] = "#3A2640",
            ["ButtonBackgroundBrush"] = "#4A3050",
            ["ButtonHoverBrush"] = "#5C4065",
            ["HoverBackgroundBrush"] = "#4A3050",
            ["InputBackgroundBrush"] = "#241A28",
            ["HeaderIconBrush"] = "#7A5A80",
            ["PaceMarkerBrush"] = "#FFFFFF",
            ["AlternatingRowBackgroundBrush"] = "#251C28"
        }),

        ["lavender"] = new ThemePreset("lavender", "Lavender", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#1A1528",
            ["PanelBackgroundBrush"] = "#252035",
            ["CardBackgroundBrush"] = "#302A42",
            ["HeaderBackgroundBrush"] = "#14101E",
            ["StatusBarBackgroundBrush"] = "#1F1A2E",
            ["PrimaryTextBrush"] = "#E8E0F8",
            ["SecondaryTextBrush"] = "#9A90B0",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#E8E0F8",
            ["AccentBrush"] = "#B388FF",
            ["LightAccentBrush"] = "#20B388FF",
            ["SuccessBrush"] = "#69F0AE",
            ["DangerBrush"] = "#FF5252",
            ["CompBrush"] = "#69F0AE",
            ["BorderBrush"] = "#3E3555",
            ["SeparatorBrush"] = "#302845",
            ["ButtonBackgroundBrush"] = "#3E3555",
            ["ButtonHoverBrush"] = "#504668",
            ["HoverBackgroundBrush"] = "#3E3555",
            ["InputBackgroundBrush"] = "#1F1A2E",
            ["HeaderIconBrush"] = "#6A5A88",
            ["PaceMarkerBrush"] = "#FFFFFF",
            ["AlternatingRowBackgroundBrush"] = "#221D30"
        }),

        ["sakura"] = new ThemePreset("sakura", "Sakura", false, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#FFF5F5",
            ["PanelBackgroundBrush"] = "#FFEDED",
            ["CardBackgroundBrush"] = "#FFFFFF",
            ["HeaderBackgroundBrush"] = "#D4487A",
            ["StatusBarBackgroundBrush"] = "#FFE8EE",
            ["PrimaryTextBrush"] = "#4A2040",
            ["SecondaryTextBrush"] = "#8A6080",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#4A2040",
            ["AccentBrush"] = "#E91E80",
            ["LightAccentBrush"] = "#FFF0F5",
            ["SuccessBrush"] = "#2E7D32",
            ["DangerBrush"] = "#C62828",
            ["CompBrush"] = "#2E7D32",
            ["BorderBrush"] = "#F0C0D0",
            ["SeparatorBrush"] = "#F5D0DE",
            ["ButtonBackgroundBrush"] = "#FFD0E0",
            ["ButtonHoverBrush"] = "#FFC0D5",
            ["HoverBackgroundBrush"] = "#FFE0EA",
            ["InputBackgroundBrush"] = "#FFFFFF",
            ["HeaderIconBrush"] = "#CC6699",
            ["PaceMarkerBrush"] = "#4A2040",
            ["AlternatingRowBackgroundBrush"] = "#FFF0F3"
        }),

        ["sunset"] = new ThemePreset("sunset", "Sunset", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#1A1018",
            ["PanelBackgroundBrush"] = "#281A22",
            ["CardBackgroundBrush"] = "#32222C",
            ["HeaderBackgroundBrush"] = "#140C12",
            ["StatusBarBackgroundBrush"] = "#22151E",
            ["PrimaryTextBrush"] = "#F0E0D8",
            ["SecondaryTextBrush"] = "#AA8A80",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#F0E0D8",
            ["AccentBrush"] = "#FF6E40",
            ["LightAccentBrush"] = "#20FF6E40",
            ["SuccessBrush"] = "#FFD740",
            ["DangerBrush"] = "#FF1744",
            ["CompBrush"] = "#FFD740",
            ["BorderBrush"] = "#4A3040",
            ["SeparatorBrush"] = "#3A2530",
            ["ButtonBackgroundBrush"] = "#4A3040",
            ["ButtonHoverBrush"] = "#5C4050",
            ["HoverBackgroundBrush"] = "#4A3040",
            ["InputBackgroundBrush"] = "#22151E",
            ["HeaderIconBrush"] = "#7A5A60",
            ["PaceMarkerBrush"] = "#FFFFFF",
            ["AlternatingRowBackgroundBrush"] = "#221820"
        }),

        ["ocean"] = new ThemePreset("ocean", "Ocean", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#0A192F",
            ["PanelBackgroundBrush"] = "#112240",
            ["CardBackgroundBrush"] = "#1A2E50",
            ["HeaderBackgroundBrush"] = "#061225",
            ["StatusBarBackgroundBrush"] = "#0E1C38",
            ["PrimaryTextBrush"] = "#CCD6F6",
            ["SecondaryTextBrush"] = "#8892B0",
            ["HeaderTextBrush"] = "#E6F1FF",
            ["ButtonTextBrush"] = "#CCD6F6",
            ["AccentBrush"] = "#64FFDA",
            ["LightAccentBrush"] = "#2064FFDA",
            ["SuccessBrush"] = "#64FFDA",
            ["DangerBrush"] = "#FF6B6B",
            ["CompBrush"] = "#64FFDA",
            ["BorderBrush"] = "#233554",
            ["SeparatorBrush"] = "#1A2944",
            ["ButtonBackgroundBrush"] = "#233554",
            ["ButtonHoverBrush"] = "#304A6E",
            ["HoverBackgroundBrush"] = "#233554",
            ["InputBackgroundBrush"] = "#0E1C38",
            ["HeaderIconBrush"] = "#4A6A8A",
            ["PaceMarkerBrush"] = "#E6F1FF",
            ["AlternatingRowBackgroundBrush"] = "#0D1E36"
        }),

        ["copper"] = new ThemePreset("copper", "Copper", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#1C1410",
            ["PanelBackgroundBrush"] = "#2A1E18",
            ["CardBackgroundBrush"] = "#352820",
            ["HeaderBackgroundBrush"] = "#15100C",
            ["StatusBarBackgroundBrush"] = "#231A14",
            ["PrimaryTextBrush"] = "#F0E0D0",
            ["SecondaryTextBrush"] = "#A89080",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#F0E0D0",
            ["AccentBrush"] = "#D4875E",
            ["LightAccentBrush"] = "#20D4875E",
            ["SuccessBrush"] = "#8BC34A",
            ["DangerBrush"] = "#EF5350",
            ["CompBrush"] = "#8BC34A",
            ["BorderBrush"] = "#4A3828",
            ["SeparatorBrush"] = "#3A2C20",
            ["ButtonBackgroundBrush"] = "#4A3828",
            ["ButtonHoverBrush"] = "#5C4838",
            ["HoverBackgroundBrush"] = "#4A3828",
            ["InputBackgroundBrush"] = "#231A14",
            ["HeaderIconBrush"] = "#7A6050",
            ["PaceMarkerBrush"] = "#FFFFFF",
            ["AlternatingRowBackgroundBrush"] = "#221815"
        }),

        ["slate"] = new ThemePreset("slate", "Slate", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#0F172A",
            ["PanelBackgroundBrush"] = "#1E293B",
            ["CardBackgroundBrush"] = "#283548",
            ["HeaderBackgroundBrush"] = "#0B1120",
            ["StatusBarBackgroundBrush"] = "#162032",
            ["PrimaryTextBrush"] = "#E2E8F0",
            ["SecondaryTextBrush"] = "#94A3B8",
            ["HeaderTextBrush"] = "#F8FAFC",
            ["ButtonTextBrush"] = "#E2E8F0",
            ["AccentBrush"] = "#38BDF8",
            ["LightAccentBrush"] = "#2038BDF8",
            ["SuccessBrush"] = "#4ADE80",
            ["DangerBrush"] = "#F87171",
            ["CompBrush"] = "#4ADE80",
            ["BorderBrush"] = "#334155",
            ["SeparatorBrush"] = "#283548",
            ["ButtonBackgroundBrush"] = "#334155",
            ["ButtonHoverBrush"] = "#475569",
            ["HoverBackgroundBrush"] = "#334155",
            ["InputBackgroundBrush"] = "#162032",
            ["HeaderIconBrush"] = "#64748B",
            ["PaceMarkerBrush"] = "#F8FAFC",
            ["AlternatingRowBackgroundBrush"] = "#141E30"
        }),

        ["aurora"] = new ThemePreset("aurora", "Aurora", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#0C0E1A",
            ["PanelBackgroundBrush"] = "#151828",
            ["CardBackgroundBrush"] = "#1E2236",
            ["HeaderBackgroundBrush"] = "#080A14",
            ["StatusBarBackgroundBrush"] = "#101320",
            ["PrimaryTextBrush"] = "#E0E8FF",
            ["SecondaryTextBrush"] = "#8888BB",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#E0E8FF",
            ["AccentBrush"] = "#7C4DFF",
            ["LightAccentBrush"] = "#207C4DFF",
            ["SuccessBrush"] = "#00E676",
            ["DangerBrush"] = "#FF4081",
            ["CompBrush"] = "#00E676",
            ["BorderBrush"] = "#2A2E48",
            ["SeparatorBrush"] = "#1E2238",
            ["ButtonBackgroundBrush"] = "#2A2E48",
            ["ButtonHoverBrush"] = "#3A3E5A",
            ["HoverBackgroundBrush"] = "#2A2E48",
            ["InputBackgroundBrush"] = "#101320",
            ["HeaderIconBrush"] = "#5A5A8A",
            ["PaceMarkerBrush"] = "#FFFFFF",
            ["AlternatingRowBackgroundBrush"] = "#121520"
        }),

        ["ember"] = new ThemePreset("ember", "Ember", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#1A1010",
            ["PanelBackgroundBrush"] = "#281818",
            ["CardBackgroundBrush"] = "#322020",
            ["HeaderBackgroundBrush"] = "#140C0C",
            ["StatusBarBackgroundBrush"] = "#221414",
            ["PrimaryTextBrush"] = "#F0E0DD",
            ["SecondaryTextBrush"] = "#AA8888",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#F0E0DD",
            ["AccentBrush"] = "#FF5722",
            ["LightAccentBrush"] = "#20FF5722",
            ["SuccessBrush"] = "#CDDC39",
            ["DangerBrush"] = "#FF1744",
            ["CompBrush"] = "#CDDC39",
            ["BorderBrush"] = "#4A2828",
            ["SeparatorBrush"] = "#3A2020",
            ["ButtonBackgroundBrush"] = "#4A2828",
            ["ButtonHoverBrush"] = "#5C3838",
            ["HoverBackgroundBrush"] = "#4A2828",
            ["InputBackgroundBrush"] = "#221414",
            ["HeaderIconBrush"] = "#7A5050",
            ["PaceMarkerBrush"] = "#FFFFFF",
            ["AlternatingRowBackgroundBrush"] = "#201414"
        }),

        ["ice"] = new ThemePreset("ice", "Ice", true, new Dictionary<string, string>
        {
            ["WindowBackgroundBrush"] = "#101820",
            ["PanelBackgroundBrush"] = "#18222E",
            ["CardBackgroundBrush"] = "#202C3A",
            ["HeaderBackgroundBrush"] = "#0C1218",
            ["StatusBarBackgroundBrush"] = "#141E28",
            ["PrimaryTextBrush"] = "#D8EAF0",
            ["SecondaryTextBrush"] = "#88A0B0",
            ["HeaderTextBrush"] = "#FFFFFF",
            ["ButtonTextBrush"] = "#D8EAF0",
            ["AccentBrush"] = "#80DEEA",
            ["LightAccentBrush"] = "#2080DEEA",
            ["SuccessBrush"] = "#B2FF59",
            ["DangerBrush"] = "#FF5252",
            ["CompBrush"] = "#B2FF59",
            ["BorderBrush"] = "#2A3A4A",
            ["SeparatorBrush"] = "#1E2E3A",
            ["ButtonBackgroundBrush"] = "#2A3A4A",
            ["ButtonHoverBrush"] = "#3A4A5A",
            ["HoverBackgroundBrush"] = "#2A3A4A",
            ["InputBackgroundBrush"] = "#141E28",
            ["HeaderIconBrush"] = "#5A7A8A",
            ["PaceMarkerBrush"] = "#FFFFFF",
            ["AlternatingRowBackgroundBrush"] = "#141E28"
        })
    };

    /// <summary>
    /// Get a preset by key. Checks built-in presets first, then custom presets.
    /// Returns default_dark if not found.
    /// </summary>
    public static ThemePreset GetPreset(string key)
    {
        if (Presets.TryGetValue(key, out var preset))
            return preset;
        if (_customPresets.TryGetValue(key, out var custom))
            return custom;
        return Presets["default_dark"];
    }

    // Custom user-saved presets (loaded from settings)
    private static readonly Dictionary<string, ThemePreset> _customPresets = new();

    /// <summary>
    /// Load custom presets from saved settings data.
    /// </summary>
    public static void LoadCustomPresets(Dictionary<string, SavedTheme>? saved)
    {
        _customPresets.Clear();
        if (saved == null) return;

        foreach (var kvp in saved)
        {
            var key = kvp.Key;
            var theme = kvp.Value;
            _customPresets[key] = new ThemePreset(key, theme.DisplayName, theme.IsDark, new Dictionary<string, string>(theme.Colors));
        }
    }

    /// <summary>
    /// Save a custom preset.
    /// </summary>
    public static void SaveCustomPreset(string key, string displayName, bool isDark, Dictionary<string, string> colors)
    {
        _customPresets[key] = new ThemePreset(key, displayName, isDark, new Dictionary<string, string>(colors));
    }

    /// <summary>
    /// Delete a custom preset.
    /// </summary>
    public static bool DeleteCustomPreset(string key)
    {
        return _customPresets.Remove(key);
    }

    /// <summary>
    /// Get all custom presets for serialization.
    /// </summary>
    public static Dictionary<string, SavedTheme> GetCustomPresetsForSave()
    {
        var result = new Dictionary<string, SavedTheme>();
        foreach (var kvp in _customPresets)
        {
            result[kvp.Key] = new SavedTheme
            {
                DisplayName = kvp.Value.DisplayName,
                IsDark = kvp.Value.IsDark,
                Colors = new Dictionary<string, string>(kvp.Value.Colors)
            };
        }
        return result;
    }

    /// <summary>
    /// Check if a key is a built-in preset.
    /// </summary>
    public static bool IsBuiltIn(string key) => Presets.ContainsKey(key);

    /// <summary>
    /// Check if a key is a custom preset.
    /// </summary>
    public static bool IsCustom(string key) => _customPresets.ContainsKey(key);

    /// <summary>
    /// Get all presets (built-in + custom) as a flat list.
    /// </summary>
    public static IEnumerable<ThemePreset> GetAllPresets()
    {
        foreach (var kvp in Presets)
            yield return kvp.Value;
        foreach (var kvp in _customPresets)
            yield return kvp.Value;
    }
}

/// <summary>
/// Serializable custom theme for storage in settings YAML.
/// </summary>
public class SavedTheme
{
    public string DisplayName { get; set; } = "";
    public bool IsDark { get; set; } = true;
    public Dictionary<string, string> Colors { get; set; } = new();
}
