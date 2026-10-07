using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Huaxiazi.Views;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class FloatingBallDisplayContractTests
{
    [Fact]
    public void CenterPreservingOrigin_KeepsTheCompanionCenterWhenTheWindowIsHidden()
    {
        var origin = CompanionDisplayMetrics.CenterPreservingOrigin(
            new System.Windows.Point(100, 200), oldSize: 44, newSize: 72);

        Assert.Equal(new System.Windows.Point(86, 186), origin);
    }

    [Theory]
    [InlineData(28)]
    [InlineData(44)]
    [InlineData(72)]
    public void CollapsedCompanion_InteractionShellKeepsSixteenDipMarginAroundVisibleOrb(double requestedSize)
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var settings = new AppSettings { IncognitoMode = true, FloatingBallSize = requestedSize };
            App.ReplaceSettings(settings);
            var window = new FloatingBallWindow();

            window.ApplyDisplayPreferences(settings);

            var orb = Assert.IsType<System.Windows.Controls.Border>(window.FindName("Orb"));
            var viewport = Assert.IsType<System.Windows.Controls.Viewbox>(window.FindName("CompanionViewport"));
            Assert.Equal(requestedSize + 16, window.Width);
            Assert.Equal(requestedSize + 16, window.Height);
            Assert.Equal(requestedSize, orb.Width);
            Assert.Equal(requestedSize, orb.Height);
            Assert.Equal(requestedSize, viewport.Width);
            Assert.Equal(requestedSize, viewport.Height);
            window.Close();
        });
    }

    [Fact]
    public void CompanionImage_DoesNotRenderOutsideItsViewport()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            App.ReplaceSettings(new AppSettings { SkinId = "MaoDie", AnimationsEnabled = false, IncognitoMode = true });
            App.SkinService.ApplySkin("MaoDie", Application.Current!.Resources);
            var face = new CompanionFace();

            var sprite = Assert.IsType<System.Windows.Shapes.Rectangle>(face.FindName("SkinSpriteHost"));

            Assert.Equal(new Thickness(0), sprite.Margin);
        });
    }

    [Theory]
    [InlineData("LuoXiaoHei")]
    [InlineData("MaoDie")]
    [InlineData("YongWeiXiaoFei")]
    [InlineData("StarSailor")]
    [InlineData("BlueWhaleMaid")]
    public void CharacterSkinStatesUseAStableTargetBodySize(string skinId)
    {
        using var reader = new SkinStateAssetReader(skinId);
        foreach (var state in Enum.GetValues<CompanionVisualState>())
        {
            var bounds = reader.GetAlphaBounds(state);
            Assert.Equal(SkinContract.CompanionStateTargetContentSize, Math.Max(bounds.Width, bounds.Height));
        }
    }

    [Fact]
    public void FloatingBallSize_IsAppliedToExpandedAndCollapsedCompanion()
    {
        RunSta(() =>
        {
            EnsureApplicationResources();
            var settings = new AppSettings { IncognitoMode = true, FloatingBallSize = 64 };
            App.ReplaceSettings(settings);

            var mainWindow = new MainWindow();
            mainWindow.ApplyDisplayPreferences(settings);
            var expandedHandle = Assert.IsType<System.Windows.Controls.Border>(mainWindow.FindName("CompanionDragHandle"));
            Assert.Equal(64, expandedHandle.Width);
            Assert.Equal(64, expandedHandle.Height);

            var floatingBall = new FloatingBallWindow();
            floatingBall.ApplyDisplayPreferences(settings);
            var orb = Assert.IsType<System.Windows.Controls.Border>(floatingBall.FindName("Orb"));
            Assert.Equal(64, orb.Width);
            Assert.Equal(64, orb.Height);

            floatingBall.Close();
            mainWindow.Close();
        });
    }

    [Theory]
    [InlineData("LuoXiaoHei")]
    [InlineData("MaoDie")]
    [InlineData("YongWeiXiaoFei")]
    [InlineData("StarSailor")]
    [InlineData("BlueWhaleMaid")]
    public void CharacterSkin_UsesTwelveIndependentNormalizedStateAssets(string skinId)
    {
        var skin = new SkinService().GetSkin(skinId);
        Assert.NotNull(skin);
        Assert.Equal("image", skin!.CompanionKind);
        Assert.Null(skin.CompanionSpriteSheetPath);
        Assert.Null(skin.CompanionIdleVariantsPath);
        Assert.True(skin.CompanionProfile.HasCompleteStateSet);

        var root = Path.Combine(RepoRoot(), "Resources", "Skins", skinId);
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        Assert.All(Enum.GetValues<CompanionVisualState>(), state =>
        {
            Assert.True(skin.CompanionProfile.TryGetAsset(state, out var relativePath), $"{skinId} 缺少 {state} 映射。");
            Assert.Equal($"Resources/Skins/{skinId}/States/{state}.png", relativePath);
            var path = Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), path);
            using var image = new Bitmap(path);
            Assert.Equal(new System.Drawing.Size(SkinContract.CompanionStateImageSize, SkinContract.CompanionStateImageSize), image.Size);
            Assert.Equal(0, image.GetPixel(0, 0).A);
            var bounds = AlphaBounds(image, new Rectangle(0, 0, image.Width, image.Height));
            Assert.False(bounds.IsEmpty, $"{skinId} 的 {state} 状态素材为空。");
            Assert.True(bounds.Left >= SkinContract.CompanionStateSafetyMargin, $"{skinId} 的 {state} 左侧安全区不足。");
            Assert.True(bounds.Top >= SkinContract.CompanionStateSafetyMargin, $"{skinId} 的 {state} 顶部安全区不足。");
            Assert.True(bounds.Right <= image.Width - SkinContract.CompanionStateSafetyMargin, $"{skinId} 的 {state} 右侧安全区不足。");
            Assert.True(bounds.Bottom <= image.Height - SkinContract.CompanionStateSafetyMargin, $"{skinId} 的 {state} 底部安全区不足。");
            Assert.InRange(bounds.Width, 1, SkinContract.CompanionStateImageSize - SkinContract.CompanionStateSafetyMargin * 2);
            Assert.InRange(bounds.Height, 1, SkinContract.CompanionStateImageSize - SkinContract.CompanionStateSafetyMargin * 2);
            hashes.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        });
        Assert.Equal(SkinContract.CompanionStates.Count, hashes.Count);
    }

    private static Rectangle AlphaBounds(Bitmap image, Rectangle frame)
    {
        var bounds = Rectangle.Empty;
        for (var y = frame.Top; y < frame.Bottom; y++)
        for (var x = frame.Left; x < frame.Right; x++)
        {
            if (image.GetPixel(x, y).A < 16) continue;
            var pixel = new Rectangle(x - frame.Left, y - frame.Top, 1, 1);
            bounds = bounds.IsEmpty ? pixel : Rectangle.Union(bounds, pixel);
        }
        return bounds;
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { TestHelpers.ResetWpfApplication(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private static void EnsureApplicationResources()
    {
        var application = TestHelpers.EnsureWpfApplication();
        application.Resources.MergedDictionaries.Clear();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Huaxiazi;component/Resources/Themes/Dark.xaml", UriKind.Relative)
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Huaxiazi;component/Resources/Styles/GlobalStyles.xaml", UriKind.Relative)
        });
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Huaxiazi;component/Resources/Icons/AppIcons.xaml", UriKind.Relative)
        });
    }

    private static string RepoRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "Huaxiazi.csproj")))
            directory = Directory.GetParent(directory)?.FullName
                ?? throw new DirectoryNotFoundException("未找到仓库根目录");
        return directory;
    }

    private sealed class SkinStateAssetReader : IDisposable
    {
        private readonly string _root;
        public SkinStateAssetReader(string skinId) => _root = Path.Combine(RepoRoot(), "Resources", "Skins", skinId, "States");

        public Rectangle GetAlphaBounds(CompanionVisualState state)
        {
            using var image = new Bitmap(Path.Combine(_root, state + ".png"));
            var bounds = Rectangle.Empty;
            for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                if (image.GetPixel(x, y).A < 16) continue;
                bounds = bounds.IsEmpty ? new Rectangle(x, y, 1, 1) : Rectangle.Union(bounds, new Rectangle(x, y, 1, 1));
            }
            return bounds;
        }

        public void Dispose() { }
    }
}
