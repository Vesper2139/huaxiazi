using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>
/// 皮肤服务：维护皮肤注册表并执行整套 ResourceDictionary 替换。
/// 为后续第三方/自定义皮肤体系预留底层入口：Register + ApplySkin。
/// </summary>
public sealed class SkinService
{
    private readonly Dictionary<string, SkinManifest> _skins = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _runtimeOverrideKeys = new(StringComparer.Ordinal);

    public SkinService()
    {
        RegisterBuiltInSkins();
    }

    public SkinService(ResourceDictionary targetResources)
    {
        TargetResources = targetResources ?? throw new ArgumentNullException(nameof(targetResources));
        RegisterBuiltInSkins();
    }

    public ResourceDictionary? TargetResources { get; }
    public event EventHandler<string>? SkinChanged;

    public IReadOnlyCollection<SkinManifest> AvailableSkins => _skins.Values;

    public SkinManifest? GetSkin(string id) =>
        _skins.TryGetValue(id, out var skin) ? skin : null;

    /// <summary>
    /// 注册新皮肤。内置皮肤同名时会被覆盖，便于外部扩展或主题补丁。
    /// </summary>
    public void Register(SkinManifest skin)
    {
        ArgumentNullException.ThrowIfNull(skin);
        _skins[skin.Id] = skin;
    }

    public bool Unregister(string id) => _skins.Remove(id);

    /// <summary>
    /// 按 Id 应用整套皮肤字典到指定 ResourceDictionary；替换而非局部修改。
    /// </summary>
    public void ApplySkin(string id, ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (!_skins.TryGetValue(id, out var skin))
        {
            throw new ArgumentException($"未知皮肤：{id}", nameof(id));
        }

        ClearRuntimeOverrides(resources);
        if (!skin.IsBuiltIn && !string.IsNullOrWhiteSpace(skin.InstallPath))
        {
            ApplyBuiltInDictionary(GetSkin("LightPaper")!, resources);
            ApplyDataTheme(skin, resources);
            SkinChanged?.Invoke(this, skin.Id);
            return;
        }
        ApplyBuiltInDictionary(skin, resources);
        SkinChanged?.Invoke(this, skin.Id);
    }

    private static void ApplyBuiltInDictionary(SkinManifest skin, ResourceDictionary resources)
    {
        var source = new Uri($"/Huaxiazi;component/{skin.ResourcePath.TrimStart('/')}", UriKind.Relative);
        var existingIndex = -1;
        for (var i = 0; i < resources.MergedDictionaries.Count; i++)
        {
            var dictionarySource = resources.MergedDictionaries[i].Source?.OriginalString;
            if (dictionarySource is not null && IsThemeResource(dictionarySource))
            {
                existingIndex = i;
                break;
            }
        }

        var dictionary = new ResourceDictionary { Source = source };
        if (existingIndex >= 0)
        {
            // RemoveAt+Insert 触发集合变更事件，比下标赋值更可靠地让 {DynamicResource}
            // 对画刷键重新解析（下标赋值在某些资源树深度可能不失效旧值）。
            resources.MergedDictionaries.RemoveAt(existingIndex);
            resources.MergedDictionaries.Insert(existingIndex, dictionary);
        }
        else
        {
            resources.MergedDictionaries.Insert(0, dictionary);
        }
    }

    private void ApplyDataTheme(SkinManifest skin, ResourceDictionary resources)
    {
        var path = Path.Combine(skin.InstallPath!, skin.ResourcePath.Replace('/', Path.DirectorySeparatorChar));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("皮肤主题令牌必须是 JSON 对象。");
        foreach (var property in document.RootElement.EnumerateObject())
        {
            object value;
            if (property.Name.EndsWith("Color", StringComparison.Ordinal) && property.Value.ValueKind == JsonValueKind.String)
            {
                value = (Color)ColorConverter.ConvertFromString(property.Value.GetString()!)!;
                var brushKey = property.Name[..^"Color".Length] + "Brush";
                resources[brushKey] = new SolidColorBrush((Color)value);
                _runtimeOverrideKeys.Add(brushKey);
            }
            else if (property.Name.EndsWith("Radius", StringComparison.Ordinal) && property.Value.TryGetDouble(out var radius))
                value = new CornerRadius(Math.Clamp(radius, 0, 30));
            else if (property.Name.EndsWith("Spacing", StringComparison.Ordinal) && property.Value.TryGetDouble(out var spacing))
                value = new Thickness(Math.Clamp(spacing, 0, 24));
            else if (property.Value.TryGetDouble(out var number))
                value = number;
            else continue;
            resources[property.Name] = value;
            _runtimeOverrideKeys.Add(property.Name);
        }
        resources["ThemeId"] = skin.Id;
        _runtimeOverrideKeys.Add("ThemeId");
    }

    private void ClearRuntimeOverrides(ResourceDictionary resources)
    {
        foreach (var key in _runtimeOverrideKeys) resources.Remove(key);
        _runtimeOverrideKeys.Clear();
    }

    /// <summary>
    /// 判断已合并字典的源是否指向皮肤字典。同时兼容两种写法：
    /// 带程序集前缀的 pack URI（如 "/Huaxiazi;component/Resources/Themes/Dark.xaml"）
    /// 与 App.xaml 中直接写出的相对路径（如 "Resources/Themes/Dark.xaml"），
    /// 否则会因前缀差异导致永远匹配不到、进而 Insert 而非 Replace、字典不断膨胀。
    /// </summary>
    private static bool IsThemeResource(string source)
    {
        var normalized = source.Replace('\\', '/');
        var idx = normalized.IndexOf("Resources/Themes/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0;
    }

    /// <summary>
    /// 按 Id 应用整套皮肤字典；替换而非局部修改，保证后续皮肤体系可插拔。
    /// 优先使用构造时注入的 ResourceDictionary，否则回退到 Application.Current.Resources。
    /// </summary>
    public void ApplySkin(string id)
    {
        if (!_skins.TryGetValue(id, out var skin))
        {
            throw new ArgumentException($"未知皮肤：{id}", nameof(id));
        }

        var resources = TargetResources ?? System.Windows.Application.Current?.Resources;
        if (resources is null) return;
        ApplySkin(id, resources);
    }

    private void RegisterBuiltInSkins()
    {
        Register(new SkinManifest("LightPaper", "浅色纸张", "Resources/Themes/Light.xaml"));
        Register(new SkinManifest("DarkNocturne", "深色夜幕", "Resources/Themes/Dark.xaml"));
        Register(new SkinManifest("LuoXiaoHei", "罗小黑", "Resources/Themes/LuoXiaoHei.xaml",
            CompanionKind: "spritesheet", PreviewPath: "Resources/Skins/LuoXiaoHei/Idle.png",
            CompanionSpriteSheetPath: "Resources/Skins/LuoXiaoHei/sprite-sheet-v2.png",
            CompanionIdleVariantsPath: "Resources/Skins/LuoXiaoHei/idle-variants-v2.png"));
        Register(new SkinManifest("MaoDie", "耄耋", "Resources/Themes/MaoDie.xaml",
            CompanionKind: "spritesheet", PreviewPath: "Resources/Skins/MaoDie/Idle.png",
            CompanionSpriteSheetPath: "Resources/Skins/MaoDie/sprite-sheet.png"));
    }

    private static IReadOnlyDictionary<string, string> BuildCompanionStates(string folder)
    {
        var states = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var state in Enum.GetNames<CompanionVisualState>())
            states[state] = $"Resources/Skins/{folder}/{state}.png";
        return states;
    }
}
