using System.Windows;
using System.Windows.Threading;
using PConTRoL.App.Services;
using PConTRoL.App.ViewModels;
using PConTRoL.App.Views;

namespace PConTRoL.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayIcon? _tray;
    private MainWindow? _main;
    private PopupWindow? _popup;
    private bool _balloonShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\PConTRoL-SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("PConTRoL läuft bereits (siehe Symbol im Infobereich unten rechts).", "PConTRoL",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        var settings = SettingsStore.Load();
        var status = new QueueStatus();
        _popup = new PopupWindow(status);
        _popup.ApplyLook(settings);

        _main = new MainWindow(settings, status, _popup);
        _main.HiddenToTray += (_, _) =>
        {
            if (_balloonShown) return;
            _balloonShown = true;
            _tray?.ShowBalloon("PConTRoL läuft weiter", "Klick auf das Symbol zeigt die Maschinenliste. Beenden über Rechtsklick.");
        };
        _popup.OpenMainRequested += (_, _) => _main.ShowFromTray();

        _tray = new TrayIcon();
        _tray.OpenMain += (_, _) => _main.ShowFromTray();
        _tray.TogglePopup += (_, _) =>
        {
            if (_popup.IsVisible) _popup.Hide();
            else _popup.ShowAtCorner();
        };
        _tray.ShowPopup += (_, _) => _popup.ShowAtCorner();
        _tray.OpenSettings += (_, _) => _main.OpenSettings();
        _tray.Exit += (_, _) => ExitApp();
        status.PropertyChanged += (_, _) => _tray.SetToolTip("PConTRoL – läuft: " + status.CurrentName);

        var startInTray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (!startInTray) _main.Show();

        if (string.IsNullOrWhiteSpace(settings.EffectiveRoot))
        {
            if (!startInTray) _main.Show();
            _main.OpenSettings();
        }
    }

    private void ExitApp()
    {
        if (_main != null)
        {
            _main.CaptureLayout();
            SettingsStore.Save(_main.Settings);
            _main.AllowClose = true;
            _main.Close();
        }
        _popup?.Close();
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write("Unbehandelter Fehler", e.Exception);
        MessageBox.Show("Unerwarteter Fehler:\n" + e.Exception.Message + "\n\nDetails in " + SettingsStore.Folder + "\\fehler.log",
            "PConTRoL", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
