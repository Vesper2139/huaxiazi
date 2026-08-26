using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Huaxiazi.Services;
using Huaxiazi.Views;
using Xunit;

namespace Huaxiazi.Tests;

/// <summary>
/// 换肤回归：画刷令牌必须随皮肤字典整体替换，且元素级 {DynamicResource} 引用要重新解析。
/// 历史缺陷：画刷定义在 GlobalStyles 中、换肤只换 Color 令牌 → 画刷实例不就地更新 → "仅部分色块变化"。
/// </summary>
public sealed class ThemeSwapRegressionTests
{
    private static void LoadAppResources(Application app)
    {
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Huaxiazi;component/Resources/Themes/Dark.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Huaxiazi;component/Resources/Styles/GlobalStyles.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Huaxiazi;component/Resources/Icons/AppIcons.xaml", UriKind.Relative) });
    }

    [Fact]
    public void ApplySkin_SwapsBrushTokens_ToNewTheme()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = TestHelpers.EnsureWpfApplication();
                LoadAppResources(app);
                var skin = new SkinService(app.Resources);

                var darkBg = ((SolidColorBrush)app.FindResource("BgBrush")).Color;
                var darkGlass = ((SolidColorBrush)app.FindResource("GlassBrush")).Color;
                var darkInstance = (SolidColorBrush)app.FindResource("BgBrush");

                skin.ApplySkin("LightPaper");

                var lightBg = ((SolidColorBrush)app.FindResource("BgBrush")).Color;
                var lightGlass = ((SolidColorBrush)app.FindResource("GlassBrush")).Color;
                var lightInstance = (SolidColorBrush)app.FindResource("BgBrush");

                Assert.Equal(Color.FromRgb(0x10, 0x10, 0x14), darkBg);
                Assert.Equal(Color.FromRgb(0xF3, 0xF1, 0xEC), lightBg);
                Assert.Equal(Color.FromRgb(0xFF, 0xFE, 0xFB), lightGlass);

                // 换肤后画刷键解析到新皮肤字典的新实例（旧实例被孤立，这正是引用需用 DynamicResource 的原因）
                Assert.NotSame(darkInstance, lightInstance);
            }
            catch (Exception exception) { failure = exception; }
            finally { TestHelpers.ResetWpfApplication(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void MainWindow_ThemeSwap_ResolvesNewSkinInWindowScope()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = TestHelpers.EnsureWpfApplication();
                LoadAppResources(app);
                var skin = new SkinService(app.Resources);

                var window = new MainWindow();
                window.Show(); // 连接视觉树，验证窗口资源作用域随换肤解析新皮肤
                var windowBgBefore = ((SolidColorBrush)window.Background).Color;

                ThemeService.Apply(new Huaxiazi.Models.AppSettings { ThemeMode = "Light" }, skin, app.Resources);
                DoEvents(window.Dispatcher);
                window.UpdateLayout();

                // 窗口资源作用域必须解析到新皮肤的画刷 —— DynamicResource 表达式在真实消息循环下据此重解析
                var windowBgBrush = (SolidColorBrush)window.TryFindResource("BgBrush");
                var surfaceBrush = (SolidColorBrush)window.WindowSurface.TryFindResource("GlassBrush");

                Assert.Equal(Color.FromRgb(0x10, 0x10, 0x14), windowBgBefore);
                Assert.Equal(Color.FromRgb(0xF3, 0xF1, 0xEC), windowBgBrush.Color);
                Assert.Equal(Color.FromRgb(0xFF, 0xFE, 0xFB), surfaceBrush.Color);
                window.Close();
            }
            catch (Exception exception) { failure = exception; }
            finally { TestHelpers.ResetWpfApplication(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void MainWindow_ThemeSwap_UpdatesEffectiveBackgrounds_UnderMessageLoop()
    {
        // 真实消息循环：动态资源失效在 Dispatcher 空闲后被处理，验证窗口有效背景确实换肤。
        // 若此处断言失败，说明换肤机制在真实运行下也不生效 —— 那是必须修的回归。
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var app = TestHelpers.EnsureWpfApplication();
            LoadAppResources(app);
            var skin = new SkinService(app.Resources);
            var window = new MainWindow();
            window.Show();
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try
                {
                    ThemeService.Apply(new Huaxiazi.Models.AppSettings { ThemeMode = "Light" }, skin, app.Resources);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    window.Dispatcher.InvokeShutdown();
                    return;
                }
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        var bgAfter = ((SolidColorBrush)window.Background).Color;
                        var surfaceAfter = ((SolidColorBrush)window.WindowSurface.Background).Color;
                        Assert.Equal(Color.FromRgb(0xF3, 0xF1, 0xEC), bgAfter);
                        Assert.Equal(Color.FromRgb(0xFF, 0xFE, 0xFB), surfaceAfter);
                    }
                    catch (Exception exception) { failure = exception; }
                    window.Close();
                    window.Dispatcher.InvokeShutdown();
                }));
            }));
            System.Windows.Threading.Dispatcher.Run();
            TestHelpers.ResetWpfApplication();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    [Fact]
    public void ApplyPaletteToResources_WritesColorAndBrushTokens()
    {
        var resources = new ResourceDictionary();
        var palette = new ThemeService.ThemePalette(
            Colors.Yellow, Colors.Black, Colors.Black, Colors.White, Colors.White, Colors.White);

        ThemeService.ApplyPaletteToResources(resources, palette);

        Assert.Equal(Colors.Black, (Color)resources["BgColor"]);
        Assert.Equal(Colors.White, (Color)resources["TextColor"]);
        Assert.Equal(Colors.Yellow, (Color)resources["BrandColor"]);
        Assert.Equal(Colors.Black, ((SolidColorBrush)resources["BgBrush"]).Color);
        Assert.Equal(Colors.White, ((SolidColorBrush)resources["TextBrush"]).Color);
        Assert.Equal(Colors.Yellow, ((SolidColorBrush)resources["BrandBrush"]).Color);
    }

    private static void DoEvents(System.Windows.Threading.Dispatcher dispatcher)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}
