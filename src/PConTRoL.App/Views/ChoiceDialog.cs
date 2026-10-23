using System.Windows;
using System.Windows.Controls;

namespace PConTRoL.App.Views;

/// <summary>Einfacher Auswahldialog: Frage plus große Knöpfe, einer je Möglichkeit.</summary>
public sealed class ChoiceDialog : Window
{
    private int _choice = -1;

    private ChoiceDialog(string title, string question, IReadOnlyList<(string Text, string Hint)> options)
    {
        Title = title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (System.Windows.Media.Brush)Application.Current.Resources["PanelBg"];
        Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Fg"];
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = question, FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = options[i].Text, FontSize = 17, FontWeight = FontWeights.Bold });
            if (options[i].Hint.Length > 0)
                content.Children.Add(new TextBlock
                {
                    Text = options[i].Hint,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)Application.Current.Resources["FgDim"],
                });
            var button = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
            button.Click += (_, _) =>
            {
                _choice = index;
                DialogResult = true;
            };
            panel.Children.Add(button);
        }
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(cancel);
        Content = panel;
    }

    /// <summary>Index der gewählten Möglichkeit oder -1 bei Abbrechen.</summary>
    public static int Ask(Window? owner, string title, string question, params (string Text, string Hint)[] options)
    {
        var dlg = new ChoiceDialog(title, question, options);
        if (owner is { IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? dlg._choice : -1;
    }
}
