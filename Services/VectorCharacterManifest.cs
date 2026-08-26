using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>纯数据矢量角色清单；不执行任何包内代码。</summary>
public sealed class VectorCharacterManifest
{
    public int SchemaVersion { get; init; }
    public VectorCanvas Canvas { get; init; } = new();
    public VectorAnchor Anchor { get; init; } = new();
    public List<VectorLayerDefinition> Layers { get; init; } = new();
    public Dictionary<string, VectorAnimationClip> Animations { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static VectorCharacterManifest Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var manifest = JsonSerializer.Deserialize<VectorCharacterManifest>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("矢量角色清单无法解析。");
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("不支持的矢量角色清单版本。");
        if (Canvas.Width != 88 || Canvas.Height != 88)
            throw new InvalidDataException("矢量角色画布必须为 88×88。 ");
        if (Layers.Count is < 4 or > 32) throw new InvalidDataException("矢量角色图层数量无效。");
        if (Layers.Select(layer => layer.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Layers.Count)
            throw new InvalidDataException("矢量角色图层 ID 重复。");
        foreach (var layer in Layers) layer.Validate(Canvas);
        foreach (var state in Enum.GetNames<CompanionVisualState>())
        {
            if (!Animations.TryGetValue(state, out var clip))
                throw new InvalidDataException($"矢量角色缺少 {state} 动画。");
            clip.Validate(state);
        }
        if (Anchor.X is < 0 or > 88 || Anchor.Y is < 0 or > 88)
            throw new InvalidDataException("矢量角色锚点越界。");
    }
}

public sealed class VectorCanvas
{
    public double Width { get; init; }
    public double Height { get; init; }
}

public sealed class VectorAnchor
{
    public double X { get; init; }
    public double Y { get; init; }
}

public sealed class VectorLayerDefinition
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "path";
    public string? Path { get; init; }
    public double[]? Bounds { get; init; }
    public string? Fill { get; init; }
    public string? Stroke { get; init; }
    public double StrokeWidth { get; init; }
    public bool Gaze { get; init; }

    public void Validate(VectorCanvas canvas)
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 48) throw new InvalidDataException("矢量图层 ID 无效。");
        if (!string.Equals(Type, "path", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Type, "ellipse", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"不支持的矢量图层类型：{Type}。");
        if (string.Equals(Type, "path", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(Path) || Path.Length > 4096))
            throw new InvalidDataException($"图层 {Id} 的路径数据无效。");
        if (string.Equals(Type, "path", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bounds = Geometry.Parse(Path!).Bounds;
                if (bounds.Left < -2 || bounds.Top < -2 || bounds.Right > 90 || bounds.Bottom > 90)
                    throw new InvalidDataException($"图层 {Id} 越过最大外扩范围。");
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException($"图层 {Id} 的路径数据无法解析。", exception);
            }
        }
        if (string.Equals(Type, "ellipse", StringComparison.OrdinalIgnoreCase) &&
            (Bounds is null || Bounds.Length != 4))
            throw new InvalidDataException($"图层 {Id} 的椭圆边界无效。");
        if (Bounds is not null && (Bounds.Length != 4 || Bounds.Any(value => value < -2 || value > 90)))
            throw new InvalidDataException($"图层 {Id} 越过最大外扩范围。");
        if (StrokeWidth < 0 || StrokeWidth > 8) throw new InvalidDataException($"图层 {Id} 线宽无效。");
    }
}

public sealed class VectorAnimationClip
{
    public int DurationMs { get; init; }
    public bool Loop { get; init; }
    public List<VectorKeyframe> Frames { get; init; } = new();

    public void Validate(string state)
    {
        if (DurationMs is < 80 or > 4000) throw new InvalidDataException($"{state} 动画时长无效。");
        if (Frames.Count < 3) throw new InvalidDataException($"{state} 动画至少需要首帧、过渡帧和尾帧。");
        if (Frames[0].TimeMs != 0 || Frames[^1].TimeMs != DurationMs)
            throw new InvalidDataException($"{state} 动画首尾帧时间不匹配。");
        for (var i = 1; i < Frames.Count; i++)
            if (Frames[i].TimeMs <= Frames[i - 1].TimeMs) throw new InvalidDataException($"{state} 动画关键帧时间未递增。");
    }
}

public sealed class VectorKeyframe
{
    public int TimeMs { get; init; }
    public double Scale { get; init; } = 1;
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }
    public double Rotation { get; init; }
    public double EyeScale { get; init; } = 1;
    public string? MouthPath { get; init; }
}
