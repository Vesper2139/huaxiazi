using System;
using System.Drawing;
using System.IO;

namespace PromptFloat.Services;

public static class AppIconService
{
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Resources", "Brand", "Vesper.ico");

    public static Icon LoadTrayIcon()
    {
        if (File.Exists(IconPath))
        {
            using var icon = new Icon(IconPath);
            return (Icon)icon.Clone();
        }

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            using var extracted = Icon.ExtractAssociatedIcon(executable);
            if (extracted is not null) return (Icon)extracted.Clone();
        }
        return (Icon)SystemIcons.Application.Clone();
    }
}
