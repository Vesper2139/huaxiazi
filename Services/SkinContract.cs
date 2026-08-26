using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>
/// 所有皮肤共同遵守的视觉契约。业务控件树和命令不属于皮肤；皮肤只能提供
/// 语义颜色、受约束的组件度量以及完整的角色状态资源。
/// </summary>
public static class SkinContract
{
    public static IReadOnlyList<string> CompanionStates { get; } =
        Enum.GetNames<CompanionVisualState>();

    public static IReadOnlyList<string> RequiredThemeTokens { get; } = new[]
    {
        "BrandColor", "BgColor", "PanelColor", "TextColor", "BodyTextColor", "MutedColor",
        "DisabledTextColor", "BorderColor", "EditorFillColor", "InputFillColor", "InputBorderColor",
        "FocusBorderColor", "DockColor", "CompanionSurfaceColor", "CompanionFaceColor",
        "CompanionAccentColor", "CompanionBorderColor", "WindowRadius", "InputRadius",
        "ButtonRadius", "SmallRadius", "CardRadius", "SkinCompanionSize", "SkinTitleBarHeight",
        "SkinToolButtonSize", "SkinIconStrokeWidth", "SkinControlHeight", "SkinContentSpacing",
        "SkinCompanionOverscan", "SkinCompanionOffsetY"
    };

    private static readonly IReadOnlyDictionary<string, (double Min, double Max)> MetricRanges =
        new Dictionary<string, (double, double)>(StringComparer.Ordinal)
        {
            ["SkinCompanionSize"] = (40, 48),
            ["SkinTitleBarHeight"] = (26, 54),
            ["SkinToolButtonSize"] = (24, 30),
            ["SkinIconStrokeWidth"] = (1.2, 1.8),
            ["SkinControlHeight"] = (28, 38),
            ["SkinContentSpacing"] = (6, 16)
            , ["SkinCompanionOverscan"] = (1.05, 1.25)
            , ["SkinCompanionOffsetY"] = (-20, 8)
        };

    public static void ValidateTheme(Stream jsonStream)
    {
        using var document = JsonDocument.Parse(jsonStream);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("皮肤主题令牌必须是 JSON 对象。");

        var root = document.RootElement;
        var missing = RequiredThemeTokens.Where(token => !root.TryGetProperty(token, out _)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"皮肤缺少共享语义令牌：{string.Join(", ", missing)}。");

        foreach (var token in RequiredThemeTokens.Where(token => token.EndsWith("Color", StringComparison.Ordinal)))
        {
            if (root.GetProperty(token).ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"颜色令牌 {token} 必须是字符串。");
            try { _ = (Color)ColorConverter.ConvertFromString(root.GetProperty(token).GetString()!)!; }
            catch (FormatException) { throw new InvalidDataException($"颜色令牌 {token} 无效。"); }
        }

        foreach (var (token, range) in MetricRanges)
        {
            if (!root.GetProperty(token).TryGetDouble(out var value) || value < range.Min || value > range.Max)
                throw new InvalidDataException($"组件度量 {token} 必须位于 {range.Min}–{range.Max}。皮肤不能改变业务布局范式。");
        }
    }
}
