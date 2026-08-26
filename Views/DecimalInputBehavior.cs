using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace PromptFloat.Views;

/// <summary>Provides bounded decimal editing with immediate, visible validation feedback.</summary>
public static class DecimalInputBehavior
{
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.RegisterAttached(
        "Minimum", typeof(double), typeof(DecimalInputBehavior), new PropertyMetadata(double.MinValue, OnConstraintChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.RegisterAttached(
        "Maximum", typeof(double), typeof(DecimalInputBehavior), new PropertyMetadata(double.MaxValue, OnConstraintChanged));

    private static readonly DependencyProperty IsHookedProperty = DependencyProperty.RegisterAttached(
        "IsHooked", typeof(bool), typeof(DecimalInputBehavior), new PropertyMetadata(false));

    private static readonly DependencyPropertyKey IsOutOfRangePropertyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsOutOfRange", typeof(bool), typeof(DecimalInputBehavior), new PropertyMetadata(false));

    public static readonly DependencyProperty IsOutOfRangeProperty = IsOutOfRangePropertyKey.DependencyProperty;

    public static void SetMinimum(DependencyObject element, double value) => element.SetValue(MinimumProperty, value);
    public static double GetMinimum(DependencyObject element) => (double)element.GetValue(MinimumProperty);
    public static void SetMaximum(DependencyObject element, double value) => element.SetValue(MaximumProperty, value);
    public static double GetMaximum(DependencyObject element) => (double)element.GetValue(MaximumProperty);
    public static bool GetIsOutOfRange(DependencyObject element) => (bool)element.GetValue(IsOutOfRangeProperty);

    private static void OnConstraintChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs _)
    {
        if (dependencyObject is not TextBox textBox || (bool)textBox.GetValue(IsHookedProperty)) return;
        textBox.SetValue(IsHookedProperty, true);
        textBox.PreviewTextInput += RejectInvalidCharacter;
        textBox.TextChanged += ValidateDraft;
        textBox.LostKeyboardFocus += NormalizeRange;
        DataObject.AddPastingHandler(textBox, ValidatePaste);
        ValidateDraft(textBox, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
    }

    private static void RejectInvalidCharacter(object sender, TextCompositionEventArgs args)
    {
        if (sender is not TextBox textBox) return;
        args.Handled = args.Text.Any(character => !char.IsDigit(character) && character != '.') ||
            (args.Text.Contains('.') && textBox.Text.Contains('.'));
    }

    private static void ValidatePaste(object sender, DataObjectPastingEventArgs args)
    {
        var text = args.SourceDataObject.GetData(DataFormats.UnicodeText) as string;
        if (string.IsNullOrWhiteSpace(text) || !double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
            args.CancelCommand();
    }

    private static void NormalizeRange(object sender, KeyboardFocusChangedEventArgs _)
    {
        if (sender is not TextBox textBox) return;
        var minimum = GetMinimum(textBox);
        var maximum = Math.Max(minimum, GetMaximum(textBox));
        var value = double.TryParse(textBox.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : minimum;
        textBox.Text = Math.Clamp(value, minimum, maximum).ToString("0.##", CultureInfo.InvariantCulture);
        BindingOperations.GetBindingExpression(textBox, TextBox.TextProperty)?.UpdateSource();
        ValidateDraft(textBox, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
    }

    private static void ValidateDraft(object sender, TextChangedEventArgs _)
    {
        if (sender is not TextBox textBox) return;
        var minimum = GetMinimum(textBox);
        var maximum = Math.Max(minimum, GetMaximum(textBox));
        var valid = double.TryParse(textBox.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) &&
            double.IsFinite(value) && value >= minimum && value <= maximum;
        var invalid = !string.IsNullOrEmpty(textBox.Text) && !valid;
        textBox.SetValue(IsOutOfRangePropertyKey, invalid);
        AutomationProperties.SetHelpText(textBox, invalid ? $"请输入 {minimum:0.##}–{maximum:0.##} 范围内的数值" : string.Empty);
    }
}
