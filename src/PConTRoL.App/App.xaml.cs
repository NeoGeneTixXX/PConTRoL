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
    private UpdateService? _updates;
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

        if (string.IsNullOrWhiteSpace(settings.MainFolder))
        {
            if (!startInTray) _main.Show();
            _main.OpenSettings();
        }

        _updates = new UpdateService();
        _updates.UpdateReady += (_, asset) =>
        {
            var version = asset.Version.ToString();
            _main.ShowUpdateAvailable(version, asset.NotesMarkdown);
            _tray.ShowUpdateAvailable(version);
        };
        _main.UpdateRequested += (_, _) => ApplyUpdate(confirmed: true);
        _tray.UpdateRequested += (_, _) => ApplyUpdate(confirmed: false);
        _updates.Start();
    }

    /// <summary>
    /// Spielt das heruntergeladene Update ein: nur nach Bestätigung und nie während einer Dateiaktion.
    /// Schlägt der Start des Updaters fehl, läuft die alte Version einfach weiter.
    /// </summary>
    private void ApplyUpdate(bool confirmed)
    {
        var pending = _updates?.Pending;
        if (_main == null || pending == null) return;

        if (!confirmed)
        {
            _main.ShowFromTray();
            var notes = string.IsNullOrWhiteSpace(pending.NotesMarkdown) ? "" : "\n\n" + pending.NotesMarkdown.Trim();
            var answer = MessageBox.Show(_main,
                $"Neue Version {pending.Version}, jetzt aktualisieren?{notes}\n\nPConTRoL wird dazu kurz beendet und neu gestartet.",
                "PConTRoL: Update", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        if (_main.IsBusy)
        {
            MessageBox.Show(_main, "Gerade läuft noch eine Aktion. Bitte danach erneut auf „Jetzt aktualisieren“ klicken.",
                "PConTRoL: Update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_updates!.ApplyAndRestart())
        {
            MessageBox.Show(_main, $"Das Update konnte nicht gestartet werden. Version {UpdateService.CurrentVersion} läuft weiter.\n\nDetails in {SettingsStore.Folder}\\update.log",
                "PConTRoL: Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ExitApp();
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
