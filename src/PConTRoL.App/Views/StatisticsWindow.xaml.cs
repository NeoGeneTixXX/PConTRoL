using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using PConTRoL.App.Services;
using PConTRoL.Core.Statistics;

namespace PConTRoL.App.Views;

/// <summary>Anzeige der Plattenausnutzung je Material. Rechnet nicht selbst, sondern nutzt <see cref="StatisticsService"/>.</summary>
public partial class StatisticsWindow : Window
{
    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
    private readonly StatisticsStore _store;
    private IReadOnlyList<BoardRecord> _all = [];
    private IReadOnlyList<BoardRecord> _filtered = [];
    private bool _settingDates;

    public StatisticsWindow(StatisticsStore store, double scale)
    {
        InitializeComponent();
        _store = store;
        Icon = IconLoader.WindowIcon();
        if (Math.Abs(scale - 1) > 0.001) RootPanel.LayoutTransform = new ScaleTransform(scale, scale);
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        StatusText.Text = "Lese Statistik …";
        try
        {
            _all = await Task.Run(_store.Load);
            StatusText.Text = $"{_all.Count} Platten gespeichert in {_store.FilePath}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _all = [];
            StatusText.Text = "Statistik konnte nicht gelesen werden: " + ex.Message;
        }
        Render();
    }

    private void Render()
    {
        var (from, to) = CurrentRange();
        _filtered = StatisticsService.Filter(_all, from, to).ToList();
        var materials = StatisticsService.ByMaterial(_filtered);
        var total = StatisticsService.Total(_filtered);

        Tiles.ItemsSource = materials.Select(m => new Tile(m)).ToList();
        TotalText.Text = total.Boards == 0
            ? "Keine Platten im gewählten Zeitraum"
            : $"Gesamt: {total.Boards} Platten · {materials.Count} Materialien · Ausnutzung {Pct(total.Utilization)} · Verschnitt {Pct(total.Waste)}";
        EmptyText.Text = _all.Count == 0
            ? "Noch keine fertigen Platten erfasst.\n\nPConTRoL zählt jede Platte, die die Maschine fertig bearbeitet hat. "
              + "Bereits bearbeitete Platten lassen sich unter Einstellungen → Statistik aus dem Archivordner einlesen."
            : "Im gewählten Zeitraum wurden keine Platten bearbeitet.";
        EmptyText.Visibility = materials.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Zeitraum als UTC-Grenzen [von, bis). Die Datumsfelder sind inklusive (bis = ganzer Tag).</summary>
    private (DateTime? From, DateTime? To) CurrentRange()
    {
        DateTime? from = FromPicker.SelectedDate?.Date;
        DateTime? to = ToPicker.SelectedDate?.Date.AddDays(1);
        return (from?.ToUniversalTime(), to?.ToUniversalTime());
    }

    private void OnRangeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || RangeBox.SelectedItem is not ComboBoxItem { Tag: string tag } || tag == "custom") return;

        var today = DateTime.Today;
        var month = new DateTime(today.Year, today.Month, 1);
        (DateTime? from, DateTime? to) = tag switch
        {
            "today" => (today, today),
            "7d" => (today.AddDays(-6), today),
            "30d" => (today.AddDays(-29), today),
            "month" => (month, today),
            "lastmonth" => (month.AddMonths(-1), month.AddDays(-1)),
            "year" => (new DateTime(today.Year, 1, 1), today),
            "lastyear" => (new DateTime(today.Year - 1, 1, 1), new DateTime(today.Year - 1, 12, 31)),
            _ => ((DateTime?)null, (DateTime?)null),
        };

        _settingDates = true;
        FromPicker.SelectedDate = from;
        ToPicker.SelectedDate = to;
        _settingDates = false;
        Render();
    }

    private void OnDateChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _settingDates) return;
        RangeBox.SelectedItem = RangeBox.Items.OfType<ComboBoxItem>().First(i => (string)i.Tag == "custom");
        Render();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void OnExportSummary(object sender, RoutedEventArgs e) =>
        Export("Statistik_Uebersicht", w => StatisticsCsv.WriteSummary(w, StatisticsService.ByMaterial(_filtered), StatisticsService.Total(_filtered)));

    private void OnExportBoards(object sender, RoutedEventArgs e) =>
        Export("Statistik_Platten", w => StatisticsCsv.WriteBoards(w, _filtered));

    private void Export(string baseName, Action<TextWriter> write)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Statistik als CSV speichern",
            Filter = "CSV-Datei (Excel)|*.csv",
            FileName = $"{baseName}_{DateTime.Now:yyyy-MM-dd}.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            // UTF-8 mit BOM, damit Excel Umlaute und „m²“ richtig anzeigt.
            using var writer = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true));
            write(writer);
            StatusText.Text = "Gespeichert: " + dlg.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Speichern fehlgeschlagen: " + ex.Message, "PConTRoL", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Pct(double v) => v.ToString("0.0", De) + " %";

    /// <summary>Anzeigedaten einer Material-Kachel.</summary>
    private sealed class Tile(MaterialStatistics m)
    {
        public string Material => m.Material;
        public double Utilization => m.Utilization;
        public int Boards => m.Boards;
        public string UtilizationText => Pct(m.Utilization);
        public string WasteText => Pct(m.Waste);
        public string AreaText =>
            $"Plattenfläche {m.BoardAreaM2.ToString("#,0.0", De)} m² · genutzt {m.UsedAreaM2.ToString("#,0.0", De)} m² · Verschnitt {m.WasteAreaM2.ToString("#,0.0", De)} m²";
    }
}
