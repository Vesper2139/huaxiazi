using System.Drawing;
using System.IO;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class FloatingBallSkinAssetTests
{
    [Theory]
    [InlineData("BlueWhaleMaid")]
    [InlineData("StarSailor")]
    [InlineData("YongWeiXiaoFei")]
    public void CompanionSpriteAssets_AreNormalizedTransparentSheets(string skinId)
    {
        var root = Path.Combine(RepoRoot(), "Resources", "Skins", skinId);
        var spriteSheet = Path.Combine(root, "companion-sprite-sheet.png");
        var idleVariants = Path.Combine(root, "companion-idle-variants.png");

        using var sheet = new Bitmap(spriteSheet);
        using var idle = new Bitmap(idleVariants);

        Assert.Equal(new Size(724, 543), sheet.Size);
        Assert.Equal(new Size(724, 181), idle.Size);
        Assert.Equal(0, sheet.GetPixel(0, 0).A);
        Assert.Equal(0, idle.GetPixel(0, 0).A);
    }

    [Theory]
    [InlineData("BlueWhaleMaid")]
    [InlineData("StarSailor")]
    [InlineData("YongWeiXiaoFei")]
    public void CompanionSpriteFrames_HaveCompleteSilhouetteSafetyMargins(string skinId)
    {
        var path = Path.Combine(RepoRoot(), "Resources", "Skins", skinId, "companion-sprite-sheet.png");
        using var sheet = new Bitmap(path);

        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 4; column++)
        {
            var bounds = AlphaBounds(sheet, new Rectangle(column * 181, row * 181, 181, 181));
            Assert.False(bounds.IsEmpty, $"{skinId} 的第 {row * 4 + column + 1} 个悬浮球状态为空。");
            Assert.InRange(bounds.Width, 1, 165);
            Assert.InRange(bounds.Height, 1, 165);
            Assert.InRange(bounds.Left, 8, 80);
            Assert.InRange(bounds.Top, 8, 80);
            Assert.InRange(181 - bounds.Right, 8, 80);
            Assert.InRange(181 - bounds.Bottom, 8, 80);
        }
    }

    private static Rectangle AlphaBounds(Bitmap image, Rectangle frame)
    {
        var bounds = Rectangle.Empty;
        for (var y = frame.Top; y < frame.Bottom; y++)
        for (var x = frame.Left; x < frame.Right; x++)
        {
            if (image.GetPixel(x, y).A < 16) continue;
            bounds = bounds.IsEmpty ? new Rectangle(x - frame.Left, y - frame.Top, 1, 1)
                : Rectangle.Union(bounds, new Rectangle(x - frame.Left, y - frame.Top, 1, 1));
        }
        return bounds;
    }

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "Huaxiazi.csproj")))
            directory = Directory.GetParent(directory)?.FullName
                ?? throw new DirectoryNotFoundException("未找到仓库根目录");
        return directory;
    }
}
