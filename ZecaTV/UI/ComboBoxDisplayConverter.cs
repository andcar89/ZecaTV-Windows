using System;
using System.Globalization;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;

namespace ZecaTV.UI
{
    /// <summary>
    /// Renders a ComboBox's selected item the same way DisplayMemberPath renders its dropdown rows.
    /// WPF normally does this itself via an internal SelectionBoxItem/SelectionBoxItemTemplate
    /// mechanism, but that mechanism doesn't reliably kick in inside a fully custom ComboBox
    /// ControlTemplate (ours, in Theme.xaml) — the closed box falls back to the raw item's
    /// ToString(), e.g. showing "ZecaTV.Models.SavedPlaylist" instead of the playlist's name. This
    /// reads the DisplayMemberPath property directly via reflection instead, sidestepping that
    /// internal machinery entirely. Used as a MultiBinding converter: values[0] is SelectedItem,
    /// values[1] is DisplayMemberPath, both TemplateBound from the ComboBox itself.
    /// </summary>
    public sealed class ComboBoxDisplayConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not { } item) return "";

            // A ComboBox populated with literal ComboBoxItem entries (Items.Add(new ComboBoxItem
            // { Content = ... }), e.g. the background-refresh interval picker) has SelectedItem be
            // the ComboBoxItem wrapper itself, not the underlying value — and ComboBoxItem.ToString()
            // prints its .NET type name ("System.Windows.Controls.ComboBoxItem"), not its Content.
            // Unwrap it first so the rest of this method always works with the actual displayed value.
            if (item is ComboBoxItem comboBoxItem) item = comboBoxItem.Content ?? item;

            var path = values[1] as string;
            if (string.IsNullOrEmpty(path)) return item.ToString() ?? "";

            var property = item.GetType().GetProperty(path, BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(item)?.ToString() ?? item.ToString() ?? "";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException("Read-only: a ComboBox's closed-box text is never edited back into SelectedItem/DisplayMemberPath.");
    }
}
