using System.Windows.Media;

namespace Huaxiazi.Services;

/// <summary>
/// Settings-page preview tokens. They are immutable per skin so one selected skin
/// cannot recolor every other card through DynamicResource lookup.
/// </summary>
public sealed class SkinPreviewPalette
{
    private SkinPreviewPalette(Color surface, Color border, Color panel, Color face, Color muted, Color accent)
    {
        Surface = surface;
        Border = border;
        Panel = panel;
        Face = face;
        Muted = muted;
        Accent = accent;
        SurfaceBrush = CreateBrush(surface);
        BorderBrush = CreateBrush(border);
        PanelBrush = CreateBrush(panel);
        FaceBrush = CreateBrush(face);
        MutedBrush = CreateBrush(muted);
        AccentBrush = CreateBrush(accent);
    }

    public Color Surface { get; }
    public Color Border { get; }
    public Color Panel { get; }
    public Color Face { get; }
    public Color Muted { get; }
    public Color Accent { get; }
    public SolidColorBrush SurfaceBrush { get; }
    public SolidColorBrush BorderBrush { get; }
    public SolidColorBrush PanelBrush { get; }
    public SolidColorBrush FaceBrush { get; }
    public SolidColorBrush MutedBrush { get; }
    public SolidColorBrush AccentBrush { get; }

    public static SkinPreviewPalette ForSkin(string? skinId) => skinId?.ToLowerInvariant() switch
    {
        "luoxiaohei" => new(
            Color.FromRgb(0xD8, 0xD3, 0xB9), Color.FromRgb(0x4A, 0x3B, 0x2D),
            Color.FromRgb(0xE9, 0xE5, 0xD1), Color.FromRgb(0xF4, 0xF0, 0xDE),
            Color.FromRgb(0x75, 0x6C, 0x5F), Color.FromRgb(0xA9, 0xB4, 0x51)),
        "maodie" => new(
            Color.FromRgb(0xD5, 0x8F, 0x4B), Color.FromRgb(0x8A, 0x5B, 0x32),
            Color.FromRgb(0xF5, 0xEB, 0xD7), Color.FromRgb(0x3A, 0x28, 0x18),
            Color.FromRgb(0x85, 0x74, 0x61), Color.FromRgb(0x68, 0x70, 0x44)),
        "yongweixiaofei" => new(
            Color.FromRgb(0xF1, 0xB6, 0xC8), Color.FromRgb(0xB9, 0x4D, 0x70),
            Color.FromRgb(0xFF, 0xF9, 0xFA), Color.FromRgb(0xFF, 0xF9, 0xF3),
            Color.FromRgb(0x9B, 0x71, 0x80), Color.FromRgb(0xD7, 0x5F, 0x83)),
        "starsailor" => new(
            Color.FromRgb(0xD8, 0xC8, 0xF6), Color.FromRgb(0x4F, 0x3A, 0x85),
            Color.FromRgb(0xFC, 0xFA, 0xFF), Color.FromRgb(0xFF, 0xF9, 0xEF),
            Color.FromRgb(0x82, 0x76, 0x9D), Color.FromRgb(0x4B, 0x8E, 0xF4)),
        "bluewhalemaid" => new(
            Color.FromRgb(0x91, 0xCE, 0xFF), Color.FromRgb(0x24, 0x56, 0xA2),
            Color.FromRgb(0xF7, 0xFB, 0xFF), Color.FromRgb(0xFD, 0xFE, 0xFF),
            Color.FromRgb(0x69, 0x85, 0xA5), Color.FromRgb(0x3C, 0x8D, 0xE8)),
        _ => Neutral
    };

    public static SkinPreviewPalette Neutral { get; } = new(
        Color.FromRgb(0xF3, 0xF1, 0xEC), Color.FromRgb(0xD2, 0xD0, 0xC9),
        Color.FromRgb(0xFF, 0xFE, 0xFB), Color.FromRgb(0x11, 0x12, 0x11),
        Color.FromRgb(0x77, 0x7B, 0x75), Color.FromRgb(0x62, 0x6B, 0x3E));

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
