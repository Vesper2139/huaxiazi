using System;
using System.Windows;
using System.Windows.Media;
using PromptFloat.Models;

namespace PromptFloat.Services;

public static class ThemeService
{
    private const string ThemePathMarker = "/Resources/Themes/";
    private static readonly string[] LegacyThemeKeys =
    [
        "BrandColor", "BrandColorDark", "BrandHoverColor", "BgColor", "PanelColor", "TextColor",
        "BodyTextColor", "MutedColor", "DisabledTextColor", "BorderColor", "HighlightColor",
        "SuccessColor", "DangerColor", "GlassTopColor", "GlassBottomColor", "GlassBaseColor",
        "EditorFillColor", "CapsuleFillColor", "CapsuleBorderColor", "InputFillColor",
        "InputBorderColor", "FocusBorderColor", "GlassBorderColor", "HoverSurfaceColor",
        "SelectedSurfaceColor", "PopupColor", "DockColor", "CompanionSurfaceColor",
        "CompanionFaceColor", "CompanionAccentColor", "CompanionBorderColor", "CompanionGlowColor",
        "CompanionShadowColor"
    ];
    public readonly record struct ThemePalette(Color Brand, Color Background, Color Panel, Color Text, Color BodyText, Color Muted);

    public static ThemePalette ResolvePalette(AppSettings settings, bool systemIsLight, bool highContrast)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (highContrast) return new ThemePalette(Colors.Yellow, Colors.Black, Colors.Black, Colors.White, Colors.White, Colors.White);
        var isLight = string.Equals(settings.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(settings.ThemeMode, "System", StringComparison.OrdinalIgnoreCase) && systemIsLight);
        var brand = isLight ? Color.FromRgb(0x11, 0x12, 0x11) : Color.FromRgb(0xB4, 0x5C, 0xFF);
        return isLight
            ? new ThemePalette(brand, Color.FromRgb(0xF5, 0xF4, 0xF1), Color.FromRgb(0xFF, 0xFE, 0xFB), Color.FromRgb(0x20, 0x22, 0x1F), Color.FromRgb(0x35, 0x38, 0x34), Color.FromRgb(0x77, 0x7B, 0x75))
            : new ThemePalette(brand, Color.FromRgb(0x10, 0x10, 0x14), Color.FromRgb(0x16, 0x15, 0x1B), Color.FromRgb(0xF4, 0xF1, 0xF7), Color.FromRgb(0xDF, 0xDB, 0xE4), Color.FromRgb(0x99, 0x93, 0xA0));
    }

    public static void Apply(AppSettings settings)
    {
        var resources = Application.Current.Resources;
        var highContrast = SystemParameters.HighContrast;
        var systemIsLight = IsSystemLight();
        var palette = ResolvePalette(settings, systemIsLight, highContrast);
        var isLight = !highContrast && (string.Equals(settings.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(settings.ThemeMode, "System", StringComparison.OrdinalIgnoreCase) && systemIsLight));
        ApplySkinDictionary(resources, isLight ? "Light" : "Dark");
        resources["EditorFontSize"] = settings.EditorFontSize;
        resources["EditorDefaultHeight"] = settings.EditorDefaultHeight;
        resources["UiScale"] = settings.UiScale;
        resources["AnimationsEnabled"] = settings.AnimationsEnabled;
        if (highContrast)
        {
            resources["BrandColor"] = palette.Brand;
            resources["BgColor"] = palette.Background;
            resources["PanelColor"] = palette.Panel;
            resources["TextColor"] = palette.Text;
            resources["BodyTextColor"] = palette.BodyText;
            resources["MutedColor"] = palette.Muted;
            resources["CompanionSurfaceColor"] = Colors.Black;
            resources["CompanionFaceColor"] = Colors.White;
            resources["CompanionAccentColor"] = Colors.Yellow;
        }
    }

    private static void ApplySkinDictionary(ResourceDictionary resources, string skinName)
    {
        foreach (var key in LegacyThemeKeys) resources.Remove(key);

        var source = new Uri($"/Huaxiazi;component/Resources/Themes/{skinName}.xaml", UriKind.Relative);
        var existingIndex = -1;
        for (var index = 0; index < resources.MergedDictionaries.Count; index++)
        {
            if (resources.MergedDictionaries[index].Source?.OriginalString.Contains(ThemePathMarker, StringComparison.OrdinalIgnoreCase) == true)
            {
                existingIndex = index;
                break;
            }
        }

        var skin = new ResourceDictionary { Source = source };
        if (existingIndex >= 0) resources.MergedDictionaries[existingIndex] = skin;
        else resources.MergedDictionaries.Insert(0, skin);
    }

    private static Color Mix(Color source, Color target, double amount)
    {
        static byte Channel(byte source, byte target, double amount) =>
            (byte)Math.Round(source + ((target - source) * amount));

        return Color.FromRgb(
            Channel(source.R, target.R, amount),
            Channel(source.G, target.G, amount),
            Channel(source.B, target.B, amount));
    }

    private static bool IsSystemLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 0), System.Globalization.CultureInfo.InvariantCulture) == 1;
        }
        catch { return false; }
    }
}
