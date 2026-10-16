using System.Windows;
using System.Windows.Controls;

namespace Dynex.Desktop;

public partial class NetworkProfilesWindow : Window
{
    private readonly DynexRuntime runtime;
    public NetworkProfilesWindow(DynexRuntime runtime)
    {
        InitializeComponent(); this.runtime = runtime; RefreshProfiles();
    }
    private sealed record Row(string Id, string Name, string Download, string Upload, string Profile, string LastUsed);
    private void RefreshProfiles()
    {
        ActiveText.Text = runtime.NetworkProfileStatus;
        ProfilesGrid.ItemsSource = runtime.Settings.NetworkProfiles.OrderByDescending(p => p.LastUsedUtc).Select(p => new Row(p.Id,
            p.Name + (p.Id == runtime.Settings.ActiveNetworkProfileId ? " · aktiv" : ""),
            p.Policy.CapacityMeasuredAtUtc is null ? "Noch nicht gemessen" : $"{p.Policy.CapacityMbps:0.##} Mbit/s",
            p.Policy.UploadCapacityMbps is { } upload ? $"{upload:0.##} Mbit/s" : "Nicht gemessen",
            p.Policy.Profile, p.LastUsedUtc.ToLocalTime().ToString("g"))).ToArray();
        if (ProfilesGrid.Items.Count == 0) StatusText.Text = "Noch kein Windows-Netzwerk eindeutig erkannt. Es wurde kein Profil erfunden.";
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is Row row) NameBox.Text = runtime.Settings.NetworkProfiles.First(p => p.Id == row.Id).Name;
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshProfiles();
    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesGrid.SelectedItem is not Row row) { StatusText.Text = "Zuerst ein Netzwerk auswählen."; return; }
        try { await runtime.RenameNetworkProfileAsync(row.Id, NameBox.Text); RefreshProfiles(); StatusText.Text = "Netzwerkname gespeichert."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
}
