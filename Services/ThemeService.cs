using System;
using System.Windows;
using System.Windows.Media;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>
/// 主题服务：解析系统主题偏好并委托 SkinService 完成整套皮肤字典替换。
/// 所有颜色令牌在皮肤字典中一次性定义，此处只负责"选哪套皮肤"。
/// </summary>
public static class ThemeService
{
    public static void Apply(AppSettings settings, SkinService? skinService = null, ResourceDictionary? resources = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // 不读取 Application.Current；由调用方传入明确的 ResourceDictionary，
        // 避免在非 UI 线程或测试上下文中取到错误的实例。
        if (resources is null) return;

        var highContrast = SystemParameters.HighContrast;
        var systemIsLight = IsSystemLight();
        var isLight = !highContrast && (
            string.Equals(settings.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(settings.ThemeMode, "System", StringComparison.OrdinalIgnoreCase) && systemIsLight));

        var skinId = ResolveSkinId(settings, systemIsLight, highContrast);
        if (skinService?.GetSkin(skinId) is null)
        {
            settings.SkinId = "default";
            skinId = ResolveSkinId(settings, systemIsLight, highContrast);
        }

        // 委托 SkinService 执行整套 ResourceDictionary 替换
        skinService?.ApplySkin(skinId, resources);

        // SkinId 只决定角色、皮肤令牌和品牌色；ThemeMode 独立决定界面的明暗层。
        // 必须在皮肤字典加载后覆盖共享界面令牌，否则选择角色皮肤会再次短路明暗设置。
        ApplyModePaletteToResources(resources, settings, systemIsLight, highContrast);

        // 动态资源在皮肤字典中已定义；此处只更新运行时参数。
        resources["EditorFontSize"] = settings.EditorFontSize;
        resources["EditorDefaultHeight"] = settings.EditorDefaultHeight;
        resources["UiScale"] = settings.UiScale;
        resources["AnimationsEnabled"] = settings.AnimationsEnabled;

        if (highContrast)
        {
            var palette = ResolvePalette(settings, systemIsLight, highContrast);
            ApplyPaletteToResources(resources, palette);
            // 精灵高对比配色：黑底、白脸、黄强调
            resources["CompanionSurfaceColor"] = Colors.Black;
            resources["CompanionFaceColor"] = Colors.White;
            resources["CompanionAccentColor"] = Colors.Yellow;
            resources["CompanionSurfaceBrush"] = new SolidColorBrush(Colors.Black);
            resources["CompanionFaceBrush"] = new SolidColorBrush(Colors.White);
            resources["CompanionAccentBrush"] = new SolidColorBrush(Colors.Yellow);
        }
    }

    internal static string ResolveSkinId(AppSettings settings, bool systemIsLight, bool highContrast)
    {
        if (highContrast) return "LightPaper";
        if (!string.IsNullOrWhiteSpace(settings.SkinId) &&
            !string.Equals(settings.SkinId, "default", StringComparison.OrdinalIgnoreCase))
            return settings.SkinId;

        var isLight = string.Equals(settings.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(settings.ThemeMode, "System", StringComparison.OrdinalIgnoreCase) && systemIsLight);
        return isLight ? "LightPaper" : "DarkNocturne";
    }

    /// <summary>
    /// 把一套调色板同时写入 Color 与 Brush 令牌。
    /// 画刷现为主题字典内的字面量、不再由 Color 令牌派生，高对比模式必须直接覆盖画刷键；
    /// App.Resources 的直属键优先于合并字典，可遮蔽皮肤字典。
    /// </summary>
    internal static void ApplyPaletteToResources(ResourceDictionary resources, ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(resources);
        resources["BrandColor"] = palette.Brand;
        resources["BgColor"] = palette.Background;
        resources["PanelColor"] = palette.Panel;
        resources["TextColor"] = palette.Text;
        resources["BodyTextColor"] = palette.BodyText;
        resources["MutedColor"] = palette.Muted;

        resources["BrandBrush"] = new SolidColorBrush(palette.Brand);
        resources["BgBrush"] = new SolidColorBrush(palette.Background);
        resources["PanelBrush"] = new SolidColorBrush(palette.Panel);
        resources["TextBrush"] = new SolidColorBrush(palette.Text);
        resources["BodyTextBrush"] = new SolidColorBrush(palette.BodyText);
        resources["MutedBrush"] = new SolidColorBrush(palette.Muted);
    }

    internal readonly record struct ThemePalette(Color Brand, Color Background, Color Panel, Color Text, Color BodyText, Color Muted);

    internal static ThemePalette ResolvePalette(AppSettings settings, bool systemIsLight, bool highContrast)
    {
        if (highContrast) return new ThemePalette(Colors.Yellow, Colors.Black, Colors.Black, Colors.White, Colors.White, Colors.White);
        var isLight = string.Equals(settings.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(settings.ThemeMode, "System", StringComparison.OrdinalIgnoreCase) && systemIsLight);
        var brand = isLight ? Color.FromRgb(0x11, 0x12, 0x11) : Color.FromRgb(0xB4, 0x5C, 0xFF);
        return isLight
            ? new ThemePalette(brand, Color.FromRgb(0xF5, 0xF4, 0xF1), Color.FromRgb(0xFF, 0xFE, 0xFB), Color.FromRgb(0x20, 0x22, 0x1F), Color.FromRgb(0x35, 0x38, 0x34), Color.FromRgb(0x77, 0x7B, 0x75))
            : new ThemePalette(brand, Color.FromRgb(0x10, 0x10, 0x14), Color.FromRgb(0x16, 0x15, 0x1B), Color.FromRgb(0xF4, 0xF1, 0xF7), Color.FromRgb(0xDF, 0xDB, 0xE4), Color.FromRgb(0x99, 0x93, 0xA0));
    }

    private readonly record struct ModePalette(
        Color Background,
        Color Panel,
        Color Text,
        Color BodyText,
        Color Muted,
        Color Border,
        Color Glass,
        Color Input,
        Color Popup,
        Color Dock,
        Color SwitchTrack);

    private static void ApplyModePaletteToResources(
        ResourceDictionary resources,
        AppSettings settings,
        bool systemIsLight,
        bool highContrast)
    {
        if (highContrast) return;
        var isLight = string.Equals(settings.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(settings.ThemeMode, "System", StringComparison.OrdinalIgnoreCase) && systemIsLight);
        var palette = isLight
            ? new ModePalette(
                Color.FromRgb(0xF5, 0xF4, 0xF1), Color.FromRgb(0xFF, 0xFE, 0xFB),
                Color.FromRgb(0x20, 0x22, 0x1F), Color.FromRgb(0x35, 0x38, 0x34),
                Color.FromRgb(0x77, 0x7B, 0x75), Color.FromRgb(0xD2, 0xD0, 0xC9),
                Color.FromRgb(0xF8, 0xF7, 0xF2), Color.FromRgb(0xFF, 0xFE, 0xFB),
                Color.FromRgb(0xFF, 0xFE, 0xFB), Color.FromRgb(0xEC, 0xEA, 0xDF),
                Color.FromRgb(0xAA, 0xA5, 0x96))
            : new ModePalette(
                Color.FromRgb(0x10, 0x10, 0x14), Color.FromRgb(0x16, 0x15, 0x1B),
                Color.FromRgb(0xF4, 0xF1, 0xF7), Color.FromRgb(0xDF, 0xDB, 0xE4),
                Color.FromRgb(0x99, 0x93, 0xA0), Color.FromRgb(0x36, 0x32, 0x3C),
                Color.FromRgb(0x1C, 0x1A, 0x20), Color.FromRgb(0x1D, 0x1B, 0x22),
                Color.FromRgb(0x1B, 0x19, 0x20), Color.FromRgb(0x1B, 0x19, 0x20),
                Color.FromRgb(0x3A, 0x35, 0x41));

        SetColorAndBrush(resources, "Bg", palette.Background);
        SetColorAndBrush(resources, "Panel", palette.Panel);
        SetColorAndBrush(resources, "Text", palette.Text);
        SetColorAndBrush(resources, "BodyText", palette.BodyText);
        SetColorAndBrush(resources, "Muted", palette.Muted);
        SetColorAndBrush(resources, "Border", palette.Border);
        SetColorAndBrush(resources, "Glass", palette.Glass);
        SetColorAndBrush(resources, "Input", palette.Input);
        SetColorAndBrush(resources, "Popup", palette.Popup);
        SetColorAndBrush(resources, "Dock", palette.Dock);
        SetColorAndBrush(resources, "SwitchTrack", palette.SwitchTrack);
        resources["ThemeMode"] = isLight ? "Light" : "Dark";
    }

    private static void SetColorAndBrush(ResourceDictionary resources, string name, Color color)
    {
        resources[$"{name}Color"] = color;
        resources[$"{name}Brush"] = new SolidColorBrush(color);
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
