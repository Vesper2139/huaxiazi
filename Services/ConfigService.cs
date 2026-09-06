using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>
/// 配置读写服务。
/// 负责从 %LocalAppData%\Huaxiazi\config.json 读取配置；不存在时使用默认值并写入。
/// </summary>
public sealed class ConfigService
{
    internal const int MaxCorruptBackups = 3;
    /// <summary>
    /// 当前配置结构版本号。2.0.0 是全新数据边界；版本不匹配的文件不会被读取或迁移。
    /// </summary>
    public const int CurrentConfigVersion = 20;

    // 注意：此处未用 readonly，以便单元测试通过反射把路径重定向到临时目录做隔离（见 Huaxiazi.Tests/TestHelpers.cs）。
    private static string AppDataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Huaxiazi");

    private static string ConfigFilePath = Path.Combine(AppDataFolder, "config.json");

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

        try
        {
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
        // A 2.0.0 installation is intentionally a clean break. Do not reinterpret
        // an older config or move secrets from it; leave the file untouched and
        // start with a fresh profile instead.
        if (settings is not null && settings.ConfigVersion != CurrentConfigVersion)
        {
            PreserveObsoleteConfig(sourcePath, settings.ConfigVersion);
            settings = null;
        }

        // Plaintext keys are never accepted in the 2.0.0 config contract.
        if (settings is not null && !string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            PreserveObsoleteConfig(sourcePath, settings.ConfigVersion);
            settings = null;
        }

        if (settings is not null)
        {
            settings.History ??= new List<string>();
            if (!DataDirectoryPolicy.TryResolve(settings.DataDirectory, AppDataFolder, verifyWritable: false, out _, out _))
            {
                settings.DataDirectory = string.Empty;
            }
            settings.NormalizeProductModes();
            settings.NormalizeProviderProfiles();
            if (settings.NormalizeLoadedCredentialBindings())
            {
                try { Save(settings); } catch { /* keep the sanitized in-memory binding */ }
            }
            settings.NormalizeResidentEntrypoints();
            settings.NormalizePromptSettings();
            settings.NormalizeDisplaySettings();
            settings.ExpressionPreferenceProfile ??= new ExpressionPreferenceProfile();
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

    private static void PreserveObsoleteConfig(string sourcePath, int version)
    {
        try
        {
            if (!File.Exists(sourcePath)) return;
            var backupPath = $"{sourcePath}.v{version}.ignored";
            if (!File.Exists(backupPath))
            {
                var raw = File.ReadAllText(sourcePath);
                File.WriteAllText(backupPath, RedactSensitiveJson(raw) ?? "{\"notice\":\"旧配置未导入\"}");
            }
        }
        catch
        {
            // A backup is best effort; the active 2.0.0 config still starts cleanly.
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

    private static bool IsSensitiveProperty(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("token", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase);
    }

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
            settings.ConfigVersion = CurrentConfigVersion;
            // 防御纵深：任何调用方直接保存配置时，也不得把密钥写入 config.json。
            // API Key 的唯一持久化位置是 DPAPI SecretStore。
            settings.ApiKey = string.Empty;
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

}
