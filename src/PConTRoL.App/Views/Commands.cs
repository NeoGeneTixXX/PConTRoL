using System.Windows.Input;

namespace PConTRoL.App.Views;

public static class Commands
{
    public static readonly RoutedUICommand FullScreen = new("Vollbild", nameof(FullScreen), typeof(Commands));
    public static readonly RoutedUICommand Refresh = new("Aktualisieren", nameof(Refresh), typeof(Commands));
}
