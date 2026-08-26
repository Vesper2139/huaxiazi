using System;
using System.IO;
using System.Reflection;
using System.Windows;
using Huaxiazi.Services;

namespace Huaxiazi.Tests;

/// <summary>
/// 测试辅助：通过反射把“私有/只读”的字段/方法暴露给单元测试，
/// 从而无需修改被测源码即可注入临时路径、调用私有方法。
///
/// 说明（为什么用反射而不是直接改源码）：
///  - ConfigService 的配置路径是 private static readonly，且 Load→Save 默认写 %AppData%。
///    测试不能污染用户真实配置，因此用反射把 AppDataFolder / ConfigFilePath 重定向到临时目录。
///  - PromptBuilderService 的 _promptsDirectory 是 private readonly 实例字段，
///    用反射注入临时目录后，可精确控制“类别增强文件是否存在”以验证安全回退。
///  - AIService.ParseContent 是 private static，用反射调用以验证各种异常 JSON 的容错。
/// 以上均未改动工程师交付的 32 个源文件，属于“反射注入临时路径”的推荐策略（见任务说明）。
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// WPF keeps Application.Current in process-wide static state, while the UI
    /// contract tests intentionally create short-lived STA threads. Clear that
    /// state after a test thread exits so a later STA thread never reuses a
    /// dispatcher that has already shut down. This is test-host hygiene only;
    /// the product still owns one Application for its lifetime.
    /// </summary>
    public static void ResetWpfApplication()
    {
        var application = Application.Current;
        if (application is not null && ReferenceEquals(application.Dispatcher, System.Windows.Threading.Dispatcher.CurrentDispatcher))
        {
            foreach (Window window in application.Windows)
            {
                try { window.Close(); } catch { /* best effort during teardown */ }
            }
        }

        var applicationType = typeof(Application);
        applicationType.GetField("_appInstance", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, null);
        applicationType.GetField("_appCreatedInThisAppDomain", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
        applicationType.GetField("_isShuttingDown", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, false);
    }

    public static Application EnsureWpfApplication()
    {
        if (Application.Current is { } existing && !ReferenceEquals(existing.Dispatcher, System.Windows.Threading.Dispatcher.CurrentDispatcher))
            ResetWpfApplication();

        return Application.Current ?? new Application();
    }

    #region ConfigService —— 重定向静态配置路径

    public static void RedirectConfigTo(string directory, string? ignoredLegacyPath = null)
    {
        var t = typeof(ConfigService);
        var fApp = t.GetField("AppDataFolder", BindingFlags.NonPublic | BindingFlags.Static)
                   ?? throw new InvalidOperationException("未能找到 ConfigService.AppDataFolder 字段（可能源码已重构，请同步更新测试）");
        var fPath = t.GetField("ConfigFilePath", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException("未能找到 ConfigService.ConfigFilePath 字段（可能源码已重构，请同步更新测试）");

        fApp.SetValue(null, directory);
        fPath.SetValue(null, Path.Combine(directory, "config.json"));
    }

    public static void ResetConfigToDefault()
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Huaxiazi");
        var legacyPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Huaxiazi",
            "config.json");
        RedirectConfigTo(appData, legacyPath);
    }

    #endregion

    #region PromptBuilderService —— 注入实例级 prompts 目录

    public static PromptBuilderService CreateBuilderWithPromptsDir(string promptsDir)
    {
        var svc = new PromptBuilderService();
        var f = typeof(PromptBuilderService).GetField("_promptsDirectory", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("未能找到 PromptBuilderService._promptsDirectory 字段（可能源码已重构，请同步更新测试）");
        f.SetValue(svc, promptsDir);
        return svc;
    }

    #endregion

    #region AIService —— 调用私有静态 ParseContent

    public static string ParseContentViaReflection(string responseBody)
    {
        var m = typeof(AIService).GetMethod("ParseContent", BindingFlags.NonPublic | BindingFlags.Static)
               ?? throw new InvalidOperationException("未能找到 AIService.ParseContent 方法（可能源码已重构，请同步更新测试）");
        try
        {
            return (string)m.Invoke(null, new object[] { responseBody })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // 把私有方法内部抛出的真实异常原样透传，便于断言具体类型/消息
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            return string.Empty; // 不可达
        }
    }

    #endregion
}
