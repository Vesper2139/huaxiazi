using System;
using System.Threading.Tasks;
using System.Windows;

namespace Huaxiazi.Services;

/// <summary>
/// 剪贴板服务：复制结果 / 读取剪贴板文字。
/// 在 WPF 中需确保在 UI 线程调用（Clipboard 要求 STA 线程）。
/// </summary>
public sealed class ClipboardService
{
    /// <summary>
    /// 将文本复制到系统剪贴板。需在 UI 线程调用。
    /// </summary>
    public void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"复制失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 读取剪贴板中的文本（无文本或失败返回空字符串）。
    /// 需要在 UI 线程调用。
    /// </summary>
    public string GetText()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                return Clipboard.GetText();
            }
        }
        catch (Exception)
        {
            // 忽略：剪贴板可能被其他进程锁定。
        }
        return string.Empty;
    }
}
