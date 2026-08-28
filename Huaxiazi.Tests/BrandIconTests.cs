using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Huaxiazi.Services;
using Huaxiazi.Views;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class BrandIconTests
{
    [Fact]
    public void WindowsAndShortcut_UseTheSharedBrandArtworkWhileNavigationUsesTheCompanion()
    {
        var root = RepoRoot();
        var mainWindow = File.ReadAllText(Path.Combine(root, "Views", "MainWindow.xaml"));
        var project = File.ReadAllText(Path.Combine(root, "Huaxiazi.csproj"));
        var installer = File.ReadAllText(Path.Combine(root, "deploy", "install.ps1"));

        Assert.Contains("x:Name=\"CompanionHost\"", mainWindow);
        Assert.DoesNotContain("NavigationBrandIcon", mainWindow);
        Assert.Contains("<ApplicationIcon>Resources\\Brand\\Huaxiazi.ico</ApplicationIcon>", project);
        Assert.Contains("$lnk.IconLocation", installer);
    }

    [Fact]
    public void WindowsIcon_ContainsAllRequiredShellAndHighDpiSizes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Brand", "Huaxiazi.ico");
        Assert.True(File.Exists(path));

        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal((ushort)1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        var sizes = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            sizes.Add(width == 0 ? 256 : width);
            Assert.Equal(width, height);
            stream.Position += 14;
        }

        Assert.True(new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 }.All(sizes.Contains));
    }

    [Fact]
    public void WindowsIcon_UsesExplicitTransparencyMaskForShellRendering()
    {
        var path = Path.Combine(RepoRoot(), "Resources", "Brand", "Huaxiazi.ico");
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        Assert.Equal((ushort)0, reader.ReadUInt16());
        Assert.Equal((ushort)1, reader.ReadUInt16());
        var count = reader.ReadUInt16();
        for (var index = 0; index < count; index++)
        {
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadUInt16();
            reader.ReadUInt16();
            var length = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var returnPosition = stream.Position;
            stream.Position = offset;
            Assert.Equal((uint)40, reader.ReadUInt32());
            var dibWidth = reader.ReadInt32();
            var dibHeight = reader.ReadInt32();
            Assert.True(dibWidth is > 0 and <= 256);
            Assert.Equal(dibWidth * 2, dibHeight);
            Assert.Equal((ushort)1, reader.ReadUInt16());
            Assert.Equal((ushort)32, reader.ReadUInt16());
            Assert.True(length > 40);

            stream.Position = returnPosition;
        }

        using var master = new Bitmap(Path.Combine(RepoRoot(), "Resources", "Brand", "Huaxiazi-256.png"));
        foreach (var point in new[] { new System.Drawing.Point(0, 0), new System.Drawing.Point(master.Width - 1, 0), new System.Drawing.Point(0, master.Height - 1), new System.Drawing.Point(master.Width - 1, master.Height - 1) })
        {
            Assert.Equal(0, master.GetPixel(point.X, point.Y).A);
        }
    }

    [Fact]
    public void WindowsAndTray_UseTheConfiguredBrandIcon()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var application = TestHelpers.EnsureWpfApplication();
                application.Resources.MergedDictionaries.Clear();
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Huaxiazi;component/Resources/Themes/Dark.xaml", UriKind.Relative) });
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Huaxiazi;component/Resources/Styles/GlobalStyles.xaml", UriKind.Relative) });
                application.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Huaxiazi;component/Resources/Icons/AppIcons.xaml", UriKind.Relative) });
                using var trayIcon = AppIconService.LoadTrayIcon();
                Assert.True(trayIcon.Width >= 16);
                var main = new MainWindow();
                var ball = new FloatingBallWindow();
                Assert.NotNull(main.Icon);
                Assert.NotNull(ball.Icon);
                main.Close();
                ball.Close();
            }
            catch (Exception exception) { failure = exception; }
            finally { TestHelpers.ResetWpfApplication(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Huaxiazi.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Huaxiazi.csproj.");
    }
}
