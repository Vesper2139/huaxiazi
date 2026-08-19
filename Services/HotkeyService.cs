using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Collections.Generic;
using System.Linq;

namespace PromptFloat.Services;

public enum GlobalHotkeyAction
{
    ToggleWindow = 0,
    QuickPolish = 1,
    QuickPromptOptimize = 2,
    CopyResult = 3
}

public sealed record HotkeyBindingValidation(bool IsValid, string ErrorMessage);

public sealed record HotkeyActionRegistration(bool Succeeded, string Message, bool IsConfigured = true);

public sealed class HotkeyRegistrationResult
{
    public required IReadOnlyDictionary<GlobalHotkeyAction, HotkeyActionRegistration> Actions { get; init; }
    public bool HasConfiguredActions => Actions.Values.Any(item => item.IsConfigured);
    public bool AllSucceeded => Actions.Values.Where(item => item.IsConfigured).All(item => item.Succeeded);
    public string Summary => !HasConfiguredActions
        ? "未配置全局快捷键"
        : AllSucceeded
        ? "已配置的快捷键均已注册。"
        : string.Join("；", Actions.Where(item => item.Value.IsConfigured && !item.Value.Succeeded)
            .Select(item => $"{item.Key}: {item.Value.Message}"));
}

public static class HotkeyRegistrationBatch
{
    public static HotkeyRegistrationResult Execute(
        IReadOnlyDictionary<GlobalHotkeyAction, string> bindings,
        Func<GlobalHotkeyAction, HotkeyCombination, bool> register)
    {
        var validation = HotkeyBindingSet.Validate(bindings);
        if (!validation.IsValid) throw new FormatException(validation.ErrorMessage);
        var actions = new Dictionary<GlobalHotkeyAction, HotkeyActionRegistration>();
        foreach (var (action, text) in bindings)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                actions[action] = new(false, "未配置", false);
                continue;
            }
            HotkeyParser.TryParse(text, out var combination);
            var succeeded = register(action, combination);
            actions[action] = succeeded
                ? new(true, "已注册")
                : new(false, "已被其他程序占用或系统拒绝注册");
        }
        return new HotkeyRegistrationResult { Actions = actions };
    }
}

public static class HotkeyBindingSet
{
    public static string GetActionName(GlobalHotkeyAction action) => action switch
    {
        GlobalHotkeyAction.ToggleWindow => "呼出 / 隐藏",
        GlobalHotkeyAction.QuickPolish => "快速润色",
        GlobalHotkeyAction.QuickPromptOptimize => "Prompt 优化",
        GlobalHotkeyAction.CopyResult => "复制当前结果",
        _ => "快捷键"
    };

    public static HotkeyBindingValidation Validate(IReadOnlyDictionary<GlobalHotkeyAction, string> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var used = new Dictionary<(ModifierKeys Modifiers, Key Key), GlobalHotkeyAction>();
        foreach (var (action, text) in bindings)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!HotkeyParser.TryParse(text, out var combination))
                return new(false, $"「{GetActionName(action)}」快捷键格式无效。");
            var key = (combination.Modifiers, combination.Key);
            if (used.TryGetValue(key, out var existing))
                return new(false, $"快捷键冲突：此组合已经用于「{GetActionName(existing)}」，不能同时分配给「{GetActionName(action)}」。");
            used[key] = action;
        }
        return new(true, string.Empty);
    }
}

public sealed class GlobalHotkeyEventArgs(GlobalHotkeyAction action) : EventArgs
{
    public GlobalHotkeyAction Action { get; } = action;
}

public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x4858;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int id);

    private HwndSource? _messageSource;
    private readonly Dictionary<int, GlobalHotkeyAction> _registered = [];

    public event EventHandler? HotkeyPressed;
    public event EventHandler<GlobalHotkeyEventArgs>? ActionPressed;

    public HotkeyRegistrationResult SetHotkey(string hotkeyText)
        => SetHotkeys(new Dictionary<GlobalHotkeyAction, string> { [GlobalHotkeyAction.ToggleWindow] = hotkeyText });

    public HotkeyRegistrationResult SetHotkeys(IReadOnlyDictionary<GlobalHotkeyAction, string> bindings)
    {
        var validation = HotkeyBindingSet.Validate(bindings);
        if (!validation.IsValid) throw new FormatException(validation.ErrorMessage);

        Stop();
        EnsureMessageSource();
        return HotkeyRegistrationBatch.Execute(bindings, (action, combination) =>
        {
            var id = HotkeyId + (int)action;
            var modifiers = ToNativeModifiers(combination.Modifiers) | ModNoRepeat;
            var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(combination.Key);
            if (!RegisterHotKey(_messageSource!.Handle, id, modifiers, virtualKey))
                return false;
            _registered[id] = action;
            return true;
        });
    }

    public void Stop()
    {
        if (_messageSource is not null)
        {
            foreach (var id in _registered.Keys.ToArray()) UnregisterHotKey(_messageSource.Handle, id);
            _registered.Clear();
        }
    }

    private void EnsureMessageSource()
    {
        if (_messageSource is not null) return;
        var parameters = new HwndSourceParameters("HuaxiaziHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3)
        };
        _messageSource = new HwndSource(parameters);
        _messageSource.AddHook(WindowProcedure);
    }

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            if (action == GlobalHotkeyAction.ToggleWindow) HotkeyPressed?.Invoke(this, EventArgs.Empty);
            ActionPressed?.Invoke(this, new GlobalHotkeyEventArgs(action));
        }
        return IntPtr.Zero;
    }

    private static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        uint value = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) value |= ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) value |= ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) value |= ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) value |= ModWin;
        return value;
    }

    public void Dispose()
    {
        Stop();
        if (_messageSource is not null)
        {
            _messageSource.RemoveHook(WindowProcedure);
            _messageSource.Dispose();
            _messageSource = null;
        }
    }
}
