using System;
using System.IO;

namespace Huaxiazi.Services;

/// <summary>
/// 解析“随包内容”的根目录（Resources / Prompts / Presets / Config）。
///
/// 背景（.NET 5+ 官方行为变更）：
/// 单文件发布下 <see cref="AppContext.BaseDirectory"/> 返回的是**宿主 EXE 所在目录**，
/// 而不是自解压目录。因此把 Huaxiazi.exe 单独放在一个空目录里运行时，
/// 所有通过 AppContext.BaseDirectory 查找的随包文件都会“不存在”。
/// 本类在常规目录部署与单文件自解压两种形态下都能定位到内容目录。
/// </summary>
internal static class AppPaths
{
    private static readonly Lazy<string> ContentRootLazy = new(ResolveContentRoot, isThreadSafe: true);

    /// <summary>随包内容根目录；解析失败时回退到程序目录，绝不抛异常。</summary>
    internal static string ContentRoot => ContentRootLazy.Value;

    private static string ResolveContentRoot()
    {
        // 1) 常规形态：文件夹 / 便携包，内容就在程序目录。
        var baseDirectory = AppContext.BaseDirectory;
        if (HasContent(baseDirectory)) return baseDirectory;

        // 2) 单文件 + IncludeAllContentForSelfExtract：程序集被解包到磁盘，
        //    Assembly.Location 指向解包目录（非解包模式下为空字符串）。
        try
        {
            var location = typeof(AppPaths).Assembly.Location;
            if (!string.IsNullOrEmpty(location))
            {
                var assemblyDirectory = Path.GetDirectoryName(location);
                if (!string.IsNullOrEmpty(assemblyDirectory) && HasContent(assemblyDirectory))
                    return assemblyDirectory;
            }
        }
        catch
        {
            // 受限环境或裁剪后的运行时可能拒绝反射读取，忽略并继续探测。
        }

        // 3) 兜底：%TEMP%\.net\<app>\<hash>\
        try
        {
            var bundleRoot = Path.Combine(Path.GetTempPath(), ".net");
            if (Directory.Exists(bundleRoot))
            {
                foreach (var appDirectory in Directory.EnumerateDirectories(bundleRoot))
                foreach (var candidate in Directory.EnumerateDirectories(appDirectory))
                {
                    if (HasContent(candidate)) return candidate;
                }
            }
        }
        catch
        {
            // 临时目录不可枚举时保持静默，由调用方按“内容缺失”降级处理。
        }

        return baseDirectory;
    }

    private static bool HasContent(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return false;
        return Directory.Exists(Path.Combine(directory, "Resources"))
            || Directory.Exists(Path.Combine(directory, "Prompts"))
            || Directory.Exists(Path.Combine(directory, "Presets"))
            || File.Exists(Path.Combine(directory, "Config", "default-config.json"));
    }
}
