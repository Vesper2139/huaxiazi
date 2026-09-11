using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json;
using Huaxiazi.Services;

namespace Huaxiazi.Models;

/// <summary>
/// 应用配置（持久化到 %LocalAppData%\Huaxiazi\config.json）。
/// 所有字段均带默认值，保证反序列化友好。
/// </summary>
public sealed class AppSettings
{
    public AppSettings Clone()
    {
        var options = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        var json = JsonSerializer.Serialize(this, options);
        return JsonSerializer.Deserialize<AppSettings>(json, options)
               ?? throw new InvalidOperationException("无法复制应用配置。");
    }

    [JsonPropertyName("defaultMode")]
    public ApplicationMode DefaultMode { get; set; } = ApplicationMode.Polish;

    [JsonPropertyName("enabledModes")]
    public List<ApplicationMode> EnabledModes { get; set; } =
        [ApplicationMode.Polish, ApplicationMode.PromptOptimize];

    [JsonPropertyName("clipboardAutoRead")]
    public bool ClipboardAutoRead { get; set; }

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    [JsonPropertyName("autoArchive")]
    public bool AutoArchive { get; set; } = true;

    [JsonPropertyName("historyEnabled")]
    public bool HistoryEnabled { get; set; } = true;

    [JsonPropertyName("historyRetentionDays")]
    public int HistoryRetentionDays { get; set; } = 30;

    [JsonPropertyName("saveOriginalText")]
    public bool SaveOriginalText { get; set; } = true;

    [JsonPropertyName("saveOptimizedText")]
    public bool SaveOptimizedText { get; set; } = true;

    [JsonPropertyName("incognitoMode")]
    public bool IncognitoMode { get; set; }

    [JsonPropertyName("autoCopyAfterOptimize")]
    public bool AutoCopyAfterOptimize { get; set; }

    /// <summary>开启后在主编辑框按 Enter 直接发送；关闭时 Enter 保留换行行为。</summary>
    [JsonPropertyName("enterToSend")]
    public bool EnterToSend { get; set; }

    [JsonPropertyName("autosaveDelayMilliseconds")]
    public int AutosaveDelayMilliseconds { get; set; } = 750;

    [JsonPropertyName("clarificationEnabled")]
    public bool ClarificationEnabled { get; set; } = true;

    [JsonPropertyName("showDiff")]
    public bool ShowDiff { get; set; }

    [JsonPropertyName("defaultPolishScenario")]
    public string DefaultPolishScenario { get; set; } = "其他";

    [JsonPropertyName("outputStyle")]
    public string OutputStyle { get; set; } = "自然";

    [JsonPropertyName("customStyleInstructions")]
    public string CustomStyleInstructions { get; set; } = string.Empty;

    [JsonPropertyName("customSystemPrompt")]
    public string CustomSystemPrompt { get; set; } = string.Empty;

    [JsonPropertyName("preferenceLearningEnabled")]
    public bool PreferenceLearningEnabled { get; set; } = true;

    [JsonPropertyName("expressionPreferenceProfile")]
    public ExpressionPreferenceProfile ExpressionPreferenceProfile { get; set; } = new();

    [JsonPropertyName("preserveMeaning")]
    public bool PreserveMeaning { get; set; } = true;

    [JsonPropertyName("minimalRewrite")]
    public bool MinimalRewrite { get; set; }

    [JsonPropertyName("professionalTone")]
    public bool ProfessionalTone { get; set; }

    [JsonPropertyName("optimizationPresets")]
    public List<OptimizationPreset> OptimizationPresets { get; set; } = OptimizationPreset.CreateDefaults();

    [JsonPropertyName("activePresetId")]
    public string ActivePresetId { get; set; } = string.Empty;

    [JsonPropertyName("enabledPromptCategories")]
    public List<PromptCategory> EnabledPromptCategories { get; set; } = PromptCategoryMetadata.AllCategories.ToList();

    [JsonPropertyName("promptHistoryLimit")]
    public int PromptHistoryLimit { get; set; } = 20;

    [JsonPropertyName("floatingBallEnabled")]
    public bool FloatingBallEnabled { get; set; } = true;

    [JsonPropertyName("trayEnabled")]
    public bool TrayEnabled { get; set; } = true;

    [JsonPropertyName("alwaysOnTop")]
    public bool AlwaysOnTop { get; set; } = true;

    [JsonPropertyName("themeMode")]
    public string ThemeMode { get; set; } = "System";

    /// <summary>完整外观皮肤。default 表示由 ThemeMode 解析浅色/深色资源。</summary>
    [JsonPropertyName("skinId")]
    public string SkinId { get; set; } = "default";

    [JsonPropertyName("editorFontSize")]
    public double EditorFontSize { get; set; } = 13;

    [JsonPropertyName("uiScale")]
    public double UiScale { get; set; } = 1;

    [JsonPropertyName("editorDefaultHeight")]
    public double EditorDefaultHeight { get; set; } = 36;

    [JsonPropertyName("animationsEnabled")]
    public bool AnimationsEnabled { get; set; } = true;

    [JsonPropertyName("companionDriverMode")]
    public CompanionDriverMode CompanionDriverMode { get; set; } = CompanionDriverMode.Local;

    [JsonPropertyName("windowOpacity")]
    public double WindowOpacity { get; set; } = 1;

    [JsonPropertyName("floatingBallOpacity")]
    public double FloatingBallOpacity { get; set; } = 0.92;

    [JsonPropertyName("floatingBallSize")]
    public double FloatingBallSize { get; set; } = 44;

    [JsonPropertyName("closeBehavior")]
    public string CloseBehavior { get; set; } = "Hide";

    [JsonPropertyName("escapeBehavior")]
    public string EscapeBehavior { get; set; } = "Hide";

    [JsonPropertyName("rememberWindowSize")]
    public bool RememberWindowSize { get; set; } = true;

    [JsonPropertyName("rememberFloatingBallPosition")]
    public bool RememberFloatingBallPosition { get; set; } = true;

    [JsonPropertyName("snapFloatingBallToEdge")]
    public bool SnapFloatingBallToEdge { get; set; } = true;

    [JsonPropertyName("showInTaskbar")]
    public bool ShowInTaskbar { get; set; }

    [JsonPropertyName("providerProfiles")]
    public List<ProviderProfile> ProviderProfiles { get; set; } =
    [
        new ProviderProfile()
    ];

    [JsonPropertyName("activeProviderProfileId")]
    public string ActiveProviderProfileId { get; set; } = "default";

    /// <summary>OpenAI-Compatible API 基地址。</summary>
    [JsonPropertyName("apiBase")]
    public string ApiBase { get; set; } = "https://api.openai.com/v1";

    /// <summary>API Key（首次使用需到设置中填写）。</summary>
    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>模型名称。</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>默认任务类别。</summary>
    [JsonPropertyName("defaultCategory")]
    public string DefaultCategory { get; set; } = "通用任务";

    /// <summary>默认优化深度。</summary>
    [JsonPropertyName("defaultDepth")]
    public string DefaultDepth { get; set; } = "标准";

    /// <summary>全局快捷键（文本表示，如 Ctrl+Shift+H）。</summary>
    [JsonPropertyName("hotkey")]
    public string Hotkey { get; set; } = "Ctrl+Shift+H";

    [JsonPropertyName("quickPolishHotkey")]
    public string QuickPolishHotkey { get; set; } = string.Empty;

    [JsonPropertyName("quickPromptHotkey")]
    public string QuickPromptHotkey { get; set; } = string.Empty;

    [JsonPropertyName("copyResultHotkey")]
    public string CopyResultHotkey { get; set; } = string.Empty;

    /// <summary>
    /// 更新检查源 URL（指向 <c>version.json</c>）。为空字符串表示禁用自动更新检查（默认）。
    /// <c>version.json</c> 必须是由发布密钥生成的 RSA 签名信封；无签名清单会被拒绝。
    /// UI 仅在用户主动点击“检查更新”时拉取，启动不强制检查，以避免引入任何网络硬依赖。
    /// 所有拉取失败（离线 / HTTP 错误 / 坏 JSON）均安全降级，不会抛未处理异常。
    /// </summary>
    [JsonPropertyName("updateCheckUrl")]
    public string UpdateCheckUrl { get; set; } = string.Empty;

    [JsonPropertyName("autoCheckUpdates")]
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>
    /// 用户画像 / 角色设定（配置文件写入；为空表示不附加画像约束）。
    /// 用于让优化结果贴合用户身份与偏好。
    /// </summary>
    [JsonPropertyName("userPersona")]
    public string UserPersona { get; set; } = string.Empty;

    /// <summary>
    /// 悬浮球窗口左上角 X 坐标（记忆上次位置）。
    /// 未记忆时为 <c>null</c>（用可空 double 显式表达“未设置”，而非隐式依赖 double.NaN 哨兵值）。
    /// </summary>
    [JsonPropertyName("windowLeft")]
    public double? WindowLeft { get; set; }

    /// <summary>
    /// 悬浮球窗口左上角 Y 坐标（记忆上次位置）。
    /// 未记忆时为 <c>null</c>。
    /// </summary>
    [JsonPropertyName("windowTop")]
    public double? WindowTop { get; set; }

    [JsonPropertyName("ballLeft")]
    public double? BallLeft { get; set; }

    [JsonPropertyName("ballTop")]
    public double? BallTop { get; set; }

    [JsonPropertyName("mainWindowWidth")]
    public double MainWindowWidth { get; set; } = 520;

    [JsonPropertyName("mainWindowHeight")]
    public double MainWindowHeight { get; set; } = 176;

    [JsonPropertyName("dataDirectory")]
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 配置结构版本号。0 表示旧版/未带版本字段的配置；
    /// 升级到新版默认值或字段变更时由 ConfigService.Migrate 提升，作为向后兼容的迁移锚点。
    /// </summary>
    [JsonPropertyName("configVersion")]
    public int ConfigVersion { get; set; }

    /// <summary>最近历史记录（最多 20 条，最新在前）。</summary>
    [JsonPropertyName("history")]
    public List<string> History { get; set; } = new();

    /// <summary>
    /// 将类别中文名解析为枚举（无法解析时返回通用任务）。
    /// </summary>
    public PromptCategory GetDefaultCategory()
    {
        foreach (var c in PromptCategoryMetadata.AllCategories)
        {
            if (c.GetDisplayName() == DefaultCategory)
            {
                return c;
            }
        }
        return PromptCategory.General;
    }

    /// <summary>
    /// 将深度中文名解析为枚举（无法解析时返回标准）。
    /// </summary>
    public PromptDepth GetDefaultDepth()
    {
        foreach (var d in PromptDepthMetadata.AllDepths)
        {
            if (d.GetDisplayName() == DefaultDepth)
            {
                return d;
            }
        }
        return PromptDepth.Standard;
    }

    public void NormalizeProductModes()
    {
        EnabledModes ??= [];
        EnabledModes = EnabledModes.Distinct().ToList();
        if (!EnabledModes.Contains(ApplicationMode.Polish)) EnabledModes.Add(ApplicationMode.Polish);
        if (!EnabledModes.Contains(ApplicationMode.PromptOptimize)) EnabledModes.Add(ApplicationMode.PromptOptimize);

        if (!EnabledModes.Contains(DefaultMode))
        {
            DefaultMode = EnabledModes[0];
        }
    }

    public void NormalizeProviderProfiles()
    {
        ProviderProfiles ??= [];
        ProviderProfiles = ProviderProfiles
            .Where(profile => profile is not null)
            .GroupBy(profile => string.IsNullOrWhiteSpace(profile.Id) ? "default" : profile.Id)
            .Select(group => group.First())
            .ToList();

        if (ProviderProfiles.Count == 0)
        {
            ProviderProfiles.Add(new ProviderProfile());
        }

        foreach (var profile in ProviderProfiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id)) profile.Id = "default";
            if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = "默认模型";
            if (string.IsNullOrWhiteSpace(profile.ApiBase)) profile.ApiBase = "https://api.openai.com/v1";
            if (string.IsNullOrWhiteSpace(profile.Model)) profile.Model = "gpt-4o-mini";
            if (profile.TimeoutSeconds is < 10 or > 600) profile.TimeoutSeconds = 120;
            profile.Temperature = System.Math.Clamp(profile.Temperature, 0, 2);
            profile.TopP = System.Math.Clamp(profile.TopP, 0, 1);
            profile.MaxTokens = System.Math.Clamp(profile.MaxTokens, 128, 32768);
            if (string.IsNullOrWhiteSpace(profile.SecretId)) profile.SecretId = ProviderCredentialBinding.ForProfile(profile);
        }

        if (!ProviderProfiles.Any(profile => profile.Id == ActiveProviderProfileId))
        {
            ActiveProviderProfileId = ProviderProfiles[0].Id;
        }
    }

    /// <summary>
    /// Rebind credential slots when loading untrusted JSON. A copied config must
    /// not be able to name an existing profile's secret slot.
    /// </summary>
    internal bool NormalizeLoadedCredentialBindings()
    {
        var changed = false;
        foreach (var profile in ProviderProfiles ?? [])
        {
            // Custom endpoints are an authorization boundary. Do not let an
            // imported profile reuse a legacy slot such as provider-default.
            var stable = ProviderCredentialBinding.ForProfile(profile);
            var switchedProvider = ProviderCredentialBinding.ForEndpointIdentity(profile);
            var valid = string.Equals(profile.SecretId, stable, StringComparison.Ordinal)
                || (profile.Platform != ProviderPlatform.CustomOpenAICompatible
                    && string.Equals(profile.SecretId, switchedProvider, StringComparison.Ordinal));
            changed |= !valid;
            // Every loaded profile is untrusted input. Keep only the stable
            // slot or the exact endpoint-bound slot produced by a provider
            // switch; never honor an arbitrary SecretId from JSON.
            if (!valid) profile.SecretId = stable;
        }
        return changed;
    }

    public ProviderProfile GetActiveProviderProfile()
    {
        NormalizeProviderProfiles();
        return ProviderProfiles.First(profile => profile.Id == ActiveProviderProfileId);
    }

    public void NormalizeResidentEntrypoints()
    {
        if (!TrayEnabled && !FloatingBallEnabled)
        {
            TrayEnabled = true;
        }
    }

    public void NormalizePromptSettings()
    {
        EnabledPromptCategories ??= [];
        EnabledPromptCategories = EnabledPromptCategories.Distinct().ToList();
        if (EnabledPromptCategories.Count == 0) EnabledPromptCategories.Add(PromptCategory.General);
        PromptHistoryLimit = System.Math.Clamp(PromptHistoryLimit, 1, 100);
        HistoryRetentionDays = System.Math.Clamp(HistoryRetentionDays, 1, 3650);
        AutosaveDelayMilliseconds = System.Math.Clamp(AutosaveDelayMilliseconds, 250, 10000);
        if (!EnabledPromptCategories.Contains(GetDefaultCategory()))
        {
            DefaultCategory = EnabledPromptCategories[0].GetDisplayName();
        }
        OptimizationPresets ??= [];
        OptimizationPresets = OptimizationPresets
            .Where(preset => preset is not null)
            .GroupBy(preset => string.IsNullOrWhiteSpace(preset.Id) ? Guid.NewGuid().ToString("N") : preset.Id)
            .Select(group => group.First())
            .ToList();
        foreach (var preset in OptimizationPresets)
        {
            if (string.IsNullOrWhiteSpace(preset.Id)) preset.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(preset.Name)) preset.Name = "未命名预设";
        }
        if (!OptimizationPresets.Any(preset => preset.Id == ActivePresetId)) ActivePresetId = string.Empty;
    }

    public void NormalizeDisplaySettings()
    {
        if (!Enum.IsDefined(CompanionDriverMode)) CompanionDriverMode = CompanionDriverMode.Local;
        EditorFontSize = System.Math.Clamp(EditorFontSize, 10, 24);
        UiScale = System.Math.Clamp(UiScale, 0.8, 1.5);
        EditorDefaultHeight = System.Math.Clamp(EditorDefaultHeight, 28, 800);
        WindowOpacity = System.Math.Clamp(WindowOpacity, 0.65, 1);
        FloatingBallOpacity = System.Math.Clamp(FloatingBallOpacity, 0.35, 1);
        FloatingBallSize = System.Math.Clamp(FloatingBallSize, 32, 96);
        if (CloseBehavior is not ("Hide" or "Tray" or "Exit")) CloseBehavior = "Hide";
        if (EscapeBehavior is not ("Hide" or "Tray" or "None")) EscapeBehavior = "Hide";
    }
}
