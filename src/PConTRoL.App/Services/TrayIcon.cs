using WinForms = System.Windows.Forms;

namespace PConTRoL.App.Services;

/// <summary>
/// Symbol im Infobereich. Einfacher Klick: Pop-up an/aus. Doppelklick: Hauptfenster.
/// Rechtsklick: Menü mit Beenden.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly System.Windows.Threading.DispatcherTimer _clickTimer;

    public TrayIcon()
    {
        _clickTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(WinForms.SystemInformation.DoubleClickTime),
        };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            TogglePopup?.Invoke(this, EventArgs.Empty);
        };

        var menu = new WinForms.ContextMenuStrip { Font = new System.Drawing.Font("Segoe UI", 11f) };
        menu.Items.Add("Hauptfenster öffnen", null, (_, _) => OpenMain?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Pop-up anzeigen", null, (_, _) => ShowPopup?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Einstellungen …", null, (_, _) => OpenSettings?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => Exit?.Invoke(this, EventArgs.Empty));

        _icon = new WinForms.NotifyIcon
        {
            Icon = IconLoader.TrayIcon(),
            Text = "PConTRoL – Produktionsreihenfolge",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != WinForms.MouseButtons.Left) return;
            _clickTimer.Stop();
            _clickTimer.Start();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != WinForms.MouseButtons.Left) return;
            _clickTimer.Stop();
            OpenMain?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? OpenMain;
    public event EventHandler? TogglePopup;
    public event EventHandler? ShowPopup;
    public event EventHandler? OpenSettings;
    public event EventHandler? Exit;

    public void SetToolTip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(3000, title, text, WinForms.ToolTipIcon.Info);

    public void Dispose()
    {
        _clickTimer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
