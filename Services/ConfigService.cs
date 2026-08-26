using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using PromptFloat.Models;

namespace PromptFloat.Services;

/// <summary>
/// 配置读写服务。
/// 负责从 %LocalAppData%\Huaxiazi\config.json 读取配置；不存在时使用默认值并写入。
/// </summary>
public sealed class ConfigService
{
    internal const int MaxCorruptBackups = 3;
    /// <summary>
    /// 当前配置结构版本号。每当默认值或字段语义发生破坏性变更时递增；
    /// 旧配置在 Load 时由 Migrate() 平滑升级到该版本，保证对终端用户与外部调用方的向后兼容。
    /// </summary>
    public const int CurrentConfigVersion = 11;

    // 注意：此处未用 readonly，以便单元测试通过反射把路径重定向到临时目录做隔离（见 PromptFloat.Tests/TestHelpers.cs）。
    private static string AppDataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Huaxiazi");

    private static string ConfigFilePath = Path.Combine(AppDataFolder, "config.json");

    private static string LegacyConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PromptFloat",
        "config.json");

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        // 允许读取旧版 (v0) 把“未记忆位置”存为 NaN 的 config.json（迁移前需先读回 NaN 再归一化为 null）；
        // 新写入的坐标统一为 JSON null，不再产生 NaN 字面量。
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 配置目录（便于调试）。暴露只读访问。
    /// </summary>
    public static string ConfigDirectory => AppDataFolder;
    public static string ConfigPath => ConfigFilePath;

    /// <summary>
    /// 加载配置。若文件不存在或反序列化失败，回退到默认配置并写回磁盘。
    /// 读取 / 回退 / 写回的每一步都做了异常隔离，确保首次启动或配置损坏时不会让
    /// 应用初始化崩溃（修复：原本 Save(defaults) 在 try/catch 之外，且默认坐标使用
    /// double.NaN，而 System.Text.Json 默认禁止序列化 NaN，会抛异常）。
    /// </summary>
    public AppSettings Load()
    {
        AppSettings? settings = null;
        var sourcePath = ConfigFilePath;
        var importingLegacyLocation = false;

        try
        {
            if (!File.Exists(sourcePath) && File.Exists(LegacyConfigFilePath))
            {
                sourcePath = LegacyConfigFilePath;
                importingLegacyLocation = true;
            }

            if (File.Exists(sourcePath))
            {
                var raw = File.ReadAllText(sourcePath);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    // 使用相同选项反序列化，确保写入的 NaN/Infinity 字面量可被正确读回。
                    settings = JsonSerializer.Deserialize<AppSettings>(raw, _jsonOptions);
                }
            }
        }
        catch (Exception)
        {
            PreserveCorruptConfig(sourcePath);
            settings = null;
        }

        // 反序列化为 null、历史字段被显式写为 null 等情况下做防御，避免后续 NRE
        if (settings is not null)
        {
            settings.History ??= new List<string>();
            var needsConfigRewrite = settings.ConfigVersion < CurrentConfigVersion;
            var legacyProductConfig = settings.ConfigVersion < 2;
            var hadPlaintextSecret = !string.IsNullOrWhiteSpace(settings.ApiKey);
            settings = Migrate(settings);
            if (!DataDirectoryPolicy.TryResolve(settings.DataDirectory, AppDataFolder, verifyWritable: false, out var dataRoot, out _))
            {
                settings.DataDirectory = string.Empty;
                dataRoot = Path.GetFullPath(AppDataFolder);
                needsConfigRewrite = true;
            }
            var secretMigrated = MigrateLegacySecret(settings, legacyProductConfig, dataRoot);

            // 加密失败时不写新位置、不改旧文件，避免产生一份半迁移配置或丢失密钥。
            if (hadPlaintextSecret && !secretMigrated)
            {
                return settings;
            }

            if (needsConfigRewrite || secretMigrated || importingLegacyLocation)
            {
                try
                {
                    Save(settings);
                    if (importingLegacyLocation)
                    {
                        SanitizeLegacyConfig(settings);
                    }
                }
                catch
                {
                    // 旧文件只有在新配置完整写入后才会脱敏；失败时原配置保持不变。
                }
            }
            return settings;
        }

        var defaults = CreateDefault();
        try
        {
            Save(defaults);
        }
        catch (Exception)
        {
            // 写回失败不应阻断启动（例如 AppData 不可写）；内存中的默认配置仍可用
        }
        return defaults;
    }

    private static void PreserveCorruptConfig(string sourcePath)
    {
        try
        {
            if (!File.Exists(sourcePath)) return;
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var backupPath = $"{sourcePath}.{stamp}.corrupt";
            var raw = File.ReadAllText(sourcePath);
            var redacted = RedactSensitiveJson(raw) ??
                "{\"error\":\"配置解析失败，原文未备份\",\"capturedAtUtc\":\"" + DateTime.UtcNow.ToString("O") + "\"}";
            File.WriteAllText(backupPath, redacted);
            CleanupCorruptBackups(sourcePath);
        }
        catch (Exception)
        {
            // 保全失败不应阻断回退默认配置。
        }
    }

    internal static string? RedactSensitiveJson(string raw)
    {
        try
        {
            var node = JsonNode.Parse(raw);
            if (node is null) return null;
            RedactSensitiveNode(node);
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            // 解析失败时绝不把原文写入备份；调用方会写入不含正文的元数据。
            return null;
        }
    }

    private static void RedactSensitiveNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                if (IsSensitiveProperty(property.Key))
                {
                    obj[property.Key] = "[REDACTED]";
                }
                else if (property.Value is not null)
                {
                    RedactSensitiveNode(property.Value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null) RedactSensitiveNode(item);
            }
        }
    }

    private static bool IsSensitiveProperty(string name) =>
        name.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase);

    private static void CleanupCorruptBackups(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        var fileName = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName)) return;
        var backups = Directory.GetFiles(directory, fileName + ".*.corrupt")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Skip(MaxCorruptBackups)
            .ToList();
        foreach (var backup in backups)
        {
            try { File.Delete(backup); } catch { }
        }
    }

    /// <summary>
    /// 保存配置到磁盘。
    /// </summary>
    public void Save(AppSettings settings)
    {
        if (settings is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(AppDataFolder);
            SanitizeCoordinates(settings);
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            var temporaryPath = ConfigFilePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, ConfigFilePath, true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"无法保存配置到 {ConfigFilePath}：{ex.Message}", ex);
        }
    }

    private static void SanitizeCoordinates(AppSettings settings)
    {
        settings.WindowLeft = FiniteOrNull(settings.WindowLeft);
        settings.WindowTop = FiniteOrNull(settings.WindowTop);
        settings.BallLeft = FiniteOrNull(settings.BallLeft);
        settings.BallTop = FiniteOrNull(settings.BallTop);
    }

    private static double? FiniteOrNull(double? value) => value is { } number && double.IsFinite(number) ? number : null;

    /// <summary>
    /// 从内置默认配置（Config/default-config.json）构造一份新配置。
    /// 该 JSON 是默认值唯一权威来源，避免默认值在代码与文件中重复硬编码而产生漂移；
    /// 若运行时找不到该文件（如单文件发布未随包携带），回退到硬编码默认值，保证永不崩溃。
    /// </summary>
    public AppSettings CreateDefault()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Config", "default-config.json"),
            Path.Combine(AppContext.BaseDirectory, "default-config.json")
        };

        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    var raw = File.ReadAllText(path);
                    var fromFile = JsonSerializer.Deserialize<AppSettings>(raw, _jsonOptions);
                    if (fromFile is not null)
                    {
                        fromFile.History ??= new List<string>();
                        return fromFile;
                    }
                }
            }
            catch (Exception)
            {
                // 读取/解析失败：尝试下一个候选，最终回退到硬编码默认值
            }
        }

        return CreateHardcodedDefault();
    }

    /// <summary>
    /// 硬编码兜底默认值（与 Config/default-config.json 保持一致）。
    /// 仅在运行时无法读取 bundled 默认配置时启用，确保程序始终有可用默认值。
    /// </summary>
    private static AppSettings CreateHardcodedDefault()
    {
        return new AppSettings
        {
            ApiBase = "https://api.openai.com/v1",
            ApiKey = string.Empty,
            Model = "gpt-4o-mini",
            DefaultCategory = "通用任务",
            DefaultDepth = "标准",
            Hotkey = "Ctrl+Shift+H",
            QuickPolishHotkey = string.Empty,
            QuickPromptHotkey = string.Empty,
            CopyResultHotkey = string.Empty,
            UpdateCheckUrl = string.Empty,
            UserPersona = string.Empty,
            DefaultMode = ApplicationMode.Polish,
            EnabledModes = [ApplicationMode.Polish, ApplicationMode.PromptOptimize],
            ClipboardAutoRead = false,
            StartWithWindows = false,
            AutoArchive = true,
            EnterToSend = false,
            ClarificationEnabled = true,
            DefaultPolishScenario = "其他",
            OutputStyle = "自然",
            CustomStyleInstructions = string.Empty,
            EnabledPromptCategories = PromptCategoryMetadata.AllCategories.ToList(),
            PromptHistoryLimit = 20,
            FloatingBallEnabled = true,
            TrayEnabled = true,
            AlwaysOnTop = true,
            ThemeMode = "System",
            SkinId = "default",
            ProviderProfiles = [new ProviderProfile()],
            ActiveProviderProfileId = "default",
            WindowLeft = null,
            WindowTop = null,
            BallLeft = null,
            BallTop = null,
            MainWindowWidth = 520,
            MainWindowHeight = 176,
            History = new(),
            ConfigVersion = CurrentConfigVersion
        };
    }

    /// <summary>
    /// 配置迁移：把任意旧版/外部来源的配置平滑升级到 <see cref="CurrentConfigVersion"/>。
    /// 这是预留的清晰扩展点——未来字段变更只需在此追加对应版本的迁移分支，
    /// 既保持对终端用户已有 config.json 的向后兼容，也避免破坏性变更。
    /// </summary>
    private static AppSettings Migrate(AppSettings settings)
    {
        // v0 → v1：将旧的 double.NaN 窗口坐标归一化为 null（旧版哨兵值），确保语义与默认值一致。
        if (settings.ConfigVersion < 1)
        {
            if (settings.WindowLeft is { } lx && double.IsNaN(lx))
            {
                settings.WindowLeft = null;
            }
            if (settings.WindowTop is { } ty && double.IsNaN(ty))
            {
                settings.WindowTop = null;
            }
            settings.ConfigVersion = 1;
        }

        if (settings.ConfigVersion < 2)
        {
            settings.ConfigVersion = 2;
        }

        if (settings.ConfigVersion < 3)
        {
            settings.ThemeMode = "Dark";
            settings.ConfigVersion = 3;
        }

        if (settings.ConfigVersion < 4)
        {
            settings.BallLeft ??= settings.WindowLeft;
            settings.BallTop ??= settings.WindowTop;
            settings.MainWindowWidth = NormalizeDimension(settings.MainWindowWidth, 520, 300);
            settings.MainWindowHeight = NormalizeDimension(settings.MainWindowHeight, 120, 112);
            settings.ConfigVersion = 4;
        }

        if (settings.ConfigVersion < 5)
        {
            settings.ConfigVersion = 5;
        }

        if (settings.ConfigVersion < 6)
        {
            // 1.1.3 及以前的出厂尺寸会被运行时抬高到 176/190；仅迁移这些旧默认值，保留用户自定义尺寸。
            var usesLegacyFactoryDisplay =
                (Math.Abs(settings.MainWindowHeight - 176) < 0.5 || Math.Abs(settings.MainWindowHeight - 190) < 0.5)
                && Math.Abs(settings.EditorDefaultHeight - 120) < 0.5
                && string.Equals(settings.ThemeMode, "Dark", StringComparison.OrdinalIgnoreCase);
            if (Math.Abs(settings.MainWindowHeight - 176) < 0.5 || Math.Abs(settings.MainWindowHeight - 190) < 0.5)
                settings.MainWindowHeight = 120;
            if (Math.Abs(settings.EditorDefaultHeight - 120) < 0.5)
                settings.EditorDefaultHeight = 36;
            if (usesLegacyFactoryDisplay)
                settings.ThemeMode = "System";
            settings.ConfigVersion = 6;
        }

        if (settings.ConfigVersion < 7)
        {
            // 旧版出厂时默认占用四组系统热键。只迁移仍等于旧出厂值的字段，保留用户自定义选择。
            if (string.Equals(settings.Hotkey, "Ctrl+Shift+P", StringComparison.OrdinalIgnoreCase))
                settings.Hotkey = "Ctrl+Shift+H";
            if (string.Equals(settings.QuickPolishHotkey, "Ctrl+Alt+1", StringComparison.OrdinalIgnoreCase))
                settings.QuickPolishHotkey = string.Empty;
            if (string.Equals(settings.QuickPromptHotkey, "Ctrl+Alt+2", StringComparison.OrdinalIgnoreCase))
                settings.QuickPromptHotkey = string.Empty;
            if (string.Equals(settings.CopyResultHotkey, "Ctrl+Alt+3", StringComparison.OrdinalIgnoreCase))
                settings.CopyResultHotkey = string.Empty;
            settings.ConfigVersion = 7;
        }

        if (settings.ConfigVersion < 8)
        {
            // v8 的精灵便笺工作区需要稳定的标题、编辑区和快捷栏高度；只迁移旧出厂尺寸。
            if (Math.Abs(settings.MainWindowWidth - 520) < 0.5) settings.MainWindowWidth = 560;
            if (Math.Abs(settings.MainWindowHeight - 120) < 0.5) settings.MainWindowHeight = 210;
            settings.FloatingBallSize = 44;
            settings.ConfigVersion = 8;
        }

        if (settings.ConfigVersion < 9)
        {
            // 仅放宽旧出厂宽度，为标题品牌、模型选择和窗口操作留出稳定节奏。
            if (Math.Abs(settings.MainWindowWidth - 560) < 0.5) settings.MainWindowWidth = 600;
            settings.ConfigVersion = 9;
        }

        if (settings.ConfigVersion < 10)
        {
            settings.SkinId = "default";
            if (Math.Abs(settings.MainWindowWidth - 600) < 0.5) settings.MainWindowWidth = 520;
            if (Math.Abs(settings.MainWindowHeight - 210) < 0.5) settings.MainWindowHeight = 176;
            settings.ConfigVersion = 10;
        }

        if (settings.ConfigVersion < 11)
        {
            // v1.5 stops all knowledge retrieval. Legacy database files remain untouched and dormant.
            settings.ExternalStrategiesEnabled = true;
            settings.PreferenceLearningEnabled = true;
            settings.ExpressionPreferenceProfile ??= new ExpressionPreferenceProfile();
            settings.PolishStrategyId = "text-polisher";
            settings.PromptStrategyId = "prompt-optimizer";
            settings.ConfigVersion = 11;
        }

        settings.NormalizeProductModes();
        settings.NormalizeProviderProfiles();
        settings.NormalizeResidentEntrypoints();
        settings.NormalizePromptSettings();
        settings.NormalizeDisplaySettings();
        settings.ExpressionPreferenceProfile ??= new ExpressionPreferenceProfile();

        return settings;
    }

    private static double NormalizeDimension(double value, double fallback, double minimum)
        => double.IsFinite(value) && value >= minimum ? value : fallback;

    private static bool MigrateLegacySecret(AppSettings settings, bool legacyProductConfig, string dataRoot)
    {
        settings.NormalizeProviderProfiles();
        var profile = legacyProductConfig
            ? settings.ProviderProfiles[0]
            : settings.GetActiveProviderProfile();
        if (legacyProductConfig)
        {
            profile.ApiBase = string.IsNullOrWhiteSpace(settings.ApiBase)
                ? "https://api.openai.com/v1"
                : settings.ApiBase;
            profile.Model = string.IsNullOrWhiteSpace(settings.Model) ? "gpt-4o-mini" : settings.Model;
            if (Uri.TryCreate(profile.ApiBase, UriKind.Absolute, out var uri) && uri.IsLoopback)
            {
                profile.Type = ProviderType.Local;
            }
            settings.ActiveProviderProfileId = profile.Id;
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey)) return false;
        try
        {
            var store = new DpapiSecretStore(Path.Combine(dataRoot, "secrets"));
            store.Save(profile.SecretId, settings.ApiKey);
            settings.ApiKey = string.Empty;
            return true;
        }
        catch
        {
            // 加密写入失败时保留原字段，避免密钥丢失；下次加载会再次尝试迁移。
            return false;
        }
    }

    private void SanitizeLegacyConfig(AppSettings settings)
    {
        var legacyDirectory = Path.GetDirectoryName(LegacyConfigFilePath);
        if (string.IsNullOrWhiteSpace(legacyDirectory)) return;

        Directory.CreateDirectory(legacyDirectory);
        var temporaryPath = LegacyConfigFilePath + ".migrating";
        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, LegacyConfigFilePath, true);
    }
}
