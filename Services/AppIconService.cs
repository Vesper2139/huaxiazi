using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Huaxiazi.Services;

public static class AppIconService
{
    // 注意：单文件发布下 AppContext.BaseDirectory 是宿主 EXE 所在目录而非解包目录，
    // 必须走 AppPaths.ContentRoot 才能在两种发布形态下都找到随包图标。
    public static string IconPath => Path.Combine(AppPaths.ContentRoot, "Resources", "Brand", "Huaxiazi.ico");

    public static string ResolveIconPath(string? skinId = null)
    {
        var activeSkinId = skinId;
        if (string.IsNullOrWhiteSpace(activeSkinId))
            activeSkinId = System.Windows.Application.Current?.TryFindResource("ThemeId") as string;

        if (!string.IsNullOrWhiteSpace(activeSkinId))
        {
            var skin = App.SkinService.GetSkin(activeSkinId);
            if (!string.IsNullOrWhiteSpace(skin?.AppIconPath))
            {
                var skinPath = Path.Combine(AppPaths.ContentRoot,
                    skin.AppIconPath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(skinPath)) return skinPath;
            }
        }

        return IconPath;
    }

    public static Icon LoadTrayIcon(string? skinId = null)
    {
        var iconPath = ResolveIconPath(skinId);
        if (File.Exists(iconPath))
        {
            using var icon = new Icon(iconPath);
            return (Icon)icon.Clone();
        }

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            using var extracted = Icon.ExtractAssociatedIcon(executable);
            if (extracted is not null) return (Icon)extracted.Clone();
        }
        return (Icon)SystemIcons.Application.Clone();
    }

    /// <summary>
    /// 加载窗口图标。窗口构造期间调用，必须保证“找不到素材”也不会抛异常：
    /// 单文件发布若把 EXE 单独放在空目录中，随包图标会被解包到临时目录，
    /// 旧实现直接对不存在的路径构造 BitmapImage，会抛 FileNotFoundException，
    /// 导致 FloatingBallWindow / MainWindow 构造失败，表现为“程序启动后没有任何窗口”。
    /// </summary>
    public static ImageSource LoadWindowIcon(string? skinId = null)
    {
        var path = ResolveIconPath(skinId);
        if (File.Exists(path))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                // 图标损坏（截断/被占用/权限不足）时继续走内嵌图标兜底。
            }
        }

        // 兜底一：宿主 EXE 自身内嵌的应用图标（ApplicationIcon 已写入 exe）。
        try
        {
            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
            {
                using var extracted = Icon.ExtractAssociatedIcon(executable);
                if (extracted is not null) return CreateImageSource(extracted);
            }
        }
        catch (Exception)
        {
            // 忽略，继续使用系统默认图标。
        }

        // 兜底二：系统默认应用图标。窗口有图标总好过启动失败。
        return CreateImageSource(SystemIcons.Application);
    }

    private static ImageSource CreateImageSource(Icon icon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }
}
