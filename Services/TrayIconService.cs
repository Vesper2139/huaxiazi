using System;
using System.Drawing;
using System.Windows.Forms;

namespace PromptFloat.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public TrayIconService(Action open, Action settings, Action exit)
    {
        _notifyIcon = new NotifyIcon
        {
            Text = "Vesper",
            Icon = AppIconService.LoadTrayIcon(),
            Visible = false,
            ContextMenuStrip = new ContextMenuStrip()
        };
        _notifyIcon.ContextMenuStrip.Items.Add("打开", null, (_, _) => open());
        _notifyIcon.ContextMenuStrip.Items.Add("系统设置", null, (_, _) => settings());
        _notifyIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _notifyIcon.ContextMenuStrip.Items.Add("退出", null, (_, _) => exit());
        _notifyIcon.DoubleClick += (_, _) => open();
    }

    public void Show() => _notifyIcon.Visible = true;

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
