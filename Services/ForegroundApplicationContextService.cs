using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PromptFloat.Services;

public sealed record SourceApplicationContext(string Channel, string Scenario, string Evidence);

/// <summary>Reads only the foreground process name and immediately reduces it to coarse context.</summary>
public static class ForegroundApplicationContextService
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    public static SourceApplicationContext Capture()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return Empty();
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0) return Empty();
            using var process = Process.GetProcessById((int)processId);
            return FromProcessName(process.ProcessName);
        }
        catch
        {
            return Empty();
        }
    }

    public static SourceApplicationContext FromProcessName(string? processName)
    {
        var name = processName?.Trim().ToUpperInvariant() ?? string.Empty;
        if (name.EndsWith(".EXE", StringComparison.Ordinal)) name = name[..^4];
        return name switch
        {
            "OUTLOOK" or "HXL" or "THUNDERBIRD" => new("邮件", "职场沟通", "来源应用类型：邮件客户端"),
            "WXWORK" or "WEWORK" => new("企业微信", "职场沟通", "来源应用类型：企业通讯"),
            "DINGTALK" => new("钉钉", "职场沟通", "来源应用类型：企业通讯"),
            "FEISHU" or "LARK" => new("飞书", "职场沟通", "来源应用类型：企业通讯"),
            "TEAMS" or "MSTEAMS" => new("Teams", "职场沟通", "来源应用类型：企业通讯"),
            "SLACK" => new("Slack", "职场沟通", "来源应用类型：企业通讯"),
            "WECHAT" => new("微信", "", "来源应用类型：即时通讯"),
            "QQ" or "TELEGRAM" => new("即时通讯", "", "来源应用类型：即时通讯"),
            "CODE" or "DEVENV" or "RIDER64" or "IDEA64" or "PYCHARM64" or "WEBSTORM64" =>
                new("代码编辑器", "", "来源应用类型：代码编辑器"),
            "WINWORD" or "WPS" => new("文档", "", "来源应用类型：文档编辑器"),
            _ => Empty()
        };
    }

    private static SourceApplicationContext Empty() => new("", "", "");
}
