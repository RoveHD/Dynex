using System.Windows;

namespace Dynex.Desktop;

internal sealed record UsageHistoryRow(string PcName, string DownloadText, string UploadText, string Gaps);

public partial class UsageHistoryWindow : Window
{
    private readonly UsageHistoryStore store;
    private bool week;

    internal UsageHistoryWindow(UsageHistoryStore store)
    {
        InitializeComponent();
        this.store = store;
        DatePicker.SelectedDate = DateTime.Today;
        RefreshRows();
    }

    private void Today_Click(object sender, RoutedEventArgs e) { week = false; DatePicker.SelectedDate = DateTime.Today; RefreshRows(); }
    private void Week_Click(object sender, RoutedEventArgs e) { week = true; RefreshRows(); }
    private void DatePicker_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => RefreshRows();

    private void RefreshRows()
    {
        if (UsageGrid is null || DatePicker?.SelectedDate is not { } selected) return;
        var end = DateOnly.FromDateTime(selected.Date);
        var start = week ? end.AddDays(-6) : end;
        var totals = store.Query(start, end);
        UsageGrid.ItemsSource = totals.Select(t => new UsageHistoryRow(t.PcName,
            t.DownloadGb is { } down ? $"{down:0.000}" : "—",
            t.UploadGb is { } up ? $"{up:0.000}" : "—",
            $"{FormatGap(t.DownloadGap)} / {FormatGap(t.UploadGap)}")).ToArray();
        var span = week ? $"{start:dd.MM.yyyy} – {end:dd.MM.yyyy}" : end.ToString("dd.MM.yyyy");
        InfoText.Text = store.LoadError is { } loadError
            ? $"{span} · Verlauf konnte nicht gelesen werden und wurde zum Schutz der vorhandenen Datei nicht überschrieben: {loadError}"
            : store.Error is { } writeError
                ? $"{span} · Speichern fehlgeschlagen; Werte bleiben bis zum nächsten Versuch im Arbeitsspeicher: {writeError}"
                : $"{span} · Ratenbasierte Schätzung aus kurzen Messintervallen; gespeichert spätestens alle 5 Minuten und beim Beenden. Lücken werden ausgelassen, offline Zeiten nicht hochgerechnet. Die Werte sind keine präzisen Gesamtzähler. " +
                  (store.RetentionLimited ? "Ältere Daten wurden wegen der Speichergrenze entfernt." : "Aufbewahrung: bis zu 90 Tage.") + " — bedeutet keine Messung.";
    }

    private static string FormatGap(TimeSpan? maybeValue) => maybeValue is not { } value ? "—" : value.TotalMinutes >= 1
        ? $"{(int)value.TotalHours} h {value.Minutes} min" : $"{(int)value.TotalSeconds} s";
}
