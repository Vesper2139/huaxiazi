using System.Threading;
using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ThemeServiceTests
{
    [Theory]
    [InlineData("Light", true, 0x11, 0x12, 0x11)]
    [InlineData("Dark", false, 0xB4, 0x5C, 0xFF)]
    public void ResolvePalette_UsesTheTwoCompanionPalettes(
        string mode, bool systemIsLight, byte r, byte g, byte b)
    {
        var palette = ThemeService.ResolvePalette(
            new AppSettings { ThemeMode = mode }, systemIsLight, false);

        Assert.Equal(Color.FromRgb(r, g, b), palette.Brand);
    }

    [Fact]
    public void ResolvePalette_SystemModeUsesSystemThemeSignal()
    {
        var light = ThemeService.ResolvePalette(new AppSettings { ThemeMode = "System" }, true, false);
        var dark = ThemeService.ResolvePalette(new AppSettings { ThemeMode = "System" }, false, false);

        Assert.NotEqual(light.Background, dark.Background);
    }

    [Fact]
    public void ResolvePalette_HighContrastUsesReadableSystemColors()
    {
        var palette = ThemeService.ResolvePalette(new AppSettings { ThemeMode = "Dark" }, false, true);

        Assert.Equal(Colors.Black, palette.Background);
        Assert.Equal(Colors.White, palette.Text);
        Assert.Equal(Colors.Yellow, palette.Brand);
    }

    [Fact]
    public void SkinService_RegistersBuiltInSkins()
    {
        var service = new SkinService();

        Assert.Equal(7, service.AvailableSkins.Count);
        Assert.NotNull(service.GetSkin("LightPaper"));
        Assert.NotNull(service.GetSkin("DarkNocturne"));
        Assert.NotNull(service.GetSkin("LuoXiaoHei"));
        Assert.NotNull(service.GetSkin("MaoDie"));
        Assert.NotNull(service.GetSkin("YongWeiXiaoFei"));
        Assert.NotNull(service.GetSkin("StarSailor"));
        Assert.NotNull(service.GetSkin("BlueWhaleMaid"));
        Assert.NotNull(service.GetSkin("lightpaper")); // case-insensitive
    }

    [Fact]
    public void SkinService_RegistersReferenceSkinsWithIndependentStatesAndAppIcons()
    {
        var service = new SkinService();

        var pink = service.GetSkin("YongWeiXiaoFei");
        var sailor = service.GetSkin("StarSailor");
        var maid = service.GetSkin("BlueWhaleMaid");

        Assert.All(new[] { pink, sailor, maid }, skin =>
        {
            Assert.NotNull(skin);
            Assert.Equal("image", skin!.CompanionKind);
            Assert.True(skin.CompanionProfile.HasCompleteStateSet);
            Assert.Null(skin.CompanionSpriteSheetPath);
            Assert.False(string.IsNullOrWhiteSpace(skin.AppIconPath));
        });
    }

    [Fact]
    public void NewSkinDisplayNames_MatchApprovedNames()
    {
        var service = new SkinService();

        Assert.Equal("菲比啾比", service.GetSkin("StarSailor")!.DisplayName);
        Assert.Equal("蓝色大肥鱼", service.GetSkin("BlueWhaleMaid")!.DisplayName);
    }

    [Fact]
    public void AllCharacterSkins_SeparateAppIconFromFloatingBallMaterial()
    {
        var service = new SkinService();

        foreach (var skinId in new[] { "LuoXiaoHei", "MaoDie", "YongWeiXiaoFei", "StarSailor", "BlueWhaleMaid" })
        {
            var skin = service.GetSkin(skinId)!;

            Assert.False(string.IsNullOrWhiteSpace(skin.AppIconPath), skinId);
            Assert.True(skin.CompanionProfile.HasCompleteStateSet, skinId);
            Assert.Null(skin.CompanionSpriteSheetPath);
            Assert.All(Enum.GetValues<CompanionVisualState>(), state =>
            {
                Assert.True(skin.CompanionProfile.TryGetAsset(state, out var stateAsset));
                Assert.NotEqual(skin.AppIconPath, stateAsset);
            });
        }
    }

    [Fact]
    public void SkinService_SpecialSkinsUseIndependentStateProfiles()
    {
        var service = new SkinService();
        var skin = service.GetSkin("LuoXiaoHei");

        Assert.NotNull(skin);
        Assert.Equal("image", skin!.CompanionKind);
        Assert.True(skin.CompanionProfile.HasCompleteStateSet);
        Assert.True(skin.CompanionProfile.TryGetAsset(CompanionVisualState.Idle, out var idleAsset));
        Assert.Equal("Resources/Skins/LuoXiaoHei/States/Idle.png", idleAsset);
        Assert.Null(skin.CompanionSpriteSheetPath);
        Assert.Null(skin.CompanionIdleVariantsPath);
    }

    [Fact]
    public void SkinService_AllowsCustomSkinRegistration()
    {
        var service = new SkinService();
        service.Register(new SkinManifest("Custom", "自定义皮肤", "Resources/Themes/Custom.xaml"));

        Assert.Equal(8, service.AvailableSkins.Count);
        var custom = service.GetSkin("Custom");
        Assert.NotNull(custom);
        Assert.Equal("自定义皮肤", custom.DisplayName);
    }

    [Theory]
    [InlineData("LuoXiaoHei", "Light", true, "LuoXiaoHei")]
    [InlineData("MaoDie", "Dark", false, "MaoDie")]
    [InlineData("default", "Light", false, "LightPaper")]
    [InlineData("default", "Dark", true, "DarkNocturne")]
    public void ResolveSkinId_SpecialSkinIsIndependentFromDefaultTheme(
        string skinId, string themeMode, bool systemIsLight, string expected)
    {
        var actual = ThemeService.ResolveSkinId(
            new AppSettings { SkinId = skinId, ThemeMode = themeMode }, systemIsLight, false);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Apply_KeepsSelectedSkinWhileThemeModeChangesTheUiPalette()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = TestHelpers.EnsureWpfApplication();
                app.Resources.MergedDictionaries.Clear();
                var settings = new AppSettings { SkinId = "MaoDie", ThemeMode = "Light" };
                var service = new SkinService(app.Resources);

                ThemeService.Apply(settings, service, app.Resources);
                var lightBackground = Assert.IsType<Color>(app.Resources["BgColor"]);
                Assert.Equal("MaoDie", app.Resources["ThemeId"]);

                settings.ThemeMode = "Dark";
                ThemeService.Apply(settings, service, app.Resources);
                var darkBackground = Assert.IsType<Color>(app.Resources["BgColor"]);

                Assert.NotEqual(lightBackground, darkBackground);
                Assert.Equal("MaoDie", app.Resources["ThemeId"]);
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
    public void ThemeService_SelectsCorrectSkinIdForEachMode()
    {
        // Light mode → LightPaper
        var lightSettings = new AppSettings { ThemeMode = "Light" };
        var lightIsLight = ThemeService.ResolvePalette(lightSettings, true, false);
        Assert.Equal(Color.FromRgb(0xF5, 0xF4, 0xF1), lightIsLight.Background);

        // Dark mode → DarkNocturne
        var darkSettings = new AppSettings { ThemeMode = "Dark" };
        var darkIsLight = ThemeService.ResolvePalette(darkSettings, false, false);
        Assert.Equal(Color.FromRgb(0x10, 0x10, 0x14), darkIsLight.Background);

        // System mode follows system preference
        var sysLight = ThemeService.ResolvePalette(new AppSettings { ThemeMode = "System" }, true, false);
        var sysDark = ThemeService.ResolvePalette(new AppSettings { ThemeMode = "System" }, false, false);
        Assert.NotEqual(sysLight.Background, sysDark.Background);
    }

    [Fact]
    public void SkinService_SwapsTheme_ChangesResolvedColors_AndDoesNotGrowDictionary()
    {
        // 自包含：在 STA 线程上经 Application 解析皮肤字典 pack URI（裸 ResourceDictionary
        // 依赖全局 Application 状态，整跑时顺序变化会偶发 "URI prefix is not recognized"）。
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = TestHelpers.EnsureWpfApplication();
                app.Resources.MergedDictionaries.Clear();
                var skin = new SkinService(app.Resources);

                skin.ApplySkin("DarkNocturne");
                var dark = (Color)app.Resources["BgColor"];

                skin.ApplySkin("LightPaper");
                var light = (Color)app.Resources["BgColor"];

                Assert.NotEqual(dark, light);
                Assert.Equal(Color.FromRgb(0xF3, 0xF1, 0xEC), light);
                Assert.Equal(Color.FromRgb(0x10, 0x10, 0x14), dark);

                // 反复切换后，皮肤字典仍只有一份（不会因为前缀不匹配退化为 Insert 导致集合膨胀）
                for (var i = 0; i < 4; i++)
                {
                    skin.ApplySkin(i % 2 == 0 ? "LightPaper" : "DarkNocturne");
                }
                var themeDictCount = app.Resources.MergedDictionaries
                    .Count(d => d.Source is not null &&
                                d.Source.OriginalString.Contains("Themes", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(1, themeDictCount);
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
    public void Apply_MissingImportedSkinFallsBackToTheSavedDefaultMode()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = TestHelpers.EnsureWpfApplication();
                app.Resources.MergedDictionaries.Clear();
                var settings = new AppSettings { SkinId = "missing-skin", ThemeMode = "Light" };
                ThemeService.Apply(settings, new SkinService(app.Resources), app.Resources);

                Assert.Equal("default", settings.SkinId);
                Assert.Equal("LightPaper", app.Resources["ThemeId"]);
            }
            catch (Exception exception) { failure = exception; }
            finally { TestHelpers.ResetWpfApplication(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
