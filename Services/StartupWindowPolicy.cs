using System;
using System.Collections.Generic;

namespace Huaxiazi.Services;

internal static class StartupWindowPolicy
{
    public static bool ShouldShowMainWindow(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var showMain = false;
        var background = false;

        foreach (var argument in arguments)
        {
            if (string.Equals(argument, "--show-main", StringComparison.OrdinalIgnoreCase)) showMain = true;
            if (string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase)) background = true;
        }

        return showMain || !background;
    }
}
