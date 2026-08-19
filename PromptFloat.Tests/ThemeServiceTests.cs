using System.Windows.Media;
using System.Windows;
using PromptFloat.Models;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

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
    public void Apply_LightModeRecolorsSecondarySurfaces()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var application = Application.Current ?? new Application();
                application.Resources["GlassBaseColor"] = Color.FromRgb(0x0A, 0x16, 0x26);
                application.Resources["EditorFillColor"] = Color.FromRgb(0x15, 0x21, 0x2D);
                application.Resources["InputFillColor"] = Color.FromRgb(0x15, 0x21, 0x2D);

                ThemeService.Apply(new AppSettings { ThemeMode = "Light" });

                Assert.Equal(Color.FromRgb(0xFF, 0xFE, 0xFB), application.Resources["GlassBaseColor"]);
                Assert.Equal(Color.FromRgb(0xF7, 0xF5, 0xF0), application.Resources["EditorFillColor"]);
                Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFC), application.Resources["InputFillColor"]);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void Apply_SwitchingBetweenLightAndDarkRecolorsTheLiveResourceSet()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var application = Application.Current ?? new Application();

                ThemeService.Apply(new AppSettings { ThemeMode = "Light" });
                var lightBackground = Assert.IsType<Color>(application.Resources["BgColor"]);
                var lightText = Assert.IsType<Color>(application.Resources["TextColor"]);
                ThemeService.Apply(new AppSettings { ThemeMode = "Dark" });
                var darkBackground = Assert.IsType<Color>(application.Resources["BgColor"]);
                var darkText = Assert.IsType<Color>(application.Resources["TextColor"]);

                Assert.NotEqual(lightBackground, darkBackground);
                Assert.NotEqual(lightText, darkText);
                Assert.True(lightBackground.R > darkBackground.R);
                Assert.True(darkText.R > lightText.R);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void Apply_SwitchesOneCompleteSkinDictionaryIncludingShapeTokens()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var application = Application.Current ?? new Application();

                ThemeService.Apply(new AppSettings { ThemeMode = "Light" });
                var lightSource = Assert.Single(application.Resources.MergedDictionaries,
                    dictionary => dictionary.Source?.OriginalString.Contains("/Themes/", StringComparison.OrdinalIgnoreCase) == true).Source;
                var lightRadius = Assert.IsType<CornerRadius>(application.TryFindResource("WindowRadius"));
                var lightTheme = Assert.IsType<string>(application.TryFindResource("ThemeId"));

                ThemeService.Apply(new AppSettings { ThemeMode = "Dark" });
                var darkSource = Assert.Single(application.Resources.MergedDictionaries,
                    dictionary => dictionary.Source?.OriginalString.Contains("/Themes/", StringComparison.OrdinalIgnoreCase) == true).Source;
                var darkRadius = Assert.IsType<CornerRadius>(application.TryFindResource("WindowRadius"));
                var darkTheme = Assert.IsType<string>(application.TryFindResource("ThemeId"));

                Assert.EndsWith("/Themes/Light.xaml", lightSource.OriginalString, StringComparison.OrdinalIgnoreCase);
                Assert.EndsWith("/Themes/Dark.xaml", darkSource.OriginalString, StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual(lightTheme, darkTheme);
                Assert.NotEqual(lightRadius, darkRadius);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }
}
