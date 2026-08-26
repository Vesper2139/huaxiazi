using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Automation;

namespace Huaxiazi.Views;

/// <summary>Prevents silent WPF integer binding failures and normalizes declared ranges on blur.</summary>
public static class IntegerInputBehavior
{
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.RegisterAttached(
        "Minimum", typeof(int), typeof(IntegerInputBehavior), new PropertyMetadata(int.MinValue, OnConstraintChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.RegisterAttached(
        "Maximum", typeof(int), typeof(IntegerInputBehavior), new PropertyMetadata(int.MaxValue, OnConstraintChanged));

    private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
        "IsHooked", typeof(bool), typeof(IntegerInputBehavior), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey IsOutOfRangePropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsOutOfRange", typeof(bool), typeof(IntegerInputBehavior), new PropertyMetadata(false));

    public static readonly DependencyProperty IsOutOfRangeProperty = IsOutOfRangePropertyKey.DependencyProperty;

    public static void SetMinimum(DependencyObject element, int value) => element.SetValue(MinimumProperty, value);
    public static int GetMinimum(DependencyObject element) => (int)element.GetValue(MinimumProperty);
    public static void SetMaximum(DependencyObject element, int value) => element.SetValue(MaximumProperty, value);
    public static int GetMaximum(DependencyObject element) => (int)element.GetValue(MaximumProperty);
    public static bool GetIsOutOfRange(DependencyObject element) => (bool)element.GetValue(IsOutOfRangeProperty);

    private static void OnConstraintChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        if (dependencyObject is not TextBox textBox || (bool)textBox.GetValue(IsHookedProperty)) return;
        textBox.SetValue(IsHookedProperty, true);
        textBox.PreviewTextInput += RejectNonDigits;
        textBox.TextChanged += ValidateDraft;
        textBox.LostKeyboardFocus += NormalizeRange;
        DataObject.AddPastingHandler(textBox, RejectInvalidPaste);
        ValidateDraft(textBox, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
    }

    private static void RejectNonDigits(object sender, TextCompositionEventArgs args) =>
        args.Handled = args.Text.Any(character => !char.IsDigit(character));

    private static void RejectInvalidPaste(object sender, DataObjectPastingEventArgs args)
    {
        if (!args.SourceDataObject.GetDataPresent(DataFormats.UnicodeText))
        {
            args.CancelCommand();
            return;
        }

        var text = args.SourceDataObject.GetData(DataFormats.UnicodeText) as string;
        if (string.IsNullOrWhiteSpace(text) || text.Any(character => !char.IsDigit(character))) args.CancelCommand();
    }

    private static void NormalizeRange(object sender, KeyboardFocusChangedEventArgs _)
    {
        if (sender is not TextBox textBox) return;
        var binding = BindingOperations.GetBindingExpression(textBox, TextBox.TextProperty);
        var minimum = GetMinimum(textBox);
        var maximum = Math.Max(minimum, GetMaximum(textBox));
        var value = int.TryParse(textBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : minimum;

        var normalized = Math.Clamp(value, minimum, maximum);
        textBox.Text = normalized.ToString(CultureInfo.InvariantCulture);
        binding?.UpdateSource();
        ValidateDraft(textBox, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
    }

    private static void ValidateDraft(object sender, TextChangedEventArgs _)
    {
        if (sender is not TextBox textBox) return;
        var minimum = GetMinimum(textBox);
        var maximum = Math.Max(minimum, GetMaximum(textBox));
        var outOfRange = !string.IsNullOrEmpty(textBox.Text) &&
            (!int.TryParse(textBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum);
        textBox.SetValue(IsOutOfRangePropertyKey, outOfRange);
        AutomationProperties.SetHelpText(textBox, outOfRange ? $"请输入 {minimum}–{maximum} 范围内的整数" : string.Empty);
    }
}
