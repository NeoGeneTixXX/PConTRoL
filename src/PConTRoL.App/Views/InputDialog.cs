using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PConTRoL.App.Views;

/// <summary>Einfacher Dialog zur Texteingabe (z. B. Gruppenname).</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _box;

    private InputDialog(string title, string prompt, string value)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.Resources["PanelBg"];
        Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Fg"];
        ShowInTaskbar = false;

        _box = new TextBox { Text = value, FontSize = 18, Margin = new Thickness(0, 8, 0, 12) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 110 };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 110 };
        ok.Click += (_, _) => DialogResult = true;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = prompt, FontSize = 16, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_box);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) =>
        {
            _box.SelectAll();
            Keyboard.Focus(_box);
        };
    }

    public static string? Ask(Window? owner, string title, string prompt, string value = "")
    {
        var dlg = new InputDialog(title, prompt, value);
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg._box.Text) ? dlg._box.Text.Trim() : null;
    }
}
