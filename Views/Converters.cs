using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Huaxiazi.Models;
using Huaxiazi.Services;
using Huaxiazi.ViewModels;

namespace Huaxiazi.Views;

/// <summary>
/// 将 SelectedCategory 与某个 ToggleButton 的类别参数比较，返回是否选中。
/// 用于方向选择 ToggleButton 的 IsChecked 单向绑定。
/// </summary>
public sealed class CategoryToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PromptCategory selected && parameter is PromptCategory target)
        {
            return selected == target;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 将 SelectedDepth 与某个 ToggleButton 的深度参数比较，返回是否选中。
/// </summary>
public sealed class DepthToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is PromptDepth selected && parameter is PromptDepth target)
        {
            return selected == target;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// bool -> Visibility。
/// 参数传 "Inverse" 时取反（true -> Collapsed）。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        var inverse = parameter is string p && p.Equals("Inverse", StringComparison.OrdinalIgnoreCase);
        var visible = inverse ? !b : b;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// bool -> bool 取反。用于按钮 IsEnabled 绑定（IsBusy 时禁用）。
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return !(value is bool v && v);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return !(value is bool v && v);
    }
}

/// <summary>
/// IsBusy -> 按钮文字；图标由 XAML 的统一矢量资源负责。
/// </summary>
public sealed class BusyTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var busy = value is bool v && v;
        return busy ? "优化中" : "优化";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 将 PromptCategory / PromptDepth 枚举转换为中文显示名（用于 ComboBox）。
/// </summary>
public sealed class EnumDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            PromptCategory c => c.GetDisplayName(),
            PromptDepth d => d.GetDisplayName(),
            _ => value?.ToString() ?? string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class ProviderPlatformDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ProviderPlatform platform
            ? ProviderPlatformCatalog.Get(platform).DisplayName
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// 将当前 ViewMode 与某个 ToggleButton 的目标模式比较，返回是否选中。
/// 用于「原文 / 优化后」分段切换 ToggleButton 的 IsChecked 单向绑定。
/// </summary>
public sealed class ViewModeToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ViewMode selected && parameter is ViewMode target)
        {
            return selected == target;
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

/// <summary>
/// 集合数量为 0 时显示（Visible），否则折叠。用于历史记录空态占位。
/// </summary>
public sealed class CountZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class SectionVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// 将 ProviderProfileViewMode 转换为对应的视图实例，供 SettingsView 的 ContentControl 切换。
/// </summary>
public sealed class ProviderProfileViewModeToContentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is ProviderProfileViewMode.Edit
            ? new ProviderProfileEditView()
            : new ProviderProfileListView();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>
/// 将配置项与 SettingsViewModel 组合，返回该配置的 API Key 状态画刷（Set=SuccessBrush，其他=MutedBrush）。
/// 用于配置列表卡片的密钥状态点。
/// </summary>
public sealed class ProfileApiKeyStatusConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is [ProviderProfile profile, SettingsViewModel vm])
        {
            var kind = vm.GetApiKeyStatusKind(profile);
            var resourceKey = kind == ApiKeyStatusKind.Set ? "SuccessBrush" : "MutedBrush";
            return Application.Current?.TryFindResource(resourceKey) as Brush ?? Brushes.Gray;
        }
        return Application.Current?.TryFindResource("MutedBrush") as Brush ?? Brushes.Gray;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>配置列表中当前生效配置的可见性。</summary>
public sealed class ProviderActiveVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var active = values is [ProviderProfile profile, SettingsViewModel vm] &&
            string.Equals(profile.Id, vm.ActiveProviderProfileId, StringComparison.Ordinal);
        return active ? Visibility.Visible : Visibility.Collapsed;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>“设为当前”按钮仅对尚未生效的配置可用。</summary>
public sealed class ProviderActiveEnabledConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var active = values is [ProviderProfile profile, SettingsViewModel vm] &&
            string.Equals(profile.Id, vm.ActiveProviderProfileId, StringComparison.Ordinal);
        return !active;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
