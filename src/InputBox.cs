using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vertushka;

public static class InputBox
{
    public static string Ask(Window owner, string title, string label, string def = "")
    {
        var w = new Window
        {
            Title = title, Owner = owner, Width = 400, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = owner.Topmost,
            Background = (Brush)Application.Current.Resources["MenuBg"]
        };
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = label, Foreground = (Brush)Application.Current.Resources["Text"], Margin = new Thickness(0, 0, 0, 8) });
        var tb = new TextBox { Text = def, Padding = new Thickness(8), FontSize = 15 };
        root.Children.Add(tb);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "Готово", IsDefault = true, Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.Resources["AccentBtn"] };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(18, 8, 18, 8), Style = (Style)Application.Current.Resources["Flat"] };
        string result = null;
        ok.Click += (s, e) => { result = tb.Text.Trim(); w.DialogResult = true; };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        root.Children.Add(row);
        w.Content = root;
        w.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
        w.ShowDialog();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
