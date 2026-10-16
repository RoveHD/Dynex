using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Dynex.Desktop.Games;
using Button = System.Windows.Controls.Button;

namespace Dynex.Desktop;

public partial class ApplicationRulesWindow : Window
{
    private readonly ObservableCollection<ApplicationRule> drafts;
    private readonly Func<ApplicationRule[], Task> save;
    private bool saving;

    public ApplicationRulesWindow(Func<IReadOnlyList<ApplicationRule>> read, Func<ApplicationRule[], Task> save)
    {
        this.save = save;
        drafts = new(ApplicationRules.Normalize(read()));
        InitializeComponent();
        RulesList.ItemsSource = drafts;
        UpdateCount();
    }

    private void UpdateCount() => CountText.Text = $"{drafts.Count} / {ApplicationRules.MaximumCount} Programmregeln";
    private static double? ReadRate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate))
            throw new ArgumentException("Limit als Zahl in Mbit/s eingeben, zum Beispiel 30 oder 5,5.");
        return rate;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rule = new ApplicationRule(ApplicationRules.NormalizeName(ProcessBox.Text), ReadRate(DownloadBox.Text), ReadRate(UploadBox.Text), EnabledBox.IsChecked == true);
            var selected = RulesList.SelectedItem as ApplicationRule;
            var next = drafts.ToList();
            var index = selected is null ? -1 : drafts.IndexOf(selected);
            if (index < 0) next.Add(rule); else next[index] = rule;
            ApplicationRules.Validate(next);
            if (index < 0) drafts.Add(rule); else drafts[index] = rule;
            RulesList.SelectedItem = rule;
            UpdateCount();
            InfoText.Text = "Regel übernommen. Mit „Einstellungen speichern“ aktivieren.";
        }
        catch (ArgumentException error) { InfoText.Text = error.Message; }
    }
    private void Rule_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (RemoveButton is null) return;
        RemoveButton.IsEnabled = RulesList.SelectedItem is ApplicationRule && !saving;
        if (RulesList.SelectedItem is not ApplicationRule rule) return;
        ProcessBox.Text = rule.ProcessName;
        DownloadBox.Text = rule.DownloadMbps?.ToString(CultureInfo.CurrentCulture) ?? "";
        UploadBox.Text = rule.UploadMbps?.ToString(CultureInfo.CurrentCulture) ?? "";
        EnabledBox.IsChecked = rule.Enabled;
    }
    private void New_Click(object sender, RoutedEventArgs e)
    {
        RulesList.SelectedItem = null; ProcessBox.Text = ""; DownloadBox.Text = ""; UploadBox.Text = ""; EnabledBox.IsChecked = true;
        ProcessBox.Focus();
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (RulesList.SelectedItem is not ApplicationRule rule) return;
        drafts.Remove(rule); New_Click(sender, e); UpdateCount();
        InfoText.Text = "Regel entfernt. Mit „Einstellungen speichern“ freigeben.";
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.IsEnabled = false;
        try
        {
            var typed = ProcessBox.Text;
            ProcessBox.ItemsSource = await Task.Run(GameDetector.GetRunningProcessNames);
            ProcessBox.Text = typed;
            InfoText.Text = "Programm auswählen, Limits eingeben und ausdrücklich hinzufügen.";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { InfoText.Text = "Programmliste konnte nicht geladen werden: " + error.Message; }
        finally { button.IsEnabled = true; }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (saving) return;
        saving = true; IsEnabled = false;
        try
        {
            var rules = ApplicationRules.Normalize(drafts);
            await save(rules);
            InfoText.Text = "Einstellungen gespeichert. Regeln gelten bei eingeschalteter Downloadbegrenzung außerhalb von Pausen und freier Nachtzeit.";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { InfoText.Text = "Speichern fehlgeschlagen: " + error.Message; }
        finally { saving = false; IsEnabled = true; }
    }
}
