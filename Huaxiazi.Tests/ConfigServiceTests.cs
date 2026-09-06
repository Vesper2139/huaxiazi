using System;
using System.IO;
using System.Text.Json;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

/// <summary>
/// 验证 ConfigService：Load→Save→Load 往返稳定性、历史超 20 条、可空窗口坐标（null / 数值）、
/// 旧版 NaN 坐标向后兼容迁移、中文默认值反序列化、缺省文件回退、空引用保护、
/// 与 default-config.json 的一致性（单一权威默认值来源）。
///
/// 通过反射把静态配置路径重定向到临时目录，避免污染用户真实 %AppData%\Huaxiazi\config.json。
/// </summary>
public class ConfigServiceTests
{
    private static string MakeTempConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "HuaxiaziCfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Load_ConfigWithInvalidDataDirectory_FallsBackToDefaultDataRoot()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), """
                {
                  "configVersion": 20,
                  "dataDirectory": "bad\u0000path"
                }
                """);

            var loaded = new ConfigService().Load();

            Assert.Equal(string.Empty, loaded.DataDirectory);
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact(Skip = "Huaxiazi 2.0.0 intentionally does not migrate older configuration schemas.")]
    public void Load_Version5FactoryDisplayDefaults_AdoptsSystemThemeAndCompactHeight()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), """
            {
              "configVersion": 5,
              "themeMode": "Dark",
              "editorDefaultHeight": 120,
              "mainWindowHeight": 176
            }
            """);

            var loaded = new ConfigService().Load();

            Assert.Equal("System", loaded.ThemeMode);
            Assert.Equal(36, loaded.EditorDefaultHeight);
            Assert.Equal(176, loaded.MainWindowHeight);
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact(Skip = "Huaxiazi 2.0.0 intentionally does not migrate older configuration schemas.")]
    public void Load_Version6FactoryHotkeys_MigratesToOneLowConflictDefault()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), """
            {
              "configVersion": 6,
              "hotkey": "Ctrl+Shift+P",
              "quickPolishHotkey": "Ctrl+Alt+1",
              "quickPromptHotkey": "Ctrl+Alt+2",
              "copyResultHotkey": "Ctrl+Alt+3"
            }
            """);

            var loaded = new ConfigService().Load();

            Assert.Equal("Ctrl+Shift+H", loaded.Hotkey);
            Assert.Equal(string.Empty, loaded.QuickPolishHotkey);
            Assert.Equal(string.Empty, loaded.QuickPromptHotkey);
            Assert.Equal(string.Empty, loaded.CopyResultHotkey);
            Assert.Equal(ConfigService.CurrentConfigVersion, loaded.ConfigVersion);
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact(Skip = "Huaxiazi 2.0.0 intentionally does not migrate older configuration schemas.")]
    public void Load_Version6CustomHotkeys_PreservesUserChoices()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), """
            {
              "configVersion": 6,
              "hotkey": "Ctrl+K",
              "quickPolishHotkey": "Alt+F8",
              "quickPromptHotkey": "",
              "copyResultHotkey": ""
            }
            """);

            var loaded = new ConfigService().Load();

            Assert.Equal("Ctrl+K", loaded.Hotkey);
            Assert.Equal("Alt+F8", loaded.QuickPolishHotkey);
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact(Skip = "Huaxiazi 2.0.0 intentionally does not migrate older configuration schemas.")]
    public void Load_Version3Coordinates_MigratesOnlyToBallPositionAndAddsWindowSize()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), """
            {
              "configVersion": 3,
              "windowLeft": 1810,
              "windowTop": 920
            }
            """);

            var loaded = new ConfigService().Load();

            Assert.Equal(ConfigService.CurrentConfigVersion, loaded.ConfigVersion);
            Assert.Equal(1810, loaded.BallLeft);
            Assert.Equal(920, loaded.BallTop);
            Assert.Equal(520, loaded.MainWindowWidth);
            Assert.Equal(176, loaded.MainWindowHeight);
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact(Skip = "2.0.0 rejects plaintext API keys; secrets must be written through the DPAPI profile flow.")]
    public void Load_ExistingValidConfig_RoundtripsAllFields()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            var json = @"{
  ""configVersion"": 20,
  ""apiBase"": ""http://localhost:11434/v1"",
  ""apiKey"": ""sk-test-123"",
  ""model"": ""deepseek-chat"",
  ""defaultCategory"": ""编程开发"",
  ""defaultDepth"": ""详细"",
  ""hotkey"": ""Ctrl+K"",
  ""windowLeft"": 120.5,
  ""windowTop"": 64.25,
  ""history"": [""a"", ""b"", ""c""]
}";
            File.WriteAllText(Path.Combine(dir, "config.json"), json);

            var svc = new ConfigService();
            var loaded = svc.Load();

            Assert.Equal("http://localhost:11434/v1", loaded.ApiBase);
            Assert.Equal(string.Empty, loaded.ApiKey);
            Assert.Equal(
                "sk-test-123",
                new DpapiSecretStore(Path.Combine(dir, "secrets"))
                    .Read(loaded.GetActiveProviderProfile().SecretId));
            Assert.Equal("deepseek-chat", loaded.Model);
            Assert.Equal("编程开发", loaded.DefaultCategory);
            Assert.Equal("详细", loaded.DefaultDepth);
            Assert.Equal("Ctrl+K", loaded.Hotkey);
            Assert.Equal(120.5, loaded.WindowLeft!.Value);
            Assert.Equal(64.25, loaded.WindowTop!.Value);
            Assert.Equal(3, loaded.History.Count);
            Assert.Equal("a", loaded.History[0]);
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_ThenLoad_PreservesNormalValues()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            var svc = new ConfigService();
            var settings = new AppSettings
            {
                ApiBase = "https://api.openai.com/v1",
                ApiKey = string.Empty,
                Model = "gpt-4o-mini",
                DefaultCategory = "学术研究",
                DefaultDepth = "标准",
                Hotkey = "Ctrl+Shift+P",
                WindowLeft = 10,
                WindowTop = 20,
                History = new() { "h1", "h2" }
            };

            svc.Save(settings);
            var loaded = svc.Load();

            Assert.Equal("学术研究", loaded.DefaultCategory);
            Assert.Equal("标准", loaded.DefaultDepth);
            Assert.Equal(10, loaded.WindowLeft!.Value);
            Assert.Equal(20, loaded.WindowTop!.Value);
            Assert.Equal(2, loaded.History.Count);
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_ReplacesStaleTemporaryFileAndLeavesValidConfig()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            var temporaryPath = Path.Combine(dir, "config.json.tmp");
            File.WriteAllText(temporaryPath, "partial-json");

            new ConfigService().Save(new AppSettings { Hotkey = "Ctrl+K" });

            Assert.False(File.Exists(temporaryPath));
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "config.json")));
            Assert.Equal("Ctrl+K", document.RootElement.GetProperty("hotkey").GetString());
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_DirectCallerNeverPersistsApiKeyToConfig()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            var settings = new AppSettings { ApiKey = "sk-synthetic-test-value-never-persist" };

            new ConfigService().Save(settings);

            var json = File.ReadAllText(Path.Combine(dir, "config.json"));
            Assert.DoesNotContain("sk-synthetic-test-value-never-persist", json, StringComparison.Ordinal);
            Assert.Equal(string.Empty, JsonDocument.Parse(json).RootElement.GetProperty("apiKey").GetString());
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_ThenLoad_PreservesHistoryBeyondMax()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            var svc = new ConfigService();
            var settings = new AppSettings
            {
                // 明确给出窗口坐标，使本测试只验证“历史 > 20 条”的往返
                WindowLeft = 1,
                WindowTop = 2
            };
            for (int i = 0; i < 25; i++)
                settings.History.Add($"item{i}");

            svc.Save(settings);
            var loaded = svc.Load();

            // ConfigService 本身不做截断（截断由 UI / MainViewModel 负责），应原样保留
            Assert.Equal(25, loaded.History.Count);
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_NullSettings_IsNoOpAndDoesNotThrow()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            var svc = new ConfigService();
            var ex = Record.Exception(() => svc.Save(null!));
            Assert.Null(ex);
            Assert.False(File.Exists(Path.Combine(dir, "config.json")));
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    // ===== 验证：窗口坐标以可空 double 表达（null = 未记忆）；旧版 NaN 哨兵值应被迁移归一化为 null =====

    [Fact]
    public void SaveLoad_RoundtripsNullWindowCoords()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            var svc = new ConfigService();
            var settings = new AppSettings { WindowLeft = null, WindowTop = null };

            svc.Save(settings);
            var loaded = svc.Load();

            // null 坐标应原样往返，且不因 AllowNamedFloatingPointLiterals 被改写成 NaN
            Assert.False(loaded.WindowLeft.HasValue);
            Assert.False(loaded.WindowTop.HasValue);
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_NonFiniteWindowAndBallCoordinates_PersistsNulls()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            var settings = new AppSettings
            {
                WindowLeft = double.PositiveInfinity,
                WindowTop = double.NaN,
                BallLeft = double.NegativeInfinity,
                BallTop = double.NaN
            };

            new ConfigService().Save(settings);

            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "config.json")));
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("windowLeft").ValueKind);
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("windowTop").ValueKind);
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("ballLeft").ValueKind);
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("ballTop").ValueKind);
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact(Skip = "Huaxiazi 2.0.0 intentionally does not migrate older configuration schemas.")]
    public void Load_LegacyNaNWindowCoords_MigratedToNull()
    {
        // 模拟旧版 (v0) 把“未记忆位置”存为 double.NaN 的 config.json；
        // Load 时应通过 Migrate 把 NaN 归一化为 null，并保持对终端用户的向后兼容。
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            var legacy = @"{
  ""apiBase"": ""https://api.openai.com/v1"",
  ""model"": ""gpt-4o-mini"",
  ""windowLeft"": NaN,
  ""windowTop"": NaN
}";
            File.WriteAllText(Path.Combine(dir, "config.json"), legacy);

            var svc = new ConfigService();
            var loaded = svc.Load();

            Assert.False(loaded.WindowLeft.HasValue);
            Assert.False(loaded.WindowTop.HasValue);
            // 迁移后版本号应提升至当前版本
            Assert.Equal(ConfigService.CurrentConfigVersion, loaded.ConfigVersion);
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact(Skip = "Huaxiazi 2.0.0 intentionally does not migrate older configuration schemas.")]
    public void Load_V1Config_UpgradesProductDefaultsToVersion2()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), """
                {
                  "configVersion": 1,
                  "enabledModes": [],
                  "defaultMode": "PromptOptimize",
                  "providerProfiles": [],
                  "activeProviderProfileId": "missing"
                }
                """);

            var loaded = new ConfigService().Load();

            Assert.Equal(ConfigService.CurrentConfigVersion, loaded.ConfigVersion);
            Assert.Equal(ApplicationMode.PromptOptimize, loaded.DefaultMode);
            Assert.Equal([ApplicationMode.Polish, ApplicationMode.PromptOptimize], loaded.EnabledModes);
            Assert.Single(loaded.ProviderProfiles);
            Assert.Equal(loaded.ProviderProfiles[0].Id, loaded.ActiveProviderProfileId);
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact(Skip = "Huaxiazi 2.0.0 intentionally does not migrate older configuration schemas.")]
    public void Load_V2Config_NormalizesUnsupportedThemeToDark()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), """
                {
                  "configVersion": 2,
                  "themeMode": "System",
                  "accentTheme": "MistBlue"
                }
                """);

            var loaded = new ConfigService().Load();

            Assert.Equal(ConfigService.CurrentConfigVersion, loaded.ConfigVersion);
            Assert.Equal("Dark", loaded.ThemeMode);
            using var rewritten = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "config.json")));
            Assert.Equal(ConfigService.CurrentConfigVersion, rewritten.RootElement.GetProperty("configVersion").GetInt32());
            Assert.Equal("Dark", rewritten.RootElement.GetProperty("themeMode").GetString());
            Assert.False(rewritten.RootElement.TryGetProperty("accentTheme", out _));
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact(Skip = "Huaxiazi 2.0.0 intentionally does not migrate older configuration schemas.")]
    public void Load_V1Config_EncryptsLegacyApiKeyAndRewritesConfigWithoutPlaintext()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), """
                {
                  "configVersion": 1,
                  "apiBase": "http://localhost:11434/v1",
                  "apiKey": "sk-legacy-sensitive",
                  "model": "qwen2.5"
                }
                """);

            var loaded = new ConfigService().Load();

            Assert.Equal(string.Empty, loaded.ApiKey);
            var active = loaded.GetActiveProviderProfile();
            Assert.Equal("http://localhost:11434/v1", active.ApiBase);
            Assert.Equal("qwen2.5", active.Model);
            Assert.Equal(ProviderType.Local, active.Type);
            Assert.Equal("sk-legacy-sensitive", new DpapiSecretStore(Path.Combine(dir, "secrets")).Read(active.SecretId));
            Assert.DoesNotContain("sk-legacy-sensitive", File.ReadAllText(Path.Combine(dir, "config.json")));
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact(Skip = "Huaxiazi 2.0.0 intentionally does not migrate older configuration schemas.")]
    public void Load_V1Config_WithCustomDataDirectory_MigratesSecretToCustomPath()
    {
        var configDir = MakeTempConfigDir();
        var customDataDir = Path.Combine(configDir, "custom_data");
        Directory.CreateDirectory(customDataDir);
        try
        {
            TestHelpers.RedirectConfigTo(configDir);
            File.WriteAllText(Path.Combine(configDir, "config.json"), """
                {
                  "configVersion": 1,
                  "dataDirectory": "${customDataDir}",
                  "apiBase": "https://api.example.com/v1",
                  "apiKey": "sk-custom-path-test",
                  "model": "test-model"
                }
                """.Replace("${customDataDir}", customDataDir.Replace("\\", "\\\\")));

            var loaded = new ConfigService().Load();

            Assert.Equal(string.Empty, loaded.ApiKey);
            var active = loaded.GetActiveProviderProfile();
            Assert.Equal(
                "sk-custom-path-test",
                new DpapiSecretStore(Path.Combine(customDataDir, "secrets")).Read(active.SecretId));
            Assert.DoesNotContain("sk-custom-path-test", File.ReadAllText(Path.Combine(configDir, "config.json")));
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(configDir, true);
        }
    }

    [Fact(Skip = "Huaxiazi 2.0.0 intentionally does not migrate older configuration schemas.")]
    public void Load_WhenNewLocationIsEmpty_ImportsOldHuaxiaziConfigAndSanitizesIt()
    {
        var root = MakeTempConfigDir();
        var newDir = Path.Combine(root, "new");
        var oldDir = Path.Combine(root, "old", "Huaxiazi");
        Directory.CreateDirectory(oldDir);
        var oldPath = Path.Combine(oldDir, "config.json");
        try
        {
            TestHelpers.RedirectConfigTo(newDir, oldPath);
            File.WriteAllText(oldPath, """
                {
                  "configVersion": 1,
                  "apiBase": "https://example.test/v1",
                  "apiKey": "sk-from-old-location",
                  "model": "legacy-model"
                }
                """);

            var loaded = new ConfigService().Load();

            Assert.Equal(ConfigService.CurrentConfigVersion, loaded.ConfigVersion);
            Assert.True(File.Exists(Path.Combine(newDir, "config.json")));
            Assert.DoesNotContain("sk-from-old-location", File.ReadAllText(Path.Combine(newDir, "config.json")));
            Assert.DoesNotContain("sk-from-old-location", File.ReadAllText(oldPath));
            Assert.Equal(
                "sk-from-old-location",
                new DpapiSecretStore(Path.Combine(newDir, "secrets"))
                    .Read(loaded.GetActiveProviderProfile().SecretId));
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Load_OlderConfigIsIgnoredAndLeftUntouched()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            var path = Path.Combine(dir, "config.json");
            const string oldJson = "{\"configVersion\":11,\"apiKey\":\"must-not-be-read\",\"model\":\"old-model\"}";
            File.WriteAllText(path, oldJson);

            var loaded = new ConfigService().Load();

            Assert.Equal(ConfigService.CurrentConfigVersion, loaded.ConfigVersion);
            Assert.Equal(string.Empty, loaded.ApiKey);
            Assert.NotEqual("old-model", loaded.Model);
            Assert.DoesNotContain("must-not-be-read", File.ReadAllText(path));
            var backup = File.ReadAllText(path + ".v11.ignored");
            Assert.Contains("[REDACTED]", backup);
            Assert.DoesNotContain("must-not-be-read", backup);
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Load_WhenFileAbsent_CreatesDefaultConfig()
    {
        var dir = MakeTempConfigDir();
        try
        {
            TestHelpers.RedirectConfigTo(dir);
            var svc = new ConfigService();

            var loaded = svc.Load(); // Bug #1 已修复：AllowNamedFloatingPointLiterals 允许序列化 NaN，Save 不再抛异常

            Assert.NotNull(loaded);
            Assert.Equal("通用任务", loaded.DefaultCategory);
            Assert.Equal("标准", loaded.DefaultDepth);
            Assert.True(File.Exists(Path.Combine(dir, "config.json")));
        }
        finally
        {
            TestHelpers.ResetConfigToDefault();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RedactSensitiveJson_CoversSnakeCaseAndCompoundTokenNames()
    {
        const string raw = """
            {
              "api_key": "api-secret",
              "accessToken": "access-secret",
              "refresh_token": "refresh-secret",
              "safe": "visible"
            }
            """;

        var redacted = ConfigService.RedactSensitiveJson(raw);

        Assert.NotNull(redacted);
        Assert.DoesNotContain("api-secret", redacted);
        Assert.DoesNotContain("access-secret", redacted);
        Assert.DoesNotContain("refresh-secret", redacted);
        Assert.Contains("visible", redacted);
    }

    [Fact]
    public void CreateDefault_MatchesBundledDefaultConfigJson_Exactly()
    {
        // CreateDefault() 以 bundled default-config.json 为唯一权威来源，二者应逐字段完全一致。
        var path = Path.Combine(AppContext.BaseDirectory, "default-config.json");
        Assert.True(File.Exists(path), "default-config.json 未随测试输出拷贝，请检查 .csproj 拷贝设置");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var defaults = new ConfigService().CreateDefault();

        Assert.Equal(root.GetProperty("apiBase").GetString(), defaults.ApiBase);
        Assert.Equal(root.GetProperty("apiKey").GetString(), defaults.ApiKey);
        Assert.Equal(root.GetProperty("model").GetString(), defaults.Model);
        Assert.Equal(root.GetProperty("defaultCategory").GetString(), defaults.DefaultCategory);
        Assert.Equal(root.GetProperty("defaultDepth").GetString(), defaults.DefaultDepth);
        Assert.Equal(root.GetProperty("hotkey").GetString(), defaults.Hotkey);
        Assert.Equal(root.GetProperty("quickPolishHotkey").GetString(), defaults.QuickPolishHotkey);
        Assert.Equal(root.GetProperty("quickPromptHotkey").GetString(), defaults.QuickPromptHotkey);
        Assert.Equal(root.GetProperty("copyResultHotkey").GetString(), defaults.CopyResultHotkey);
        Assert.Equal(root.GetProperty("userPersona").GetString(), defaults.UserPersona);

        // 窗口坐标未记忆时为 null（JSON null），与 bundled 文件完全一致，不再有 NaN/0.0 歧义
        Assert.Null(defaults.WindowLeft);
        Assert.Null(defaults.WindowTop);
        Assert.True(root.GetProperty("windowLeft").ValueKind == JsonValueKind.Null);
        Assert.True(root.GetProperty("windowTop").ValueKind == JsonValueKind.Null);

        // 配置版本号与 bundled 文件、与当前版本三者一致
        Assert.Equal(ConfigService.CurrentConfigVersion, defaults.ConfigVersion);
        Assert.Equal(root.GetProperty("configVersion").GetInt32(), defaults.ConfigVersion);
    }
}
