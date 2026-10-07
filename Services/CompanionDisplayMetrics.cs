using System;
using System.Windows;

namespace Huaxiazi.Services;

/// <summary>一次解析出的完整角色显示几何，所有可视层必须共同消费。</summary>
public readonly record struct CompanionDisplayLayout(
    double WindowSize,
    double HitTargetSize,
    double ViewportSize);

/// <summary>
/// 悬浮球的唯一尺寸契约。展开态和收缩态都只消费这一组度量，皮肤只负责素材和视觉令牌。
/// </summary>
public static class CompanionDisplayMetrics
{
    public const double MinSize = 28;
    public const double MaxSize = 72;
    public const double DefaultSize = 44;

    public static double NormalizeSize(double size) =>
        double.IsFinite(size) ? Math.Clamp(size, MinSize, MaxSize) : DefaultSize;

    public static CompanionDisplayLayout ResolveLayout(double requestedSize)
    {
        var size = NormalizeSize(requestedSize);
        // Keep the historical transparent interaction shell 16 DIP larger than
        // the visual. The shell absorbs edge clicks and makes double-click/drag
        // reliable without changing the visible companion size.
        return new CompanionDisplayLayout(size + 16, size, size);
    }

    public static Point CenterPreservingOrigin(Point origin, double oldSize, double newSize)
    {
        var normalizedOldSize = double.IsFinite(oldSize) && oldSize > 0 ? oldSize : DefaultSize;
        var normalizedNewSize = double.IsFinite(newSize) && newSize > 0 ? newSize : DefaultSize;
        var center = new Point(origin.X + normalizedOldSize / 2, origin.Y + normalizedOldSize / 2);
        return new Point(center.X - normalizedNewSize / 2, center.Y - normalizedNewSize / 2);
    }

}
