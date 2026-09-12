using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DraftLedger.App;

internal sealed record Field(string Label, string Value, string[]? Choices = null, bool Multiline = false, bool Secret = false, string? ToolTip = null);

internal static class Dialogs
{
    public static Dictionary<string, string>? Form(Window owner, string title, Field[] fields, Func<Dictionary<string, string>, string?>? validate = null)
    {
        var window = new Window { Owner = owner, Icon = owner.Icon, Title = title, Width = 510, SizeToContent = SizeToContent.Height, MaxHeight = Math.Max(500, SystemParameters.WorkArea.Height - 60), WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var layout = new DockPanel { Margin = new Thickness(24) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 85 };
        var save = new Button { Content = "Continue", IsDefault = true, MinWidth = 100, Style = (Style)owner.FindResource("PrimaryButton") };
        buttons.Children.Add(cancel); buttons.Children.Add(save); DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        var panel = new StackPanel(); var controls = new Dictionary<string, Control>();
        foreach (var field in fields)
        {
            panel.Children.Add(new TextBlock { Text = field.Label, Margin = new Thickness(0, 12, 0, 6) });
            Control control;
            if (field.Secret) control = new PasswordBox { Password = field.Value, Padding = new Thickness(8), MinHeight = 34 };
            else if (field.Choices is not null)
            {
                var combo = new ComboBox { ItemsSource = field.Choices, SelectedItem = field.Value, Padding = new Thickness(7), MinHeight = 32 };
                if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
                control = combo;
            }
            else control = new TextBox { Text = field.Value, AcceptsReturn = field.Multiline, TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = field.Multiline ? 95 : 34, MaxHeight = field.Multiline ? 160 : 40, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            System.Windows.Automation.AutomationProperties.SetName(control, field.Label);
            if (!string.IsNullOrWhiteSpace(field.ToolTip)) { control.ToolTip = field.ToolTip; if (panel.Children[^2] is FrameworkElement label) label.ToolTip = field.ToolTip; }
            controls[field.Label] = control; panel.Children.Add(control);
        }
        layout.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); window.Content = layout;
        Dictionary<string, string>? result = null;
        save.Click += (_, _) =>
        {
            var values = controls.ToDictionary(x => x.Key, x => x.Value switch { TextBox t => t.Text, PasswordBox p => p.Password, ComboBox c => c.SelectedItem?.ToString() ?? "", _ => "" });
            var error = validate?.Invoke(values);
            if (error is not null) { MessageBox.Show(window, error, title, MessageBoxButton.OK, MessageBoxImage.Information); return; }
            result = values; window.DialogResult = true;
        };
        window.Loaded += (_, _) => { controls.Values.FirstOrDefault()?.Focus(); if (controls.Values.FirstOrDefault() is TextBox t) t.SelectAll(); };
        window.ShowDialog(); return result;
    }

    public static string? Name(Window owner, string title, string value = "") => Form(owner, title, [new("Name", value)], v => string.IsNullOrWhiteSpace(v["Name"]) ? "Please enter a name." : null)?.GetValueOrDefault("Name")?.Trim();

    public static T? Select<T>(Window owner, string title, IReadOnlyList<T> items, Func<T, string> label, Func<T, string>? preview = null) where T : class
    {
        var window = new Window { Owner = owner, Icon = owner.Icon, Title = title, Width = 800, Height = 560, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var layout = new DockPanel { Margin = new Thickness(20) };
        var open = new Button { Content = "Open selected", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        DockPanel.SetDock(open, Dock.Bottom); layout.Children.Add(open);
        var list = new ListBox { ItemsSource = items.Select(label).ToList(), MinHeight = 130, MaxHeight = preview is null ? 450 : 200 };
        DockPanel.SetDock(list, Dock.Top); layout.Children.Add(list);
        if (preview is not null)
        {
            var text = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0) };
            layout.Children.Add(text);
            list.SelectionChanged += (_, _) => { if (list.SelectedIndex >= 0) { try { text.Text = preview(items[list.SelectedIndex]); } catch (Exception ex) { text.Text = ex.Message; } } };
        }
        T? result = null;
        void Choose() { if (list.SelectedIndex >= 0) { result = items[list.SelectedIndex]; window.DialogResult = true; } }
        open.Click += (_, _) => Choose(); list.MouseDoubleClick += (_, _) => Choose();
        window.Content = layout; if (items.Count > 0) list.SelectedIndex = 0;
        window.ShowDialog(); return result;
    }
}
